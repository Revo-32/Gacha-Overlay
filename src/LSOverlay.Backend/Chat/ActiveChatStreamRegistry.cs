using System.Threading.Channels;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Chat;

internal enum ChatResumeDisposition
{
    Resumable,
    ChannelInactive,
    WrongGeneration,
    HistoryExpired,
    FutureSequence,
}

internal sealed record ChatBootstrapCapture(
    ulong ChannelId,
    string Generation,
    long StartSequence);

internal sealed record ChatBootstrapCompletion(
    ChatResumeDisposition Disposition,
    string Generation,
    long LatestSequence,
    IReadOnlyList<ChatMessage> Messages);

internal sealed record ChatResumeResult(
    ChatResumeDisposition Disposition,
    ChatStreamSubscription? Subscription,
    string? Generation,
    long LatestSequence);

internal sealed class ChatStreamSubscription : IAsyncDisposable
{
    private readonly ActiveChatStreamRegistry _owner;
    private int _disposed;

    internal ChatStreamSubscription(
        ActiveChatStreamRegistry owner,
        ulong channelId,
        Guid id,
        IReadOnlyList<ChatMutationEnvelope> replay,
        Channel<ChatMutationEnvelope> channel)
    {
        _owner = owner;
        ChannelId = channelId;
        Id = id;
        Replay = replay;
        Reader = channel.Reader;
        Writer = channel.Writer;
    }

