using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Gta;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.Transport;

internal sealed class BackendWebSocketSession
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(75);

    private readonly RemotePublicationHub _publication;
    private readonly TransportMetrics _metrics;
    private readonly RemoteChatService? _chat;
    private readonly RemoteSalesService? _sales;
    private readonly GtaEventService? _gtaEvents;

    public BackendWebSocketSession(
        RemotePublicationHub publication,
        TransportMetrics metrics,
        RemoteChatService? chat = null,
        RemoteSalesService? sales = null,
        GtaEventService? gtaEvents = null)
    {
        _publication = publication;
        _metrics = metrics;
        _chat = chat;
        _sales = sales;
        _gtaEvents = gtaEvents;
    }

    public async Task RunAsync(
        WebSocket socket,
        AuthenticatedClientIdentity identity,
        CancellationToken cancellationToken)
    {
        StreamClientMessage? resume;
        try
        {
            resume = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError,
                "Invalid WebSocket control message.", cancellationToken).ConfigureAwait(false);
            return;
        }
        if (resume is null || resume.Type != OverlayTransportProtocol.Resume ||
            resume.Generation is null || resume.AfterSequence is null)
        {
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError,
                "A version 1 resume request is required.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        RemoteResumeResult prepared;
        try
        {
            OverlayProtocolJson.EnsureVersion(resume.ProtocolVersion);
            prepared = _publication.PrepareResume(
                resume.Generation,
                resume.AfterSequence.Value);
        }
        catch (NotSupportedException)
        {
            await CloseAsync(socket, WebSocketCloseStatus.ProtocolError,
                "Unsupported protocol version.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (prepared.Disposition != ResumeDisposition.Resumable ||
            prepared.Subscription is null)
        {
            _metrics.Increment(TransportMetric.ResyncRequired);
            await SendAsync(socket, new StreamServerMessage(
                OverlayTransportProtocol.Version,
                OverlayTransportProtocol.ResyncRequired,
                _publication.Generation,
                prepared.LatestSequence,
                Reason: prepared.Disposition.ToString()), cancellationToken).ConfigureAwait(false);
            await CloseAsync(socket, WebSocketCloseStatus.NormalClosure,
                "Bootstrap required.", cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var subscription = prepared.Subscription;
        foreach (var item in subscription.Replay)
        {
            await SendAsync(socket, EventMessage(item), cancellationToken).ConfigureAwait(false);
            _metrics.Increment(TransportMetric.ReplayEventsSent);
        }

        await SendAsync(socket, new StreamServerMessage(
            OverlayTransportProtocol.Version,
            OverlayTransportProtocol.Live,
            _publication.Generation,
            prepared.LatestSequence), cancellationToken).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var outbound = Channel.CreateBounded<StreamServerMessage>(
            new BoundedChannelOptions(RemotePublicationHub.DefaultOutboundCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        var heartbeatState = new HeartbeatState();
        await using var chatState = new ChatConnectionState(
            _chat,
            identity,
            outbound.Writer);
        await using var salesState = new SalesConnectionState(
            _sales,
            identity,
            outbound.Writer);
        using var gtaState = new GtaCompanionConnectionState(
            _gtaEvents,
            SupportsGtaCompanion(resume),
            outbound.Writer);
        var forward = ForwardEventsAsync(subscription, outbound.Writer, linked.Token);
        var heartbeat = ProduceHeartbeatsAsync(
            outbound.Writer,
            heartbeatState,
            chatState,
            salesState,
            linked.Token);
        var send = SendLoopAsync(socket, outbound.Reader, linked.Token);
        var receive = ReceiveLoopAsync(
            socket,
            heartbeatState,
            chatState,
            salesState,
            linked.Token);

        var completed = await Task.WhenAny(forward, heartbeat, send, receive)
            .ConfigureAwait(false);
        linked.Cancel();
        outbound.Writer.TryComplete();
        if (completed.IsFaulted && !cancellationToken.IsCancellationRequested)
        {
            var failure = completed.Exception?.GetBaseException();
            var status = failure is InvalidDataException
                ? WebSocketCloseStatus.ProtocolError
                : WebSocketCloseStatus.PolicyViolation;
            await CloseAsync(socket, status, "WebSocket session ended.", cancellationToken)
                .ConfigureAwait(false);
        }

        await IgnoreCancellationAsync(forward, heartbeat, send, receive).ConfigureAwait(false);
    }

    private async Task ForwardEventsAsync(
        RemoteSubscription subscription,
        ChannelWriter<StreamServerMessage> writer,
        CancellationToken cancellationToken)
    {
        await foreach (var item in subscription.Reader.ReadAllAsync(cancellationToken))
        {
            if (!writer.TryWrite(EventMessage(item)))
            {
                _metrics.Increment(TransportMetric.SlowClientDisconnects);
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }
    }

    private async Task ProduceHeartbeatsAsync(
        ChannelWriter<StreamServerMessage> writer,
        HeartbeatState state,
        ChatConnectionState chatState,
        SalesConnectionState salesState,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            if (now - state.LastAcknowledgement > HeartbeatTimeout)
            {
                _metrics.Increment(TransportMetric.HeartbeatTimeouts);
                throw new TimeoutException("Client heartbeat acknowledgement timed out.");
            }

            await chatState.RefreshAuthorizationIfDueAsync(now, cancellationToken)
                .ConfigureAwait(false);
            await salesState.RefreshAuthorizationIfDueAsync(now, cancellationToken)
                .ConfigureAwait(false);

            var id = Guid.NewGuid().ToString("N");
            state.ExpectedId = id;
            if (!writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    OverlayTransportProtocol.Heartbeat,
                    HeartbeatId: id,
                    SentAt: now)))
            {
                _metrics.Increment(TransportMetric.SlowClientDisconnects);
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }
    }

    private static async Task SendLoopAsync(
        WebSocket socket,
        ChannelReader<StreamServerMessage> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var message in reader.ReadAllAsync(cancellationToken))
        {
            await SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        HeartbeatState state,
        ChatConnectionState chatState,
        SalesConnectionState salesState,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            try
            {
                OverlayProtocolJson.EnsureVersion(message.ProtocolVersion);
            }
            catch (NotSupportedException)
            {
                throw new InvalidDataException("Unsupported protocol version.");
            }

            if (message.Type == OverlayTransportProtocol.ChatSubscribe)
            {
                await chatState.SwitchAsync(message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message.Type == OverlayTransportProtocol.SalesSubscribe)
            {
                await salesState.SubscribeAsync(message, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (message.Type != OverlayTransportProtocol.HeartbeatAck ||
                message.HeartbeatId is null ||
                !string.Equals(message.HeartbeatId, state.ExpectedId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Unsupported WebSocket control message.");
            }

            state.LastAcknowledgement = DateTimeOffset.UtcNow;
        }
    }

    private static async Task<StreamClientMessage?> ReceiveAsync(
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(
            OverlayTransportProtocol.MaximumInboundWebSocketBytes);
        try
        {
            var count = 0;
            while (true)
            {
                if (count == OverlayTransportProtocol.MaximumInboundWebSocketBytes)
                {
                    throw new InvalidDataException("WebSocket message exceeds the 16 KiB limit.");
                }

                var result = await socket.ReceiveAsync(
                        rented.AsMemory(count,
                            OverlayTransportProtocol.MaximumInboundWebSocketBytes - count),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidDataException("Binary WebSocket messages are not supported.");
                }

                count += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            try
            {
                return JsonSerializer.Deserialize<StreamClientMessage>(
                    rented.AsSpan(0, count),
                    OverlayProtocolJson.Options);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Malformed WebSocket JSON.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static Task SendAsync(
        WebSocket socket,
        StreamServerMessage message,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, OverlayProtocolJson.Options);
        return socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task CloseAsync(
        WebSocket socket,
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseOutputAsync(status, description, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static StreamServerMessage EventMessage(ProtocolEventEnvelope envelope) => new(
        OverlayTransportProtocol.Version,
        OverlayTransportProtocol.Event,
        envelope.Generation,
        envelope.Sequence,
        envelope);

    internal static bool SupportsGtaCompanion(StreamClientMessage resume) =>
        resume.Capabilities?.Contains(
            OverlayTransportProtocol.GtaCompanionV1Capability,
            StringComparer.Ordinal) == true;

    private static async Task IgnoreCancellationAsync(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or WebSocketException or
                ChannelClosedException or InvalidOperationException or TimeoutException or
                InvalidDataException)
            {
                // A single failed/closed bounded session is isolated from the host.
            }
        }
    }

    private sealed class HeartbeatState
    {
        public DateTimeOffset LastAcknowledgement { get; set; } = DateTimeOffset.UtcNow;
        public string? ExpectedId { get; set; }
    }

    private sealed class GtaCompanionConnectionState : IDisposable
    {
        private readonly GtaEventService? _service;
        private readonly ChannelWriter<StreamServerMessage> _writer;

        public GtaCompanionConnectionState(
            GtaEventService? service,
            bool enabled,
            ChannelWriter<StreamServerMessage> writer)
        {
            _service = enabled ? service : null;
            _writer = writer;
            if (_service is null)
            {
                return;
            }

            _service.SnapshotChanged += OnSnapshotChanged;
            Write(_service.CaptureSnapshot());
        }

        public void Dispose()
        {
            if (_service is not null)
            {
                _service.SnapshotChanged -= OnSnapshotChanged;
            }
        }

        private void OnSnapshotChanged(GtaCompanionSnapshot snapshot) => Write(snapshot);

        private void Write(GtaCompanionSnapshot snapshot)
        {
            if (!_writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    OverlayTransportProtocol.GtaCompanionSnapshot,
                    GtaCompanion: snapshot)))
            {
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }
    }

    private sealed class SalesConnectionState : IAsyncDisposable
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);

        private readonly RemoteSalesService? _sales;
        private readonly AuthenticatedClientIdentity _identity;
        private readonly ChannelWriter<StreamServerMessage> _writer;
        private SalesStreamSubscription? _subscription;
        private CancellationTokenSource? _pumpCancellation;
        private Task? _pump;
        private DateTimeOffset _nextAuthorizationRefresh = DateTimeOffset.MaxValue;
        private volatile bool _deliveryAllowed;
        private volatile bool _missedWhileSuspended;

        public SalesConnectionState(
            RemoteSalesService? sales,
            AuthenticatedClientIdentity identity,
            ChannelWriter<StreamServerMessage> writer)
        {
            _sales = sales;
            _identity = identity;
            _writer = writer;
        }

        public async Task SubscribeAsync(
            StreamClientMessage message,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message.SalesGeneration) ||
                message.AfterSalesSequence is null)
            {
                throw new InvalidDataException("A complete sales subscription is required.");
            }

            if (_sales is null)
            {
                throw new InvalidDataException("Sales transport is unavailable in this host.");
            }

            var prepared = await _sales.SubscribeAsync(
                    _identity,
                    message.SalesGeneration,
                    message.AfterSalesSequence.Value,
                    forceAuthorizationRefresh: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (prepared.Status != ChatAuthorizationStatus.Authorized ||
                prepared.Resume?.Subscription is null)
            {
                WriteStatus(
                    MapFailure(prepared.Status, prepared.Resume?.Disposition),
                    prepared.Resume?.Generation,
                    prepared.Resume?.LatestSequence,
                    prepared.Reason);
                return;
            }

            await DisposeSubscriptionAsync().ConfigureAwait(false);
            _subscription = prepared.Resume.Subscription;
            foreach (var envelope in _subscription.Replay)
            {
                WriteEvent(envelope);
            }

            WriteStatus(
                OverlayTransportProtocol.SalesReady,
                prepared.Resume.Generation,
                prepared.Resume.LatestSequence,
                null);
            _deliveryAllowed = true;
            _missedWhileSuspended = false;
            _nextAuthorizationRefresh = DateTimeOffset.UtcNow + RefreshInterval;
            _pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pump = PumpAsync(_subscription, _pumpCancellation.Token);
        }

        public async Task RefreshAuthorizationIfDueAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (_subscription is null || now < _nextAuthorizationRefresh || _sales is null)
            {
                return;
            }

            var result = await _sales.RefreshAuthorizationAsync(_identity, cancellationToken)
                .ConfigureAwait(false);
            _nextAuthorizationRefresh = now + RefreshInterval;
            switch (result.Status)
            {
                case ChatAuthorizationStatus.Authorized:
                    if (_missedWhileSuspended)
                    {
                        WriteStatus(
                            OverlayTransportProtocol.SalesResyncRequired,
                            null,
                            null,
                            "AuthorizationRecoveredAfterSuspension");
                    }

                    _deliveryAllowed = true;
                    _missedWhileSuspended = false;
                    break;
                case ChatAuthorizationStatus.AuthorizationUnavailable:
                    _deliveryAllowed = false;
                    WriteStatus(
                        OverlayTransportProtocol.SalesAuthorizationUnavailable,
                        null,
                        null,
                        result.Status.ToString());
                    break;
                default:
                    _deliveryAllowed = false;
                    WriteStatus(
                        OverlayTransportProtocol.SalesAccessRevoked,
                        null,
                        null,
                        result.Status.ToString());
                    await DisposeSubscriptionAsync().ConfigureAwait(false);
                    break;
            }
        }

        public async ValueTask DisposeAsync() => await DisposeSubscriptionAsync()
            .ConfigureAwait(false);

        private async Task PumpAsync(
            SalesStreamSubscription subscription,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var envelope in subscription.Reader.ReadAllAsync(cancellationToken))
                {
                    if (!_deliveryAllowed)
                    {
                        _missedWhileSuspended = true;
                        continue;
                    }

                    WriteEvent(envelope);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal connection shutdown or replacement subscription.
            }
            catch (Exception exception)
            {
                _writer.TryComplete(exception);
            }
        }

        private void WriteEvent(SalesMutationEnvelope envelope)
        {
            if (!_writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    envelope.EventType,
                    ChannelId: envelope.ChannelId,
                    SalesGeneration: envelope.Generation,
                    SalesLatestSequence: envelope.Sequence,
                    SalesEvent: envelope)))
            {
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }

        private void WriteStatus(
            string type,
            string? generation,
            long? latestSequence,
            string? reason)
        {
            if (!_writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    type,
                    Reason: reason,
                    ChannelId: _sales?.ChannelId,
                    SalesGeneration: generation,
                    SalesLatestSequence: latestSequence)))
            {
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }

        private async Task DisposeSubscriptionAsync()
        {
            var cancellation = _pumpCancellation;
            var pump = _pump;
            var subscription = _subscription;
            _pumpCancellation = null;
            _pump = null;
            _subscription = null;
            cancellation?.Cancel();
            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

            if (pump is not null)
            {
                await IgnoreCancellationAsync(pump).ConfigureAwait(false);
            }

            cancellation?.Dispose();
        }

        private static string MapFailure(
            ChatAuthorizationStatus status,
            SalesResumeDisposition? disposition) => status switch
            {
                ChatAuthorizationStatus.AccessRevoked =>
                    OverlayTransportProtocol.SalesAccessRevoked,
                ChatAuthorizationStatus.AuthorizationUnavailable =>
                    OverlayTransportProtocol.SalesAuthorizationUnavailable,
                ChatAuthorizationStatus.ChannelUnavailable when
                    disposition is SalesResumeDisposition.WrongGeneration or
                        SalesResumeDisposition.HistoryExpired or
                        SalesResumeDisposition.FutureSequence =>
                    OverlayTransportProtocol.SalesResyncRequired,
                ChatAuthorizationStatus.ChannelUnavailable =>
                    OverlayTransportProtocol.SalesChannelUnavailable,
                _ => OverlayTransportProtocol.SalesFailed,
            };
    }

    private sealed class ChatConnectionState : IAsyncDisposable
    {
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(90);

        private readonly RemoteChatService? _chat;
        private readonly AuthenticatedClientIdentity _identity;
        private readonly ChannelWriter<StreamServerMessage> _writer;
        private readonly SemaphoreSlim _switchGate = new(1, 1);
        private ChatStreamSubscription? _subscription;
        private CancellationTokenSource? _pumpCancellation;
        private Task? _pump;
        private ulong? _channelId;
        private long _switchGeneration = -1;
        private DateTimeOffset _nextAuthorizationRefresh = DateTimeOffset.MaxValue;
        private volatile bool _deliveryAllowed;
        private volatile bool _missedWhileSuspended;

        public ChatConnectionState(
            RemoteChatService? chat,
            AuthenticatedClientIdentity identity,
            ChannelWriter<StreamServerMessage> writer)
        {
            _chat = chat;
            _identity = identity;
            _writer = writer;
        }

        public async Task SwitchAsync(
            StreamClientMessage message,
            CancellationToken cancellationToken)
        {
            if (message.ChannelId is null ||
                string.IsNullOrWhiteSpace(message.ChatGeneration) ||
                message.AfterChatSequence is null ||
                message.SwitchGeneration is null ||
                message.SwitchGeneration < 0)
            {
                throw new InvalidDataException("A complete chat subscription is required.");
            }

            if (_chat is null)
            {
                throw new InvalidDataException("Chat transport is unavailable in this host.");
            }

            await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (message.SwitchGeneration <= _switchGeneration)
                {
                    return;
                }

                var prepared = await _chat.SubscribeAsync(
                        _identity,
                        message.ChannelId.Value,
                        message.ChatGeneration,
                        message.AfterChatSequence.Value,
                        forceAuthorizationRefresh: true,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (prepared.Status != ChatAuthorizationStatus.Authorized ||
                    prepared.Resume?.Subscription is null)
                {
                    WriteStatus(
                        MapFailure(prepared.Status, prepared.Resume?.Disposition),
                        message.ChannelId,
                        message.ChatGeneration,
                        prepared.Resume?.LatestSequence,
                        message.SwitchGeneration,
                        prepared.Reason);
                    return;
                }

                var next = prepared.Resume.Subscription;
                foreach (var envelope in next.Replay)
                {
                    WriteChatEvent(envelope, message.SwitchGeneration.Value);
                }

                WriteStatus(
                    OverlayTransportProtocol.ChatReady,
                    message.ChannelId,
                    prepared.Resume.Generation,
                    prepared.Resume.LatestSequence,
                    message.SwitchGeneration,
                    null);

                // Commit only after the replacement subscription is authorized, replayed,
                // and declared ready; the old channel remains live until this point.
                var previous = _subscription;
                var previousCancellation = _pumpCancellation;
                var previousPump = _pump;
                _subscription = next;
                _pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                _switchGeneration = message.SwitchGeneration.Value;
                _channelId = message.ChannelId;
                _deliveryAllowed = true;
                _missedWhileSuspended = false;
                _nextAuthorizationRefresh = DateTimeOffset.UtcNow + RefreshInterval;
                _pump = PumpAsync(next, _switchGeneration, _pumpCancellation.Token);

                previousCancellation?.Cancel();
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }

                if (previousPump is not null)
                {
                    await IgnoreCancellationAsync(previousPump).ConfigureAwait(false);
                }

                previousCancellation?.Dispose();
            }
            finally
            {
                _switchGate.Release();
            }
        }

        public async Task RefreshAuthorizationIfDueAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            if (_channelId is not ulong channelId || now < _nextAuthorizationRefresh)
            {
                return;
            }

            if (_chat is null)
            {
                return;
            }

            var result = await _chat.RefreshAuthorizationAsync(
                    _identity,
                    channelId,
                    cancellationToken)
                .ConfigureAwait(false);
            _nextAuthorizationRefresh = now + RefreshInterval;
            switch (result.Status)
            {
                case ChatAuthorizationStatus.Authorized:
                    if (_missedWhileSuspended)
                    {
                        WriteStatus(
                            OverlayTransportProtocol.ChatResyncRequired,
                            channelId,
                            null,
                            null,
                            _switchGeneration,
                            "AuthorizationRecoveredAfterSuspension");
                    }

                    _deliveryAllowed = true;
                    _missedWhileSuspended = false;
                    break;
                case ChatAuthorizationStatus.AuthorizationUnavailable:
                    _deliveryAllowed = false;
                    WriteStatus(
                        OverlayTransportProtocol.ChatAuthorizationUnavailable,
                        channelId,
                        null,
                        null,
                        _switchGeneration,
                        result.Status.ToString());
                    break;
                default:
                    _deliveryAllowed = false;
                    WriteStatus(
                        OverlayTransportProtocol.ChatAccessRevoked,
                        channelId,
                        null,
                        null,
                        _switchGeneration,
                        result.Status.ToString());
                    await DisposeSubscriptionAsync().ConfigureAwait(false);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _switchGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DisposeSubscriptionAsync().ConfigureAwait(false);
            }
            finally
            {
                _switchGate.Release();
                _switchGate.Dispose();
            }
        }

        private async Task PumpAsync(
            ChatStreamSubscription subscription,
            long switchGeneration,
            CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var envelope in subscription.Reader.ReadAllAsync(cancellationToken))
                {
                    if (!_deliveryAllowed)
                    {
                        _missedWhileSuspended = true;
                        continue;
                    }

                    WriteChatEvent(envelope, switchGeneration);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Normal channel switch or connection shutdown.
            }
            catch (Exception exception)
            {
                _writer.TryComplete(exception);
            }
        }

        private void WriteChatEvent(ChatMutationEnvelope envelope, long switchGeneration)
        {
            if (!_writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    envelope.EventType,
                    ChannelId: envelope.ChannelId,
                    ChatGeneration: envelope.Generation,
                    ChatLatestSequence: envelope.Sequence,
                    SwitchGeneration: switchGeneration,
                    ChatEvent: envelope)))
            {
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }

        private void WriteStatus(
            string type,
            ulong? channelId,
            string? generation,
            long? latestSequence,
            long? switchGeneration,
            string? reason)
        {
            if (!_writer.TryWrite(new StreamServerMessage(
                    OverlayTransportProtocol.Version,
                    type,
                    Reason: reason,
                    ChannelId: channelId,
                    ChatGeneration: generation,
                    ChatLatestSequence: latestSequence,
                    SwitchGeneration: switchGeneration)))
            {
                throw new InvalidOperationException("Slow client outbound queue is full.");
            }
        }

        private async Task DisposeSubscriptionAsync()
        {
            var cancellation = _pumpCancellation;
            var pump = _pump;
            var subscription = _subscription;
            _pumpCancellation = null;
            _pump = null;
            _subscription = null;
            cancellation?.Cancel();
            if (subscription is not null)
            {
                await subscription.DisposeAsync().ConfigureAwait(false);
            }

            if (pump is not null)
            {
                await IgnoreCancellationAsync(pump).ConfigureAwait(false);
            }

            cancellation?.Dispose();
        }

        private static string MapFailure(
            ChatAuthorizationStatus status,
            ChatResumeDisposition? disposition) => status switch
            {
                ChatAuthorizationStatus.AccessRevoked =>
                    OverlayTransportProtocol.ChatAccessRevoked,
                ChatAuthorizationStatus.AuthorizationUnavailable =>
                    OverlayTransportProtocol.ChatAuthorizationUnavailable,
                ChatAuthorizationStatus.ChannelUnavailable when
                    disposition is ChatResumeDisposition.WrongGeneration or
                        ChatResumeDisposition.HistoryExpired or
                        ChatResumeDisposition.FutureSequence =>
                    OverlayTransportProtocol.ChatResyncRequired,
                ChatAuthorizationStatus.ChannelUnavailable =>
                    OverlayTransportProtocol.ChatChannelUnavailable,
                _ => OverlayTransportProtocol.ChatFailed,
            };
    }
}
