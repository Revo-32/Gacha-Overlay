using System.Threading.Channels;
using GachaOverlay.Core.Sales;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Sales;

internal enum SalesResumeDisposition
{
    Resumable,
    Inactive,
    WrongGeneration,
    HistoryExpired,
    FutureSequence,
}

internal sealed record SalesBootstrapCapture(string Generation, long StartSequence);

internal sealed record SalesBootstrapCompletion(
    SalesResumeDisposition Disposition,
    string Generation,
    long LatestSequence,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<SalesCompletionObservation> Observations);

internal sealed record SalesResumeResult(
    SalesResumeDisposition Disposition,
    SalesStreamSubscription? Subscription,
    string? Generation,
    long LatestSequence);

internal sealed class SalesStreamSubscription : IAsyncDisposable
{
    private readonly ActiveSalesStreamRegistry _owner;
    private int _disposed;

    internal SalesStreamSubscription(
        ActiveSalesStreamRegistry owner,
        Guid id,
        IReadOnlyList<SalesMutationEnvelope> replay,
        Channel<SalesMutationEnvelope> channel)
    {
        _owner = owner;
        Id = id;
        Replay = replay;
        Reader = channel.Reader;
        Writer = channel.Writer;
    }

    internal Guid Id { get; }
    internal ChannelWriter<SalesMutationEnvelope> Writer { get; }
    public IReadOnlyList<SalesMutationEnvelope> Replay { get; }
    public ChannelReader<SalesMutationEnvelope> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Remove(Id);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ActiveSalesStreamRegistry
{
    public const int JournalCapacity = 256;
    public const int OutboundCapacity = 256;
    public const int AuthoritativeWindowSize = AuthoritativeSalesWindow.Size;

    private readonly object _sync = new();
    private readonly ulong _channelId;
    private readonly Dictionary<ulong, ChatMessage> _messages = new();
    private readonly Dictionary<ulong, SalesCompletionObservation> _observations = new();
    private readonly List<SalesMutationEnvelope> _journal = new(JournalCapacity);
    private readonly Dictionary<Guid, SalesStreamSubscription> _subscriptions = new();
    private string _generation = Guid.NewGuid().ToString("N");
    private long _sequence;
    private bool _active;

    public ActiveSalesStreamRegistry(Configuration.BackendConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _channelId = configuration.SalesChannelId;
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public bool IsCurrentGeneration(string generation)
    {
        lock (_sync)
        {
            return _active &&
                !string.IsNullOrWhiteSpace(generation) &&
                string.Equals(generation, _generation, StringComparison.Ordinal);
        }
    }

    public SalesBootstrapCapture Activate()
    {
        lock (_sync)
        {
            _active = true;
            return new SalesBootstrapCapture(_generation, _sequence);
        }
    }

    public SalesBootstrapCompletion CompleteBootstrap(
        SalesBootstrapCapture capture,
        IReadOnlyCollection<ChatMessage> messages,
        IReadOnlyCollection<SalesCompletionObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(observations);
        lock (_sync)
        {
            if (!_active)
            {
                return Empty(SalesResumeDisposition.Inactive, capture.Generation);
            }

            if (!string.Equals(capture.Generation, _generation, StringComparison.Ordinal))
            {
                return Empty(SalesResumeDisposition.WrongGeneration, _generation);
            }

            if (_journal.Count > 0 && capture.StartSequence < _journal[0].Sequence - 1)
            {
                return Empty(SalesResumeDisposition.HistoryExpired, _generation);
            }

            _messages.Clear();
            _observations.Clear();
            foreach (var message in messages
                         .OrderBy(item => item.CreatedAt)
                         .ThenBy(item => item.MessageId)
                         .TakeLast(AuthoritativeWindowSize))
            {
                _messages[message.MessageId] = message;
            }

            foreach (var observation in observations.Where(item =>
                         _messages.ContainsKey(item.MessageId)))
            {
                _observations[observation.MessageId] = observation;
            }

            foreach (var mutation in _journal.Where(item =>
                         item.Sequence > capture.StartSequence))
            {
                Apply(mutation);
            }

            TrimMessages();
            return Snapshot(SalesResumeDisposition.Resumable);
        }
    }

    public bool PublishUpsert(
        string eventType,
        ChatMessage message,
        SalesCompletionObservation observation)
    {
        if (eventType is not (OverlayTransportProtocol.SalesMessageCreate or
            OverlayTransportProtocol.SalesMessageUpdate))
        {
            throw new ArgumentException("Unsupported sales upsert event type.", nameof(eventType));
        }

        lock (_sync)
        {
            if (!_active || message.ChannelId != _channelId)
            {
                return false;
            }

            if (_messages.TryGetValue(message.MessageId, out var existing))
            {
                message = message with { CreatedAt = existing.CreatedAt };
            }

            _messages[message.MessageId] = message;
            _observations[message.MessageId] = observation;
            TrimMessages();
            Publish(NewEnvelope(eventType, message.MessageId, message, observation));
            return true;
        }
    }

    public bool PublishEvidence(SalesCompletionObservation observation)
    {
        lock (_sync)
        {
            if (!_active || !_messages.ContainsKey(observation.MessageId))
            {
                return false;
            }

            _observations[observation.MessageId] = observation;
            Publish(NewEnvelope(
                OverlayTransportProtocol.SalesCompletionEvidence,
                observation.MessageId,
                null,
                observation));
            return true;
        }
    }

    public bool PublishDelete(ulong channelId, ulong messageId)
    {
        lock (_sync)
        {
            if (!_active || channelId != _channelId)
            {
                return false;
            }

            _messages.Remove(messageId);
            _observations.Remove(messageId);
            Publish(NewEnvelope(
                OverlayTransportProtocol.SalesMessageDelete,
                messageId,
                null,
                null));
            return true;
        }
    }

    public bool PublishResyncRequired()
    {
        lock (_sync)
        {
            if (!_active)
            {
                return false;
            }

            Publish(NewEnvelope(
                OverlayTransportProtocol.SalesResyncRequired,
                0,
                null,
                null));
            return true;
        }
    }

    public SalesResumeResult PrepareResume(string generation, long afterSequence)
    {
        lock (_sync)
        {
            if (!_active)
            {
                return new SalesResumeResult(SalesResumeDisposition.Inactive, null, null, 0);
            }

            if (!string.Equals(generation, _generation, StringComparison.Ordinal))
            {
                return new SalesResumeResult(
                    SalesResumeDisposition.WrongGeneration,
                    null,
                    _generation,
                    _sequence);
            }

            if (afterSequence < 0 || afterSequence > _sequence)
            {
                return new SalesResumeResult(
                    SalesResumeDisposition.FutureSequence,
                    null,
                    _generation,
                    _sequence);
            }

            if (_journal.Count > 0 && afterSequence < _journal[0].Sequence - 1)
            {
                return new SalesResumeResult(
                    SalesResumeDisposition.HistoryExpired,
                    null,
                    _generation,
                    _sequence);
            }

            var cutoff = _sequence;
            var replay = _journal.Where(item => item.Sequence > afterSequence &&
                item.Sequence <= cutoff).ToArray();
            var channel = Channel.CreateBounded<SalesMutationEnvelope>(
                new BoundedChannelOptions(OutboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
            var id = Guid.NewGuid();
            var subscription = new SalesStreamSubscription(this, id, replay, channel);
            _subscriptions.Add(id, subscription);
            return new SalesResumeResult(
                SalesResumeDisposition.Resumable,
                subscription,
                _generation,
                cutoff);
        }
    }

    internal void Remove(Guid id)
    {
        lock (_sync)
        {
            if (_subscriptions.Remove(id, out var subscription))
            {
                subscription.Writer.TryComplete();
            }
        }
    }

    private SalesMutationEnvelope NewEnvelope(
        string eventType,
        ulong messageId,
        ChatMessage? message,
        SalesCompletionObservation? observation) => new(
            OverlayTransportProtocol.Version,
            _generation,
            checked(++_sequence),
            eventType,
            _channelId,
            messageId,
            message,
            observation);

    private void Publish(SalesMutationEnvelope envelope)
    {
        if (_journal.Count == JournalCapacity)
        {
            _journal.RemoveAt(0);
        }

        _journal.Add(envelope);
        foreach (var id in _subscriptions.Where(pair =>
                     !pair.Value.Writer.TryWrite(envelope)).Select(pair => pair.Key).ToArray())
        {
            if (_subscriptions.Remove(id, out var subscription))
            {
                subscription.Writer.TryComplete(new InvalidOperationException(
                    "Slow sales client outbound queue is full."));
            }
        }
    }

    private void Apply(SalesMutationEnvelope mutation)
    {
        if (mutation.EventType == OverlayTransportProtocol.SalesMessageDelete)
        {
            _messages.Remove(mutation.MessageId);
            _observations.Remove(mutation.MessageId);
            return;
        }

        if (mutation.Message is not null)
        {
            _messages[mutation.MessageId] = mutation.Message;
        }

        if (mutation.CompletionObservation is not null)
        {
            _observations[mutation.MessageId] = mutation.CompletionObservation;
        }
    }

    private void TrimMessages()
    {
        foreach (var id in _messages.Values
                     .OrderByDescending(item => item.CreatedAt)
                     .ThenByDescending(item => item.MessageId)
                     .Skip(AuthoritativeWindowSize)
                     .Select(item => item.MessageId)
                     .ToArray())
        {
            _messages.Remove(id);
            _observations.Remove(id);
        }
    }

    private SalesBootstrapCompletion Snapshot(SalesResumeDisposition disposition) => new(
        disposition,
        _generation,
        _sequence,
        _messages.Values.OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.MessageId).ToArray(),
        _observations.Values.OrderBy(item => item.MessageId).ToArray());

    private SalesBootstrapCompletion Empty(
        SalesResumeDisposition disposition,
        string generation) => new(
            disposition,
            generation,
            _sequence,
            Array.Empty<ChatMessage>(),
            Array.Empty<SalesCompletionObservation>());
}
