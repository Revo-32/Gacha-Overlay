using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using LSOverlay.Protocol;

namespace LSOverlay.RemoteClient;

public sealed partial class LSOverlayRemoteClient : ILSOverlayRemoteClient, ILSOverlayRemoteSalesClient, ILSOverlayDiscordWebAuthClient
{
    private static readonly TimeSpan[] ReconnectDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    };

    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public LSOverlayRemoteClient(Uri baseUri, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        TransportEndpointSecurity.EnsureAllowed(baseUri);
        if (baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Remote client base URI must be HTTP or HTTPS.", nameof(baseUri));
        }

        _baseUri = baseUri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/");
        if (httpClient is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
        }
    }

    public event Action? StreamLive;
    public event Action<HostPresenceSnapshot>? HostPresenceChanged;
    public event Action? ResyncRequired;
    public event Action<ChatBootstrapResponse>? ChatChannelReady;
    public event Action<ChatMutationEnvelope>? ChatMutationReceived;
    public event Action<ulong, string>? ChatStreamStatusChanged;
    public event Action<SalesBootstrapResponse>? SalesReady;
    public event Action<SalesMutationEnvelope>? SalesMutationReceived;
    public event Action<string>? SalesStreamStatusChanged;

    public async Task<CreatePairingResponse> CreatePairingAsync(
        Guid clientInstallationId,
        CancellationToken cancellationToken = default)
    {
        var request = new CreatePairingRequest(
            OverlayTransportProtocol.Version,
            clientInstallationId);
        using var content = JsonContent.Create(request, options: OverlayProtocolJson.Options);
        using var response = await _http.PostAsync(
                Endpoint("api/v1/pairings"),
                content,
                cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<CreatePairingResponse>(response, cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<PairingClaimResponse> GetPairingAsync(
        Guid pairingId,
        string pairingClaimSecret,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            Endpoint($"api/v1/pairings/{pairingId:D}"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "LSOPairing",
            pairingClaimSecret);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException("Pairing claim was rejected.");
        }

        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<PairingClaimResponse>(response, cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<BootstrapResponse> GetBootstrapAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, "api/v1/bootstrap", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RemoteAuthenticationRequiredException();
        }

        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<BootstrapResponse>(response, cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<ChatChannelCatalogResponse> GetChatChannelsAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, "api/v1/chat/channels", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<ChatChannelCatalogResponse>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<ChatBootstrapResponse> GetChatBootstrapAsync(
        string accessToken,
        ulong channelId,
        CancellationToken cancellationToken = default)
    {
        var payload = new ChatBootstrapRequest(
            OverlayTransportProtocol.Version,
            channelId);
        using var request = Authorized(HttpMethod.Post, "api/v1/chat/bootstrap", accessToken);
        request.Content = JsonContent.Create(payload, options: OverlayProtocolJson.Options);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<ChatBootstrapResponse>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<SalesBootstrapResponse> GetSalesBootstrapAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var payload = new SalesBootstrapRequest(OverlayTransportProtocol.Version);
        using var request = Authorized(HttpMethod.Post, "api/v1/sales/bootstrap", accessToken);
        request.Content = JsonContent.Create(payload, options: OverlayProtocolJson.Options);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<SalesBootstrapResponse>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        return value;
    }

    public async Task<SalesStatusActionResponse> SetSalesStatusAsync(
        string accessToken,
        SalesStatusActionRequest payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        OverlayProtocolJson.EnsureVersion(payload.ProtocolVersion);
        using var request = Authorized(HttpMethod.Post, "api/v1/sales/status", accessToken);
        request.Content = JsonContent.Create(payload, options: OverlayProtocolJson.Options);
        using var response = await _http.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthorized(response);
        response.EnsureSuccessStatusCode();
        var value = await DeserializeAsync<SalesStatusActionResponse>(
                response,
                cancellationToken)
            .ConfigureAwait(false);
        OverlayProtocolJson.EnsureVersion(value.ProtocolVersion);
        if (value.ClientRequestId != payload.ClientRequestId)
        {
            throw new InvalidDataException("Sales status response request ID did not match.");
        }

        return value;
    }

    public Task StreamChatAsync(
        string accessToken,
        BootstrapResponse presenceBootstrap,
        ChatBootstrapResponse initialChatBootstrap,
        ChannelReader<ChatBootstrapResponse> channelSwitches,
        CancellationToken cancellationToken = default) =>
        StreamChatCoreAsync(
                accessToken,
                presenceBootstrap,
                initialChatBootstrap,
                null,
                channelSwitches,
                null,
                cancellationToken);

    public Task StreamChatAndSalesAsync(
        string accessToken,
        BootstrapResponse presenceBootstrap,
        ChatBootstrapResponse initialChatBootstrap,
        SalesBootstrapResponse salesBootstrap,
        ChannelReader<ChatBootstrapResponse> channelSwitches,
        ChannelReader<SalesBootstrapResponse> salesResyncs,
        CancellationToken cancellationToken = default) =>
        StreamChatCoreAsync(
                accessToken,
                presenceBootstrap,
                initialChatBootstrap,
                salesBootstrap,
                channelSwitches,
                salesResyncs,
                cancellationToken);

    private async Task StreamChatCoreAsync(
        string accessToken,
        BootstrapResponse presenceBootstrap,
        ChatBootstrapResponse initialChatBootstrap,
        SalesBootstrapResponse? salesBootstrap,
        ChannelReader<ChatBootstrapResponse> channelSwitches,
        ChannelReader<SalesBootstrapResponse>? salesResyncs,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(presenceBootstrap);
        ArgumentNullException.ThrowIfNull(initialChatBootstrap);
        ArgumentNullException.ThrowIfNull(channelSwitches);
        OverlayProtocolJson.EnsureVersion(presenceBootstrap.ProtocolVersion);
        OverlayProtocolJson.EnsureVersion(initialChatBootstrap.ProtocolVersion);
        if (salesBootstrap is not null)
        {
            OverlayProtocolJson.EnsureVersion(salesBootstrap.ProtocolVersion);
        }

        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await socket.ConnectAsync(StreamEndpoint(), cancellationToken).ConfigureAwait(false);
        using var sendGate = new SemaphoreSlim(1, 1);
        await SendSerializedAsync(socket, new StreamClientMessage(
            OverlayTransportProtocol.Version,
            OverlayTransportProtocol.Resume,
            presenceBootstrap.Generation,
            presenceBootstrap.LatestSequence), sendGate, cancellationToken).ConfigureAwait(false);

        var switchState = new TransactionalChatSwitchState(this);
        await switchState.RequestAsync(
                socket,
                initialChatBootstrap,
                sendGate,
                cancellationToken)
            .ConfigureAwait(false);
        var salesState = new TransactionalSalesState(this, salesBootstrap);
        if (salesBootstrap is not null)
        {
            await salesState.RequestAsync(socket, salesBootstrap, sendGate, cancellationToken)
                .ConfigureAwait(false);
        }
        var presenceGeneration = presenceBootstrap.Generation;
        var nextPresenceSequence = presenceBootstrap.LatestSequence + 1;
        // These requests now belong to the transactional states, not this
        // connection-long async frame. Keep only the presence cursor.
        initialChatBootstrap = null!;
        salesBootstrap = null;
        presenceBootstrap = null!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var switchPump = PumpChatSwitchesAsync(
            socket,
            channelSwitches,
            switchState,
            sendGate,
            linked.Token);
        var salesPump = salesResyncs is null
            ? Task.CompletedTask
            : PumpSalesResyncsAsync(
                socket,
                salesResyncs,
                salesState,
                sendGate,
                linked.Token);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   socket.State == WebSocketState.Open)
            {
                var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    return;
                }

                OverlayProtocolJson.EnsureVersion(message.ProtocolVersion);
                switch (message.Type)
                {
                    case OverlayTransportProtocol.Event:
                        ValidateEvent(message.Event, presenceGeneration);
                        if (message.Event!.Sequence != nextPresenceSequence++)
                        {
                            throw new InvalidDataException(
                                "Remote presence sequence contained a gap.");
                        }

                        HostPresenceChanged?.Invoke(message.Event.Payload);
                        break;
                    case OverlayTransportProtocol.Live:
                        StreamLive?.Invoke();
                        break;
                    case OverlayTransportProtocol.Heartbeat:
                        if (string.IsNullOrWhiteSpace(message.HeartbeatId))
                        {
                            throw new InvalidDataException("Heartbeat ID is missing.");
                        }

                        await SendSerializedAsync(socket, new StreamClientMessage(
                            OverlayTransportProtocol.Version,
                            OverlayTransportProtocol.HeartbeatAck,
                            HeartbeatId: message.HeartbeatId), sendGate, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    case OverlayTransportProtocol.ChatReady:
                        switchState.Commit(message);
                        break;
                    case OverlayTransportProtocol.ChatMessageCreate:
                    case OverlayTransportProtocol.ChatMessageUpdate:
                    case OverlayTransportProtocol.ChatMessageDelete:
                        switchState.Accept(message);
                        break;
                    case OverlayTransportProtocol.ChatResyncRequired:
                    case OverlayTransportProtocol.ChatFailed:
                    case OverlayTransportProtocol.ChatAccessRevoked:
                    case OverlayTransportProtocol.ChatAuthorizationUnavailable:
                    case OverlayTransportProtocol.ChatChannelUnavailable:
                        switchState.Status(message);
                        break;
                    case OverlayTransportProtocol.SalesReady:
                        salesState.Commit(message);
                        break;
                    case OverlayTransportProtocol.SalesMessageCreate:
                    case OverlayTransportProtocol.SalesMessageUpdate:
                    case OverlayTransportProtocol.SalesMessageDelete:
                    case OverlayTransportProtocol.SalesCompletionEvidence:
                        salesState.Accept(message);
                        break;
                    case OverlayTransportProtocol.SalesResyncRequired:
                    case OverlayTransportProtocol.SalesFailed:
                    case OverlayTransportProtocol.SalesAccessRevoked:
                    case OverlayTransportProtocol.SalesAuthorizationUnavailable:
                    case OverlayTransportProtocol.SalesChannelUnavailable:
                        salesState.Status(message);
                        break;
                    case OverlayTransportProtocol.ResyncRequired:
                        ResyncRequired?.Invoke();
                        throw new RemoteResyncRequiredException();
                    default:
                        throw new InvalidDataException("Unsupported server stream message.");
                }
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await Task.WhenAll(switchPump, salesPump).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // Normal stream shutdown.
            }
        }
    }

    public async Task StreamAsync(
        string accessToken,
        BootstrapResponse bootstrap,
        CancellationToken cancellationToken = default)
    {
        var cursor = bootstrap.LatestSequence;
        await StreamCoreAsync(
            accessToken,
            bootstrap,
            sequence => cursor = sequence,
            () => { },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamCoreAsync(
        string accessToken,
        BootstrapResponse bootstrap,
        Action<long> observeSequence,
        Action becameLive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentNullException.ThrowIfNull(bootstrap);
        OverlayProtocolJson.EnsureVersion(bootstrap.ProtocolVersion);
        using var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(OverlayTransportProtocol.WebSocketSubprotocol);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        try
        {
            await socket.ConnectAsync(StreamEndpoint(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAuthenticationFailure(exception))
        {
            throw new RemoteAuthenticationRequiredException();
        }

        await SendAsync(socket, new StreamClientMessage(
            OverlayTransportProtocol.Version,
            OverlayTransportProtocol.Resume,
            bootstrap.Generation,
            bootstrap.LatestSequence), cancellationToken).ConfigureAwait(false);

        var nextExpectedSequence = bootstrap.LatestSequence + 1;

        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            OverlayProtocolJson.EnsureVersion(message.ProtocolVersion);
            switch (message.Type)
            {
                case OverlayTransportProtocol.Event:
                    ValidateEvent(message.Event, bootstrap.Generation);
                    if (message.Event!.Sequence != nextExpectedSequence)
                    {
                        throw new InvalidDataException(
                            "Remote event sequence was duplicated or contained a gap.");
                    }

                    nextExpectedSequence++;
                    observeSequence(message.Event!.Sequence);
                    HostPresenceChanged?.Invoke(message.Event!.Payload);
                    break;
                case OverlayTransportProtocol.Live:
                    becameLive();
                    StreamLive?.Invoke();
                    break;
                case OverlayTransportProtocol.ResyncRequired:
                    ResyncRequired?.Invoke();
                    throw new RemoteResyncRequiredException();
                case OverlayTransportProtocol.Heartbeat:
                    if (string.IsNullOrWhiteSpace(message.HeartbeatId))
                    {
                        throw new InvalidDataException("Heartbeat ID is missing.");
                    }

                    await SendAsync(socket, new StreamClientMessage(
                        OverlayTransportProtocol.Version,
                        OverlayTransportProtocol.HeartbeatAck,
                        HeartbeatId: message.HeartbeatId), cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidDataException("Unsupported server stream message.");
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Remote stream closed before cancellation.");
        }
    }

    public async Task RunAuthenticatedAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Remote reconnect loop is already running.");
        }

        try
        {
            var failures = 0;
            BootstrapResponse? bootstrap = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                long cursor = 0;
                try
                {
                    bootstrap ??= await GetBootstrapAsync(accessToken, cancellationToken)
                        .ConfigureAwait(false);
                    cursor = bootstrap.LatestSequence;
                    await StreamCoreAsync(
                            accessToken,
                            bootstrap,
                            sequence => cursor = sequence,
                            () => failures = 0,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RemoteAuthenticationRequiredException)
                {
                    throw;
                }
                catch (RemoteResyncRequiredException)
                {
                    bootstrap = null;
                    continue;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or WebSocketException or IOException)
                {
                    if (bootstrap is not null && cursor >= bootstrap.LatestSequence)
                    {
                        bootstrap = bootstrap with { LatestSequence = cursor };
                    }

                    var baseDelay = ReconnectDelays[Math.Min(failures, ReconnectDelays.Length - 1)];
                    failures++;
                    var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
                    await Task.Delay(baseDelay + jitter, cancellationToken).ConfigureAwait(false);
                    bootstrap = null;
                }
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _runGate.Dispose();
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, Endpoint(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private Uri Endpoint(string relative) => new(_baseUri, relative);

    private Uri StreamEndpoint()
    {
        var builder = new UriBuilder(Endpoint("api/v1/stream"))
        {
            Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
        };
        var result = builder.Uri;
        TransportEndpointSecurity.EnsureAllowed(result);
        return result;
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(
                stream,
                OverlayProtocolJson.Options,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Backend response was empty.");
    }

    private static void ValidateEvent(
        ProtocolEventEnvelope? envelope,
        string expectedGeneration)
    {
        if (envelope is null ||
            envelope.ProtocolVersion != OverlayTransportProtocol.Version ||
            envelope.EventType != OverlayTransportProtocol.HostPresenceChanged ||
            envelope.Sequence <= 0 ||
            !string.Equals(envelope.Generation, expectedGeneration, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Invalid remote event envelope.");
        }
    }

    private static bool IsAuthenticationFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException http &&
                http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return true;
            }
        }

        return false;
    }

    private static Task SendAsync(
        ClientWebSocket socket,
        StreamClientMessage message,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, OverlayProtocolJson.Options);
        return socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private static async Task SendSerializedAsync(
        ClientWebSocket socket,
        StreamClientMessage message,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendAsync(socket, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void EnsureAuthorized(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RemoteAuthenticationRequiredException();
        }
    }

    private static async Task PumpChatSwitchesAsync(
        ClientWebSocket socket,
        ChannelReader<ChatBootstrapResponse> switches,
        TransactionalChatSwitchState state,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        await foreach (var next in switches.ReadAllAsync(cancellationToken))
        {
            OverlayProtocolJson.EnsureVersion(next.ProtocolVersion);
            await state.RequestAsync(socket, next, sendGate, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task PumpSalesResyncsAsync(
        ClientWebSocket socket,
        ChannelReader<SalesBootstrapResponse> resyncs,
        TransactionalSalesState state,
        SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        await foreach (var next in resyncs.ReadAllAsync(cancellationToken))
        {
            OverlayProtocolJson.EnsureVersion(next.ProtocolVersion);
            await state.RequestAsync(socket, next, sendGate, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class TransactionalSalesState
    {
        private const int MaximumStagedEvents = 256;

        private readonly object _sync = new();
        private readonly LSOverlayRemoteClient _owner;
        private readonly List<SalesMutationEnvelope> _staged = new();
        private SalesBootstrapResponse? _bootstrap;
        private string? _generation;
        private long _nextSequence;
        private bool _committed;

        public TransactionalSalesState(
            LSOverlayRemoteClient owner,
            SalesBootstrapResponse? bootstrap)
        {
            _owner = owner;
            _bootstrap = bootstrap;
        }

        public Task RequestAsync(
            ClientWebSocket socket,
            SalesBootstrapResponse bootstrap,
            SemaphoreSlim sendGate,
            CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _bootstrap = bootstrap;
                _generation = null;
                _nextSequence = 0;
                _committed = false;
                _staged.Clear();
            }

            return SendSerializedAsync(
                socket,
                new StreamClientMessage(
                    OverlayTransportProtocol.Version,
                    OverlayTransportProtocol.SalesSubscribe,
                    SalesGeneration: bootstrap.Generation,
                    AfterSalesSequence: bootstrap.LatestSequence),
                sendGate,
                cancellationToken);
        }

        public void Commit(StreamServerMessage message)
        {
            SalesBootstrapResponse bootstrap;
            long latestSequence;
            SalesMutationEnvelope[] staged;
            lock (_sync)
            {
                if (_bootstrap is null ||
                    string.IsNullOrWhiteSpace(message.SalesGeneration) ||
                    message.SalesLatestSequence is not long receivedLatestSequence ||
                    !string.Equals(
                        _bootstrap.Generation,
                        message.SalesGeneration,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Incomplete sales ready message.");
                }

                bootstrap = _bootstrap;
                latestSequence = receivedLatestSequence;
                _generation = message.SalesGeneration;
                _nextSequence = bootstrap.LatestSequence + 1;
                _committed = true;
                staged = _staged
                    .Where(item => item.Sequence > bootstrap.LatestSequence)
                    .GroupBy(item => item.Sequence)
                    .Select(group => group.First())
                    .OrderBy(item => item.Sequence)
                    .ToArray();
                _staged.Clear();
            }

            _owner.SalesReady?.Invoke(bootstrap with { LatestSequence = latestSequence });
            foreach (var envelope in staged)
            {
                Deliver(envelope);
            }
        }

        public void Accept(StreamServerMessage message)
        {
            if (message.SalesEvent is not { } envelope)
            {
                throw new InvalidDataException("Incomplete sales event.");
            }

            lock (_sync)
            {
                if (!_committed)
                {
                    if (_staged.Count >= MaximumStagedEvents)
                    {
                        throw new InvalidDataException("Staged sales bootstrap overflowed.");
                    }

                    _staged.Add(envelope);
                    return;
                }
            }

            Deliver(envelope);
        }

        public void Status(StreamServerMessage message)
        {
            _owner.SalesStreamStatusChanged?.Invoke(message.Type);
            if (message.Type == OverlayTransportProtocol.SalesResyncRequired)
            {
                _owner.ResyncRequired?.Invoke();
            }
        }

        private void Deliver(SalesMutationEnvelope envelope)
        {
            lock (_sync)
            {
                if (!string.Equals(_generation, envelope.Generation, StringComparison.Ordinal) ||
                    envelope.Sequence != _nextSequence)
                {
                    throw new InvalidDataException(
                        "Sales event sequence was duplicated, stale, or contained a gap.");
                }

                _nextSequence++;
            }

            _owner.SalesMutationReceived?.Invoke(envelope);
        }
    }

    private sealed class TransactionalChatSwitchState
    {
        private const int MaximumStagedEvents = 256;

        private readonly object _sync = new();
        private readonly LSOverlayRemoteClient _owner;
        private readonly Dictionary<long, ChatBootstrapResponse> _requested = new();
        private readonly Dictionary<long, List<ChatMutationEnvelope>> _staged = new();
        private long _latestRequested = -1;
        private long _committed = -1;
        private ulong? _channelId;
        private string? _generation;
        private long _nextSequence;

        public TransactionalChatSwitchState(LSOverlayRemoteClient owner)
        {
            _owner = owner;
        }

        public async Task RequestAsync(
            ClientWebSocket socket,
            ChatBootstrapResponse bootstrap,
            SemaphoreSlim sendGate,
            CancellationToken cancellationToken)
        {
            long switchGeneration;
            lock (_sync)
            {
                switchGeneration = checked(++_latestRequested);
                _requested[switchGeneration] = bootstrap;
                _staged[switchGeneration] = new List<ChatMutationEnvelope>();
                foreach (var stale in _requested.Keys
                             .Where(value => value < _latestRequested - 4)
                             .ToArray())
                {
                    _requested.Remove(stale);
                    _staged.Remove(stale);
                }
            }

            await SendSerializedAsync(socket, new StreamClientMessage(
                OverlayTransportProtocol.Version,
                OverlayTransportProtocol.ChatSubscribe,
                ChannelId: bootstrap.Channel.ChannelId,
                ChatGeneration: bootstrap.Generation,
                AfterChatSequence: bootstrap.LatestSequence,
                SwitchGeneration: switchGeneration), sendGate, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Commit(StreamServerMessage message)
        {
            if (message.SwitchGeneration is not long switchGeneration ||
                message.ChannelId is not ulong channelId ||
                string.IsNullOrWhiteSpace(message.ChatGeneration) ||
                message.ChatLatestSequence is not long latestSequence)
            {
                throw new InvalidDataException("Incomplete chat ready message.");
            }

            ChatBootstrapResponse bootstrap;
            ChatMutationEnvelope[] staged;
            lock (_sync)
            {
                if (switchGeneration != _latestRequested ||
                    !_requested.TryGetValue(switchGeneration, out bootstrap!))
                {
                    return;
                }

                if (bootstrap.Channel.ChannelId != channelId ||
                    !string.Equals(bootstrap.Generation, message.ChatGeneration,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Chat ready does not match staged switch.");
                }

                _committed = switchGeneration;
                _channelId = channelId;
                _generation = message.ChatGeneration;
                _nextSequence = bootstrap.LatestSequence + 1;
                staged = _staged[switchGeneration].ToArray();
                _requested.Clear();
                _staged.Clear();
            }

            _owner.ChatChannelReady?.Invoke(bootstrap with
            {
                LatestSequence = latestSequence,
            });
            foreach (var envelope in staged.OrderBy(item => item.Sequence))
            {
                Deliver(envelope);
            }
        }

        public void Accept(StreamServerMessage message)
        {
            if (message.ChatEvent is not { } envelope ||
                message.SwitchGeneration is not long switchGeneration)
            {
                throw new InvalidDataException("Incomplete chat event.");
            }

            lock (_sync)
            {
                if (switchGeneration == _committed)
                {
                    // Deliver outside this lock after structural checks below.
                }
                else if (switchGeneration == _latestRequested &&
                         _staged.TryGetValue(switchGeneration, out var staged))
                {
                    if (staged.Count >= MaximumStagedEvents)
                    {
                        throw new InvalidDataException("Staged chat switch overflowed.");
                    }

                    staged.Add(envelope);
                    return;
                }
                else
                {
                    return;
                }
            }

            Deliver(envelope);
        }

        public void Status(StreamServerMessage message)
        {
            if (message.ChannelId is ulong channelId)
            {
                _owner.ChatStreamStatusChanged?.Invoke(channelId, message.Type);
            }

            if (message.Type == OverlayTransportProtocol.ChatResyncRequired)
            {
                _owner.ResyncRequired?.Invoke();
            }
        }

        private void Deliver(ChatMutationEnvelope envelope)
        {
            lock (_sync)
            {
                if (_channelId != envelope.ChannelId ||
                    !string.Equals(_generation, envelope.Generation,
                        StringComparison.Ordinal) ||
                    envelope.Sequence != _nextSequence)
                {
                    throw new InvalidDataException(
                        "Chat event sequence was duplicated, stale, or contained a gap.");
                }

                _nextSequence++;
            }

            _owner.ChatMutationReceived?.Invoke(envelope);
        }
    }

    private static async Task<StreamServerMessage?> ReceiveAsync(
        ClientWebSocket socket,
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
                    throw new InvalidDataException("Server stream message exceeded 16 KiB.");
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
                    throw new InvalidDataException("Server sent a binary stream message.");
                }

                count += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return JsonSerializer.Deserialize<StreamServerMessage>(
                rented.AsSpan(0, count),
                OverlayProtocolJson.Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Server sent malformed stream JSON.", exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }
}