    internal ulong ChannelId { get; }
    internal Guid Id { get; }
    internal ChannelWriter<ChatMutationEnvelope> Writer { get; }
    public IReadOnlyList<ChatMutationEnvelope> Replay { get; }
    public ChannelReader<ChatMutationEnvelope> Reader { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _owner.Remove(ChannelId, Id);
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ActiveChatStreamRegistry
{
    public const int MaximumActiveChannels = 16;
    public const int JournalCapacity = 128;
    public const int OutboundCapacity = 256;
    public const int BootstrapMessageLimit = 20;
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(10);

    private sealed class StreamState
    {
        public required ChatChannelDescriptor Descriptor { get; set; }
        public required string Generation { get; init; }
        public long Sequence { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public List<ChatMutationEnvelope> Journal { get; } = new(JournalCapacity);
        public Dictionary<ulong, ChatMessage> Messages { get; } = new();
        public Dictionary<Guid, ChatStreamSubscription> Subscriptions { get; } = new();
    }

    private readonly object _sync = new();
    private readonly Dictionary<ulong, StreamState> _streams = new();
    private readonly Func<DateTimeOffset> _clock;

    public ActiveChatStreamRegistry()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    internal ActiveChatStreamRegistry(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public int ActiveChannelCount
    {
        get
        {
            lock (_sync)
            {
                return _streams.Count;
            }
        }
    }

    public bool IsActive(ulong channelId)
    {
        lock (_sync)
        {
            return _streams.ContainsKey(channelId);
        }
    }

    public ChatBootstrapCapture Activate(ChatChannelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_sync)
        {
            EvictIdleCore();
            if (!_streams.TryGetValue(descriptor.ChannelId, out var stream))
            {
                if (_streams.Count >= MaximumActiveChannels)
                {
                    var candidate = _streams.Values
                        .Where(item => item.Subscriptions.Count == 0)
                        .MinBy(item => item.LastUsed);
                    if (candidate is null)
                    {
                        throw new InvalidOperationException(
                            "Active chat channel capacity is exhausted.");
                    }

                    _streams.Remove(candidate.Descriptor.ChannelId);
                }

                stream = new StreamState
                {
                    Descriptor = descriptor,
                    Generation = Guid.NewGuid().ToString("N"),
                    LastUsed = _clock(),
                };
                _streams.Add(descriptor.ChannelId, stream);
            }
            else
            {
                stream.Descriptor = descriptor;
                stream.LastUsed = _clock();
            }

            return new ChatBootstrapCapture(
                descriptor.ChannelId,
                stream.Generation,
                stream.Sequence);
        }
    }

    public ChatBootstrapCompletion CompleteBootstrap(
        ChatBootstrapCapture capture,
        IReadOnlyCollection<ChatMessage> recentMessages)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(recentMessages);
        lock (_sync)
        {
            if (!_streams.TryGetValue(capture.ChannelId, out var stream))
            {
                return new ChatBootstrapCompletion(
                    ChatResumeDisposition.ChannelInactive,
                    capture.Generation,
                    0,
                    Array.Empty<ChatMessage>());
            }

            if (!string.Equals(stream.Generation, capture.Generation, StringComparison.Ordinal))
            {
                return new ChatBootstrapCompletion(
                    ChatResumeDisposition.WrongGeneration,
                    stream.Generation,
                    stream.Sequence,
                    Array.Empty<ChatMessage>());
            }

            if (stream.Journal.Count > 0 &&
                capture.StartSequence < stream.Journal[0].Sequence - 1)
            {
                return new ChatBootstrapCompletion(
                    ChatResumeDisposition.HistoryExpired,
                    stream.Generation,
                    stream.Sequence,
                    Array.Empty<ChatMessage>());
            }

            stream.Messages.Clear();
            foreach (var message in recentMessages
                         .OrderBy(message => message.CreatedAt)
                         .ThenBy(message => message.MessageId)
                         .TakeLast(BootstrapMessageLimit))
            {
                stream.Messages[message.MessageId] = message;
            }

            foreach (var mutation in stream.Journal.Where(item =>
                         item.Sequence > capture.StartSequence))
            {
                Apply(stream.Messages, mutation);
            }

            TrimMessages(stream.Messages);
            stream.LastUsed = _clock();
            return new ChatBootstrapCompletion(
                ChatResumeDisposition.Resumable,
                stream.Generation,
                stream.Sequence,
                SnapshotMessages(stream.Messages));
        }
    }

    public bool PublishUpsert(string eventType, ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (eventType is not (OverlayTransportProtocol.ChatMessageCreate or
            OverlayTransportProtocol.ChatMessageUpdate))
        {
            throw new ArgumentException("Unsupported chat upsert event type.", nameof(eventType));
        }

        lock (_sync)
        {
            if (!_streams.TryGetValue(message.ChannelId, out var stream))
            {
                return false;
            }

            if (eventType == OverlayTransportProtocol.ChatMessageUpdate &&
                stream.Messages.TryGetValue(message.MessageId, out var existing))
            {
                message = message with { CreatedAt = existing.CreatedAt };
            }

            var envelope = NewEnvelope(stream, eventType, message.MessageId, message);
            stream.Messages[message.MessageId] = message;
            TrimMessages(stream.Messages);
            PublishCore(stream, envelope);
            return true;
        }
    }

    public bool PublishDelete(ulong channelId, ulong messageId)
    {
        lock (_sync)
        {
            if (!_streams.TryGetValue(channelId, out var stream))
            {
                return false;
            }

            var envelope = NewEnvelope(
                stream,
                OverlayTransportProtocol.ChatMessageDelete,
                messageId,
                null);
            stream.Messages.Remove(messageId);
            PublishCore(stream, envelope);
            return true;
        }
    }

    public bool PublishResyncRequired(ulong channelId)
    {
        lock (_sync)
        {
            if (!_streams.TryGetValue(channelId, out var stream))
            {
                return false;
            }

            PublishCore(stream, NewEnvelope(
                stream,
                OverlayTransportProtocol.ChatResyncRequired,
                0,
                null));
            return true;
        }
    }

    public bool RemoveChannel(ulong channelId)
    {
        lock (_sync)
        {
            if (!_streams.TryGetValue(channelId, out var stream))
            {
                return false;
            }

            var envelope = NewEnvelope(
                stream,
                OverlayTransportProtocol.ChatChannelUnavailable,
                0,
                null);
            PublishCore(stream, envelope);
            _streams.Remove(channelId);
            foreach (var subscription in stream.Subscriptions.Values)
            {
                subscription.Writer.TryComplete();
            }

            stream.Subscriptions.Clear();
            return true;
        }
    }

    public ChatResumeResult PrepareResume(
        ulong channelId,
        string generation,
        long afterSequence)
    {
        lock (_sync)
        {
            if (!_streams.TryGetValue(channelId, out var stream))
            {
                return new ChatResumeResult(
                    ChatResumeDisposition.ChannelInactive,
                    null,
                    null,
                    0);
            }

            if (!string.Equals(generation, stream.Generation, StringComparison.Ordinal))
            {
                return new ChatResumeResult(
                    ChatResumeDisposition.WrongGeneration,
                    null,
                    stream.Generation,
                    stream.Sequence);
            }

            if (afterSequence < 0 || afterSequence > stream.Sequence)
            {
                return new ChatResumeResult(
                    ChatResumeDisposition.FutureSequence,
                    null,
                    stream.Generation,
                    stream.Sequence);
            }

            if (stream.Journal.Count > 0 &&
                afterSequence < stream.Journal[0].Sequence - 1)
            {
                return new ChatResumeResult(
                    ChatResumeDisposition.HistoryExpired,
                    null,
                    stream.Generation,
                    stream.Sequence);
            }

            var cutoff = stream.Sequence;
            var replay = stream.Journal.Where(item =>
                item.Sequence > afterSequence && item.Sequence <= cutoff).ToArray();
            var channel = Channel.CreateBounded<ChatMutationEnvelope>(
                new BoundedChannelOptions(OutboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });
            var id = Guid.NewGuid();
            var subscription = new ChatStreamSubscription(
                this,
                channelId,
                id,
                replay,
                channel);
            stream.Subscriptions.Add(id, subscription);
            stream.LastUsed = _clock();
            return new ChatResumeResult(
                ChatResumeDisposition.Resumable,
                subscription,
                stream.Generation,
                cutoff);
        }
    }

    public void EvictIdle()
    {
        lock (_sync)
        {
            EvictIdleCore();
        }
    }

    internal void Remove(ulong channelId, Guid id)
    {
        lock (_sync)
        {
            if (_streams.TryGetValue(channelId, out var stream) &&
                stream.Subscriptions.Remove(id, out var subscription))
            {
                subscription.Writer.TryComplete();
                stream.LastUsed = _clock();
            }
        }
    }

    private static ChatMutationEnvelope NewEnvelope(
        StreamState stream,
        string eventType,
        ulong messageId,
        ChatMessage? message) => new(
            OverlayTransportProtocol.Version,
            stream.Generation,
            checked(++stream.Sequence),
            eventType,
            stream.Descriptor.ChannelId,
            messageId,
            message);

    private static void PublishCore(StreamState stream, ChatMutationEnvelope envelope)
    {
        if (stream.Journal.Count == JournalCapacity)
        {
            stream.Journal.RemoveAt(0);
        }

        stream.Journal.Add(envelope);
        List<Guid>? slow = null;
        foreach (var pair in stream.Subscriptions)
        {
            if (!pair.Value.Writer.TryWrite(envelope))
            {
                (slow ??= new List<Guid>()).Add(pair.Key);
            }
        }

        if (slow is not null)
        {
            foreach (var id in slow)
            {
                if (stream.Subscriptions.Remove(id, out var subscription))
                {
                    subscription.Writer.TryComplete(new InvalidOperationException(
                        "Slow chat client outbound queue is full."));
                }
            }
        }
    }

    private static void Apply(
        IDictionary<ulong, ChatMessage> messages,
        ChatMutationEnvelope mutation)
    {
        if (mutation.EventType == OverlayTransportProtocol.ChatMessageDelete)
        {
            messages.Remove(mutation.MessageId);
        }
        else if (mutation.Message is not null)
        {
            messages[mutation.MessageId] = mutation.Message;
        }
    }

    private static void TrimMessages(IDictionary<ulong, ChatMessage> messages)
    {
        foreach (var id in messages.Values
                     .OrderByDescending(message => message.CreatedAt)
                     .ThenByDescending(message => message.MessageId)
                     .Skip(BootstrapMessageLimit)
                     .Select(message => message.MessageId)
                     .ToArray())
        {
            messages.Remove(id);
        }
    }

    private static IReadOnlyList<ChatMessage> SnapshotMessages(
        IDictionary<ulong, ChatMessage> messages) => messages.Values
        .OrderBy(message => message.CreatedAt)
        .ThenBy(message => message.MessageId)
        .ToArray();

    private void EvictIdleCore()
    {
        var threshold = _clock() - IdleLifetime;
        foreach (var channelId in _streams
                     .Where(pair => pair.Value.Subscriptions.Count == 0 &&
                         pair.Value.LastUsed <= threshold)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _streams.Remove(channelId);
        }
    }
}
