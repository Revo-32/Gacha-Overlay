using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LSOverlay.Backend.Chat;
using LSOverlay.Backend.Discord;
using LSOverlay.Backend.Sales;
using LSOverlay.Backend.Security;
using LSOverlay.Backend.Transport;
using LSOverlay.Protocol;

namespace LSOverlay.Backend.CoreClient;

internal sealed class CoreUserSession : IDisposable
{
    private readonly RemoteChatService _chat;
    private readonly RemoteSalesService _sales;
    private readonly RemotePublicationHub _presence;
    private readonly Func<bool> _credentialValid;
    private readonly IGuildMembershipVerifier _membership;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _gate = new();
    private readonly RelayState _state;
    private readonly ChannelSelection _channels = new();
    private readonly Channel<bool> _updates = Signal(), _chatRequests = Signal(), _salesRequests = Signal();
    private readonly Channel<StreamServerMessage> _events = Channel.CreateBounded<StreamServerMessage>(256);
    private readonly SemaphoreSlim _chatGate = new(1, 1), _salesGate = new(1, 1);
    private long _switch, _chatSequence, _salesSequence, _ackTicks = DateTime.UtcNow.Ticks;
    private string? _chatGeneration, _salesGeneration;
    private bool _chatRecovering, _salesRecovering;
    private int _requestedSlot = -1, _dirty = 1, _heartbeat;
    private static Channel<bool> Signal() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    public AuthenticatedClientIdentity Identity { get; }
    public CoreMediaScope Media { get; }
    public bool Cancelled => _lifetime.IsCancellationRequested;
    public CoreUserSession(AuthenticatedClientIdentity identity, RemoteChatService chat, RemoteSalesService sales,
        RemotePublicationHub presence, IGuildMembershipVerifier membership, Func<bool> credentialValid, CancellationToken cancellation)
    {
        Identity = identity; _chat = chat; _sales = sales; _presence = presence; _membership = membership; _credentialValid = credentialValid;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        _lifetime.CancelAfter(TimeSpan.FromHours(6));
        Media = new(identity.DiscordUserId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message => { lock (_gate) return _state!.ReadableChannel(message); },
            async (channel, token) =>
            {
                if (!_credentialValid() || !ulong.TryParse(channel, out var id)) return false;
                var access = await _chat.AuthorizeMediaAsync(Identity, id, token);
                return access.Status == ChatAuthorizationStatus.Authorized && _credentialValid();
            }, _lifetime.Token);
        _state = new(_presence.CaptureBootstrap(identity), Media) { Channels = _channels, IncludeSalesActionHints = true };
    }
    public bool Select(int slot)
    {
        if (Cancelled) return false;
        try { _channels.Resolve(slot); } catch (UnauthorizedAccessException) { return false; }
        Interlocked.Exchange(ref _requestedSlot, slot); return _chatRequests.Writer.TryWrite(true);
    }
    private void Change(Action change)
    {
        lock (_gate) change(); Interlocked.Exchange(ref _dirty, 1); _updates.Writer.TryWrite(true);
    }
    public async Task Run(WebSocket socket, CancellationToken cancellation)
    {
        using var abort = _lifetime.Token.Register(socket.Abort);
        using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        {
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var hello = JsonSerializer.Deserialize<CoreSessionStart>(await Receive(socket, helloTimeout.Token), OverlayProtocolJson.Options) ?? throw new InvalidDataException();
            CoreSnapshotWire.ValidateHello(hello);
        }
        var initial = _presence.StartSession(Identity);
        await using var presence = initial.Resume.Subscription ?? throw new InvalidDataException("Presence unavailable");
        Change(() => _state.Presence(initial.Bootstrap));
        await using var chatState = new BackendWebSocketSession.ChatConnectionState(_chat, Identity, _events.Writer);
        await using var salesState = new BackendWebSocketSession.SalesConnectionState(_sales, Identity, _events.Writer);
        _chatRequests.Writer.TryWrite(true); _salesRequests.Writer.TryWrite(true);
        var tasks = new[] { Send(socket), ReceiveControls(socket), Dispatch(), Presence(presence), Heartbeat(chatState, salesState), ChatPump(chatState), SalesPump(salesState) };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            _lifetime.Cancel(); _events.Writer.TryComplete();
            // Observe every task before disposing gates, including malformed controls.
            // The original first failure still propagates out of Run.
            try { await Task.WhenAll(tasks); } catch (Exception) { }
        }
    }
    private async Task ChatPump(BackendWebSocketSession.ChatConnectionState connection)
    {
        var token = _lifetime.Token;
        while (await _chatRequests.Reader.WaitToReadAsync(token))
        {
            _chatRequests.Reader.TryRead(out _);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var catalog = await _chat.GetCatalogAsync(Identity, timeout.Token);
                if (catalog.Status != ChatAuthorizationStatus.Authorized) throw new UnauthorizedAccessException();
                _channels.Update(new(1, catalog.AuthorizedChannels));
                var slot = Interlocked.Exchange(ref _requestedSlot, -1);
                ulong current; string status; lock (_gate) { current = _state.ChatChannel; status = _state.ChatStatus; }
                if (slot < 0) slot = _channels.Describe(current).Slot;
                if (slot < 0) slot = _channels.Describe(0).AvailableSlots.FirstOrDefault(-1);
                var channel = _channels.Resolve(slot);
                if (channel == current && status == "live") continue;
                var bootstrap = await _chat.BootstrapAsync(Identity, new(1, channel), timeout.Token);
                if (bootstrap.Status != ChatAuthorizationStatus.Authorized || bootstrap.Response is null) throw new UnauthorizedAccessException();
                await _chatGate.WaitAsync(timeout.Token);
                try
                {
                    var response = bootstrap.Response;
                    Change(() => { Media.RevokeChannel(_state.ChatChannel.ToString()); _switch++; _chatRecovering = false; _chatGeneration = response.Generation; _chatSequence = response.LatestSequence; _state.Chat(response); });
                    await connection.SwitchAsync(new(1, OverlayTransportProtocol.ChatSubscribe, ChannelId: channel, ChatGeneration: response.Generation,
                        AfterChatSequence: response.LatestSequence, SwitchGeneration: _switch), timeout.Token);
                }
                finally { _chatGate.Release(); }
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is UnauthorizedAccessException or HttpRequestException or IOException or OperationCanceledException)
            {
                Change(() => { Media.RevokeChannel(_state.ChatChannel.ToString()); _state.ChatState(_state.ChatChannel, OverlayTransportProtocol.ChatAuthorizationUnavailable); });
                await Task.Delay(TimeSpan.FromSeconds(5), token); _chatRequests.Writer.TryWrite(true);
            }
        }
    }
    private async Task SalesPump(BackendWebSocketSession.SalesConnectionState connection)
    {
        var token = _lifetime.Token;
        while (await _salesRequests.Reader.WaitToReadAsync(token))
        {
            _salesRequests.Reader.TryRead(out _);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var bootstrap = await _sales.BootstrapAsync(Identity, new(1), timeout.Token);
                if (bootstrap.Status != ChatAuthorizationStatus.Authorized || bootstrap.Response is null) throw new UnauthorizedAccessException();
                await _salesGate.WaitAsync(timeout.Token);
                try
                {
                    var response = bootstrap.Response;
                    Change(() => { Media.RevokeChannel(_sales.ChannelId.ToString()); _salesRecovering = false; _salesGeneration = response.Generation; _salesSequence = response.LatestSequence; _state.Sales(response); });
                    await connection.SubscribeAsync(new(1, OverlayTransportProtocol.SalesSubscribe, SalesGeneration: response.Generation, AfterSalesSequence: response.LatestSequence), timeout.Token);
                }
                finally { _salesGate.Release(); }
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is UnauthorizedAccessException or HttpRequestException or IOException or OperationCanceledException)
            {
                Change(() => { Media.RevokeChannel(_sales.ChannelId.ToString()); _state.SalesState(OverlayTransportProtocol.SalesAuthorizationUnavailable); });
                await Task.Delay(TimeSpan.FromSeconds(5), token); _salesRequests.Writer.TryWrite(true);
            }
        }
    }
    private async Task Dispatch()
    {
        await foreach (var message in _events.Reader.ReadAllAsync(_lifetime.Token))
        {
            Change(() =>
            {
                if (message.SalesEvent is { } sale)
                {
                    if (_salesRecovering || sale.Generation != _salesGeneration || sale.Sequence <= _salesSequence) return;
                    if (sale.EventType == OverlayTransportProtocol.SalesResyncRequired)
                    { _salesRecovering = true; _state.SalesState(sale.EventType); Media.RevokeChannel(_sales.ChannelId.ToString()); _salesRequests.Writer.TryWrite(true); return; }
                    Media.Revoke(sale.MessageId.ToString()); _state.Sales(sale); _salesSequence = sale.Sequence;
                }
                else if (message.Type.StartsWith("sales_", StringComparison.Ordinal))
                {
                    if (message.SalesGeneration is not null && message.SalesGeneration != _salesGeneration) return;
                    _state.SalesState(message.Type);
                    if (message.Type != OverlayTransportProtocol.SalesReady)
                    { _salesRecovering = true; Media.RevokeChannel(_sales.ChannelId.ToString()); _salesRequests.Writer.TryWrite(true); }
                }
                else if (message.SwitchGeneration == _switch)
                {
                    if (message.ChatEvent is { } chat)
                    {
                        if (_chatRecovering || chat.Generation != _chatGeneration || chat.Sequence <= _chatSequence) return;
                        if (chat.EventType == OverlayTransportProtocol.ChatResyncRequired)
                        { _chatRecovering = true; _state.ChatState(chat.ChannelId, chat.EventType); Media.RevokeChannel(chat.ChannelId.ToString()); _chatRequests.Writer.TryWrite(true); return; }
                        Media.Revoke(chat.MessageId.ToString()); _state.Chat(chat); _chatSequence = chat.Sequence;
                    }
                    else if (message.ChannelId is ulong channel)
                    {
                        _state.ChatState(channel, message.Type);
                        if (message.Type != OverlayTransportProtocol.ChatReady)
                        { _chatRecovering = true; Media.RevokeChannel(channel.ToString()); _chatRequests.Writer.TryWrite(true); }
                    }
                }
            });
        }
    }
    private async Task Presence(RemoteSubscription subscription)
    {
        await foreach (var item in subscription.Reader.ReadAllAsync(_lifetime.Token))
            Change(() => _state.Presence(item.Payload));
    }
    private async Task Heartbeat(BackendWebSocketSession.ChatConnectionState chat, BackendWebSocketSession.SalesConnectionState sales)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(_lifetime.Token))
        {
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _ackTicks) > TimeSpan.FromSeconds(75).Ticks) throw new TimeoutException();
            if (!_credentialValid() || await _membership.VerifyAsync(Identity, _lifetime.Token) != GuildMembershipStatus.Member) throw new UnauthorizedAccessException();
            Interlocked.Exchange(ref _heartbeat, 1); _updates.Writer.TryWrite(true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var batch = new ChatAuthorizationService.RefreshBatch(timeout.Token);
            using var permissionBatch = batch.Enter();
            await _chatGate.WaitAsync(timeout.Token);
            try { await chat.RefreshAuthorizationIfDueAsync(DateTimeOffset.UtcNow, timeout.Token); } finally { _chatGate.Release(); }
            await _salesGate.WaitAsync(timeout.Token);
            try { await sales.RefreshAuthorizationIfDueAsync(DateTimeOffset.UtcNow, timeout.Token); } finally { _salesGate.Release(); }
        }
    }
    private async Task Send(WebSocket socket)
    {
        while (await _updates.Reader.WaitToReadAsync(_lifetime.Token))
        {
            // Bound serialization/render publication during bursts without holding chat delivery behind Sales REST.
            await Task.Delay(TimeSpan.FromMilliseconds(30), _lifetime.Token);
            _updates.Reader.TryRead(out _);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
            if (Interlocked.Exchange(ref _dirty, 0) != 0)
            {
                CoreSnapshot snapshot; lock (_gate) snapshot = _state.Capture();
                foreach (var bytes in CoreSnapshotWire.Encode(snapshot)) await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
                await Write(socket, new CoreReady(1, CoreClientProtocol.Ready, snapshot.Generation, snapshot.Revision, false), timeout.Token);
            }
            if (Interlocked.Exchange(ref _heartbeat, 0) != 0) await Write(socket, new { protocolVersion = 1, type = "heartbeat" }, timeout.Token);
        }
    }
    private async Task ReceiveControls(WebSocket socket)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            using var json = JsonDocument.Parse(await Receive(socket, _lifetime.Token));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("protocolVersion", out var version) || version.GetInt32() != 1 ||
                !root.TryGetProperty("type", out var type) || type.GetString() != "heartbeat_ack") throw new InvalidDataException();
            Interlocked.Exchange(ref _ackTicks, DateTime.UtcNow.Ticks);
        }
    }
    private static Task Write<T>(WebSocket socket, T value, CancellationToken token) => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, OverlayProtocolJson.Options), WebSocketMessageType.Text, true, token);
    private static async Task<byte[]> Receive(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[4096]; var count = 0;
        while (count < bytes.Length)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(count), token);
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Core control must be text");
            count += result.Count; if (result.EndOfMessage) return bytes[..count];
        }
        throw new InvalidDataException("Core control exceeds limit");
    }
    public void Dispose() { _lifetime.Cancel(); Media.Revoke(); _lifetime.Dispose(); _chatGate.Dispose(); _salesGate.Dispose(); }
}
