using System.Diagnostics;
using System.Text.Json;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Discord.Authentication;
using GachaOverlay.Infrastructure.Discord.Channels;
using GachaOverlay.Infrastructure.Discord.Forward;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Discord.Process;
using GachaOverlay.Infrastructure.Discord.Rpc;

namespace GachaOverlay.Infrastructure.Discord.Connection;

public sealed class DiscordConnectionCoordinator : IDiscordConnectionService
{
    private static readonly TimeSpan OpaqueHydrationDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan OpaqueHydrationRetryDelay = TimeSpan.FromMilliseconds(650);

    private static readonly string[] SubscriptionEvents =
    {
        "MESSAGE_CREATE",
        "MESSAGE_UPDATE",
        "MESSAGE_DELETE",
    };

    private readonly object _sync = new();
    private readonly IDiscordProcessService _processService;
    private readonly IDiscordCredentialProvider _credentialProvider;
    private readonly IDiscordRpcClientFactory _rpcClientFactory;
    private readonly IDiscordAuthenticationService _authenticationService;
    private readonly IDiscordChannelResolver _channelResolver;
    private readonly IDiscordMessageNormalizer _normalizer;
    private readonly DiscordMessagePipeline _messagePipeline;
    private readonly Func<DiscordTargetOptions> _targetOptionsProvider;
    private readonly IReconnectDelayStrategy _reconnectDelay;
    private readonly IAppLogger _logger;
    private readonly ForwardMessageResolver _forwardResolver;
    private readonly IDiscordOpaqueMessageResolver? _opaqueMessageResolver;
    private readonly Func<bool> _discordAutoLaunchEnabledProvider;
    private readonly IRuntimeMetrics? _metrics;
    private readonly Dictionary<string, ForwardLookupRegistration> _forwardLookups =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpaqueHydrationRegistration> _opaqueHydrations =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _manualReconnectSignal = new(0, 1);
    private readonly SemaphoreSlim _mainSwitchGate = new(1, 1);
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _attemptCancellation;
    private Task? _workerTask;
    private DiscordConnectionStatus _status = DiscordConnectionStatus.Initial;
    private DiscordAuthenticatedUser? _authenticatedUser;
    private long _generation;
    private int _started;
    private int _disposed;
    private long _forwardLookupRevision;
    private long _opaqueHydrationRevision;
    private long _mainSwitchRevision;
    private IDiscordRpcClient? _activeClient;
    private DiscordTargetChannels? _activeTargets;
    private long _activeGeneration;
    private CancellationToken _activeSessionCancellation;
    private int _autoLaunchAttempted;
    private long _connectedStartedTimestamp;
    private long _connectedElapsedStopwatchTicks;

    public DiscordConnectionCoordinator(
        IDiscordProcessService processService,
        IDiscordCredentialProvider credentialProvider,
        IDiscordRpcClientFactory rpcClientFactory,
        IDiscordAuthenticationService authenticationService,
        IDiscordChannelResolver channelResolver,
        IDiscordMessageNormalizer normalizer,
        DiscordMessagePipeline messagePipeline,
        DiscordTargetOptions targetOptions,
        IReconnectDelayStrategy reconnectDelay,
        IAppLogger logger,
        IDiscordOpaqueMessageResolver? opaqueMessageResolver = null,
        Func<bool>? discordAutoLaunchEnabledProvider = null,
        IRuntimeMetrics? metrics = null)
        : this(
            processService,
            credentialProvider,
            rpcClientFactory,
            authenticationService,
            channelResolver,
            normalizer,
            messagePipeline,
            () => targetOptions,
            reconnectDelay,
            logger,
            opaqueMessageResolver,
            discordAutoLaunchEnabledProvider,
            metrics)
    {
    }

    public DiscordConnectionCoordinator(
        IDiscordProcessService processService,
        IDiscordCredentialProvider credentialProvider,
        IDiscordRpcClientFactory rpcClientFactory,
        IDiscordAuthenticationService authenticationService,
        IDiscordChannelResolver channelResolver,
        IDiscordMessageNormalizer normalizer,
        DiscordMessagePipeline messagePipeline,
        Func<DiscordTargetOptions> targetOptionsProvider,
        IReconnectDelayStrategy reconnectDelay,
        IAppLogger logger,
        IDiscordOpaqueMessageResolver? opaqueMessageResolver = null,
        Func<bool>? discordAutoLaunchEnabledProvider = null,
        IRuntimeMetrics? metrics = null)
    {
        _processService = processService;
        _credentialProvider = credentialProvider;
        _rpcClientFactory = rpcClientFactory;
        _authenticationService = authenticationService;
        _channelResolver = channelResolver;
        _normalizer = normalizer;
        _messagePipeline = messagePipeline;
        _targetOptionsProvider = targetOptionsProvider;
        _reconnectDelay = reconnectDelay;
        _logger = logger;
        _forwardResolver = new ForwardMessageResolver(normalizer, logger);
        _opaqueMessageResolver = opaqueMessageResolver;
        _discordAutoLaunchEnabledProvider = discordAutoLaunchEnabledProvider ?? (() => false);
        _metrics = metrics;
        _messagePipeline.StateChanged += OnMessageStateChanged;
    }

    public event Action<DiscordConnectionStatus>? StatusChanged;

    public event Action<DiscordMessageState>? MessageStateChanged;

    public event Action<DiscordTargetChannels>? TargetChannelsResolved;

    public event Action<DiscordAuthenticatedUser>? AuthenticatedUserChanged;

    public DiscordConnectionStatus Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public DiscordMessageState MessageState => _messagePipeline.Current;

    public DiscordAuthenticatedUser? AuthenticatedUser
    {
        get
        {
            lock (_sync)
            {
                return _authenticatedUser;
            }
        }
    }

    public void RefreshRuntimeMetrics()
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        var elapsedTicks = Interlocked.Read(ref _connectedElapsedStopwatchTicks);
        var connectedStarted = Interlocked.Read(ref _connectedStartedTimestamp);
        if (connectedStarted != 0)
        {
            elapsedTicks += Math.Max(0, nowTimestamp - connectedStarted);
        }

        _metrics?.SetGauge(
            RuntimeMetricNames.RpcConnectedDurationSeconds,
            (double)elapsedTicks / Stopwatch.Frequency);
    }

    public void Start(CancellationToken applicationStopping)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The Discord coordinator has already started.");
        }

        _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        _workerTask = Task.Run(() => RunAsync(_runCancellation.Token));
    }

    public void RequestReconnect()
    {
        if (_disposed != 0)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                _attemptCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            _manualReconnectSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task<MainChannelSwitchResult> SwitchMainChannelAsync(
        DiscordMainChannelOption channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var revision = Interlocked.Increment(ref _mainSwitchRevision);
        await _mainSwitchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? subscribedChannelId = null;
        IDiscordRpcClient? client = null;
        try
        {
            if (revision != Volatile.Read(ref _mainSwitchRevision))
            {
                return new MainChannelSwitchResult(MainChannelSwitchStatus.Superseded);
            }

            DiscordTargetChannels targets;
            long generation;
            CancellationToken sessionCancellation;
            lock (_sync)
            {
                if (_activeClient is null || _activeTargets is null ||
                    _status.State != DiscordConnectionState.Connected)
                {
                    return new MainChannelSwitchResult(MainChannelSwitchStatus.NotConnected);
                }

                client = _activeClient;
                targets = _activeTargets;
                generation = _activeGeneration;
                sessionCancellation = _activeSessionCancellation;
            }

            if (string.Equals(targets.MainChannelId, channel.ChannelId, StringComparison.Ordinal))
            {
                return new MainChannelSwitchResult(
                    MainChannelSwitchStatus.NoChange,
                    channel.ChannelId,
                    channel.Name);
            }

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                sessionCancellation);
            _metrics?.Increment(RuntimeMetricNames.RpcGetChannels);
            var availableResponse = await client.CommandAsync(
                    "GET_CHANNELS",
                    new { guild_id = ProductionServerProfile.GuildId },
                    cancellationToken: operation.Token)
                .ConfigureAwait(false);
            DiscordRpcProtocol.EnsureSuccess(availableResponse);
            var selectable = ParseSelectableMainChannels(availableResponse);
            var validated = selectable.SingleOrDefault(candidate => string.Equals(
                candidate.ChannelId,
                channel.ChannelId,
                StringComparison.Ordinal));
            if (validated is null)
            {
                return new MainChannelSwitchResult(MainChannelSwitchStatus.InvalidChannel);
            }

            await SubscribeChannelAsync(client, validated.ChannelId, operation.Token)
                .ConfigureAwait(false);
            subscribedChannelId = validated.ChannelId;
            var response = await GetChannelAsync(client, validated.ChannelId, operation.Token)
                .ConfigureAwait(false);
            var snapshot = NormalizeSnapshotWithMetrics(
                response,
                validated.ChannelId,
                ProductionServerProfile.GuildId);

            DiscordTargetChannels nextTargets;
            lock (_sync)
            {
                if (revision != Volatile.Read(ref _mainSwitchRevision) ||
                    !ReferenceEquals(client, _activeClient) ||
                    generation != _activeGeneration ||
                    _activeTargets is null ||
                    !string.Equals(
                        _activeTargets.MainChannelId,
                        targets.MainChannelId,
                        StringComparison.Ordinal))
                {
                    return new MainChannelSwitchResult(MainChannelSwitchStatus.Superseded);
                }

                nextTargets = targets with
                {
                    MainChannelId = validated.ChannelId,
                    MainChannelName = validated.Name,
                };
                CancelForwardLookups(generation);
                CancelOpaqueHydrations(generation);
                if (!_messagePipeline.ReplaceMain(generation, nextTargets, snapshot))
                {
                    return new MainChannelSwitchResult(MainChannelSwitchStatus.Superseded);
                }

                _activeTargets = nextTargets;
            }

            subscribedChannelId = null;
            PublishTargets(nextTargets);
            StartForwardLookups(
                generation,
                client,
                nextTargets.MainChannelId,
                snapshot,
                sessionCancellation);
            await TryUnsubscribeChannelAsync(client, targets.MainChannelId, operation.Token)
                .ConfigureAwait(false);
            _logger.Information(
                "RPC",
                $"Main channel switched channel_id={validated.ChannelId} generation={generation}.");
            return new MainChannelSwitchResult(
                MainChannelSwitchStatus.Succeeded,
                validated.ChannelId,
                validated.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new MainChannelSwitchResult(MainChannelSwitchStatus.Superseded);
        }
        catch (Exception exception)
        {
            _logger.Warning(
                "RPC",
                $"Main channel switch failed ({exception.GetType().Name}); current channel was retained.");
            return new MainChannelSwitchResult(MainChannelSwitchStatus.Failed);
        }
        finally
        {
            if (subscribedChannelId is not null && client is not null)
            {
                await TryUnsubscribeChannelAsync(
                        client,
                        subscribedChannelId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            _mainSwitchGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _runCancellation?.Cancel();
        lock (_sync)
        {
            try
            {
                _attemptCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _messagePipeline.StateChanged -= OnMessageStateChanged;
        _attemptCancellation?.Dispose();
        _runCancellation?.Dispose();
        _manualReconnectSignal.Dispose();
        _mainSwitchGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_credentialProvider.TryGetCredentials(out var credentials) || credentials is null)
                {
                    SetStatus(
                        DiscordConnectionState.ConfigurationRequired,
                        _generation,
                        "CredentialsMissing");
                    _logger.Warning(
                        "RPC",
                        "Discord connection configuration is missing; waiting for setup or reconnect.");
                    await _manualReconnectSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!_processService.IsDiscordRunning())
                {
                    var statusDetail = "DiscordNotRunning";
                    if (_discordAutoLaunchEnabledProvider() &&
                        Interlocked.Exchange(ref _autoLaunchAttempted, 1) == 0)
                    {
                        var launched = _processService.TryLaunchDiscord();
                        _logger.Information(
                            "DISCORD",
                            $"Auto-launch attempted result={(launched ? "Started" : "Failed")}.");
                        if (!launched)
                        {
                            statusDetail = "DiscordAutoLaunchFailed";
                        }
                    }

                    SetStatus(DiscordConnectionState.Disconnected, _generation, statusDetail);
                    _logger.Information("DISCORD", "Discord process is not running; waiting.");
                    await _processService.WaitUntilDiscordIsRunningAsync(cancellationToken)
                        .ConfigureAwait(false);
                    _logger.Information("DISCORD", "Process detected.");
                    consecutiveFailures = 0;
                }

                var generation = Interlocked.Increment(ref _generation);
                _forwardResolver.BeginGeneration(generation);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                lock (_sync)
                {
                    _attemptCancellation?.Dispose();
                    _attemptCancellation = attempt;
                }

                var manuallyRestarted = false;
                var configurationRequired = false;
                var client = _rpcClientFactory.Create();
                string? guildIdHint = null;
                string? mainChannelId = null;
                Action<JsonElement> dispatchHandler = payload =>
                    HandleDispatch(
                        generation,
                        client,
                        payload,
                        guildIdHint,
                        mainChannelId,
                        attempt.Token);
                client.DispatchReceived += dispatchHandler;

                try
                {
                    SetStatus(
                        consecutiveFailures == 0
                            ? DiscordConnectionState.Connecting
                            : DiscordConnectionState.Reconnecting,
                        generation,
                        "OpeningPipe");
                    _logger.Information("RPC", $"Connecting generation={generation}.");
                    var pipeName = await client.ConnectAsync(attempt.Token).ConfigureAwait(false);
                    _logger.Information("RPC", $"Connected pipe={pipeName}.");

                    await client.HandshakeAsync(credentials.ClientId, attempt.Token)
                        .ConfigureAwait(false);
                    _logger.Information("RPC", "Handshake READY received.");

                    SetStatus(
                        DiscordConnectionState.Authenticating,
                        generation,
                        "OAuthAuthenticate");
                    _logger.Information("RPC", "Authenticating.");
                    var identity = await _authenticationService.AuthenticateAsync(
                            client,
                            credentials,
                            attempt.Token)
                        .ConfigureAwait(false);
                    _logger.Information("RPC", $"Authenticated user_id={identity.UserId}.");
                    PublishAuthenticatedUser(new DiscordAuthenticatedUser(
                        identity.UserId,
                        identity.Username));

                    var targets = await _channelResolver.ResolveAsync(
                            client,
                            _targetOptionsProvider(),
                            attempt.Token)
                        .ConfigureAwait(false);
                    guildIdHint = targets.GuildId;
                    mainChannelId = targets.MainChannelId;
                    _logger.Information("RPC", $"Target guild resolved id={targets.GuildId}.");
                    _logger.Information(
                        "RPC",
                        $"#{targets.MainChannelName} resolved id={targets.MainChannelId}.");
                    _logger.Information(
                        "RPC",
                        $"#{targets.SalesChannelName} resolved id={targets.SalesChannelId}.");
                    PublishTargets(targets);

                    if (!_messagePipeline.StartBootstrap(generation, targets))
                    {
                        throw new OperationCanceledException("A newer bootstrap generation is active.");
                    }

                    await SubscribeAsync(client, targets, attempt.Token).ConfigureAwait(false);
                    _logger.Information("RPC", $"Bootstrap started generation={generation}.");

                    var mainTask = GetChannelAsync(client, targets.MainChannelId, attempt.Token);
                    var salesTask = GetChannelAsync(client, targets.SalesChannelId, attempt.Token);
                    await Task.WhenAll(mainTask, salesTask).ConfigureAwait(false);

                    var mainSnapshot = NormalizeSnapshotWithMetrics(
                        await mainTask.ConfigureAwait(false),
                        targets.MainChannelId,
                        targets.GuildId);
                    var salesSnapshot = NormalizeSnapshotWithMetrics(
                        await salesTask.ConfigureAwait(false),
                        targets.SalesChannelId,
                        targets.GuildId);
                    _logger.Information("RPC", $"Snapshot #main count={mainSnapshot.Count}.");
                    _logger.Information("RPC", $"Snapshot #sales count={salesSnapshot.Count}.");

                    if (!_messagePipeline.CompleteBootstrap(
                            generation,
                            mainSnapshot,
                            salesSnapshot))
                    {
                        throw new OperationCanceledException("Bootstrap result became stale.");
                    }

                    _logger.Information("RPC", $"Bootstrap completed generation={generation}.");
                    StartForwardLookups(
                        generation,
                        client,
                        targets.MainChannelId,
                        mainSnapshot,
                        attempt.Token);
                    lock (_sync)
                    {
                        _activeClient = client;
                        _activeTargets = targets;
                        _activeGeneration = generation;
                        _activeSessionCancellation = attempt.Token;
                    }
                    SetStatus(DiscordConnectionState.Connected, generation, "LiveAndBootstrapped");
                    consecutiveFailures = 0;

                    var disconnectReason = await client.WaitForDisconnectAsync(attempt.Token)
                        .ConfigureAwait(false);
                    throw disconnectReason ?? new IOException("Discord RPC connection ended.");
                }
                catch (OperationCanceledException) when (
                    attempt.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
                {
                    manuallyRestarted = true;
                    _messagePipeline.AbortBootstrap(generation);
                    SetStatus(DiscordConnectionState.Reconnecting, generation, "ReconnectRequested");
                    _logger.Information("RPC", "Reconnect requested.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _messagePipeline.AbortBootstrap(generation);
                    break;
                }
                catch (DiscordAuthenticationRequiredException exception)
                {
                    _messagePipeline.AbortBootstrap(generation);
                    consecutiveFailures = 0;
                    configurationRequired = true;
                    SetStatus(
                        DiscordConnectionState.ConfigurationRequired,
                        generation,
                        "AuthenticationRequired");
                    _logger.Warning(
                        "AUTH",
                        $"Discord authentication requires user action ({exception.GetType().Name}).");
                }
                catch (DiscordChannelResolutionException exception)
                {
                    _messagePipeline.AbortBootstrap(generation);
                    consecutiveFailures = 0;
                    configurationRequired = true;
                    SetStatus(
                        DiscordConnectionState.ConfigurationRequired,
                        generation,
                        "TargetConfigurationInvalid");
                    _logger.Warning(
                        "RPC",
                        $"Discord Guild/Channel configuration requires user action ({exception.GetType().Name}).");
                }
                catch (Exception exception)
                {
                    _messagePipeline.AbortBootstrap(generation);
                    consecutiveFailures++;
                    _metrics?.Increment(RuntimeMetricNames.RpcConnectionErrors);
                    SetStatus(DiscordConnectionState.Reconnecting, generation, "TransientFailure");
                    _logger.Error(
                        "RPC",
                        $"Connection generation {generation} failed; reconnect scheduled.",
                        exception);
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_activeClient, client))
                        {
                            _activeClient = null;
                            _activeTargets = null;
                            _activeGeneration = 0;
                            _activeSessionCancellation = default;
                        }
                    }
                    CancelForwardLookups(generation);
                    CancelOpaqueHydrations(generation);
                    client.DispatchReceived -= dispatchHandler;
                    await client.DisposeAsync().ConfigureAwait(false);
                    lock (_sync)
                    {
                        if (ReferenceEquals(_attemptCancellation, attempt))
                        {
                            _attemptCancellation = null;
                        }
                    }
                }

                if (configurationRequired && !cancellationToken.IsCancellationRequested)
                {
                    await _manualReconnectSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!manuallyRestarted && !cancellationToken.IsCancellationRequested)
                {
                    await _reconnectDelay.DelayAsync(consecutiveFailures, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            SetStatus(DiscordConnectionState.Disconnected, _generation, "Stopped");
            _logger.Information("RPC", "Connection coordinator stopped.");
        }
    }

    private async Task SubscribeAsync(
        IDiscordRpcClient client,
        DiscordTargetChannels targets,
        CancellationToken cancellationToken)
    {
        foreach (var channelId in new[] { targets.MainChannelId, targets.SalesChannelId })
        {
            foreach (var eventName in SubscriptionEvents)
            {
                var response = await client.SubscribeAsync(
                        eventName,
                        new { channel_id = channelId },
                        cancellationToken)
                    .ConfigureAwait(false);
                DiscordRpcProtocol.EnsureSuccess(response);
                _logger.Information(
                    "RPC",
                    $"Subscribed {eventName} channel_id={channelId}.");
            }
        }
    }

    private async Task SubscribeChannelAsync(
        IDiscordRpcClient client,
        string channelId,
        CancellationToken cancellationToken)
    {
        foreach (var eventName in SubscriptionEvents)
        {
            var response = await client.SubscribeAsync(
                    eventName,
                    new { channel_id = channelId },
                    cancellationToken)
                .ConfigureAwait(false);
            DiscordRpcProtocol.EnsureSuccess(response);
        }
    }

    private async Task TryUnsubscribeChannelAsync(
        IDiscordRpcClient client,
        string channelId,
        CancellationToken cancellationToken)
    {
        foreach (var eventName in SubscriptionEvents)
        {
            try
            {
                var response = await client.UnsubscribeAsync(
                        eventName,
                        new { channel_id = channelId },
                        cancellationToken)
                    .ConfigureAwait(false);
                DiscordRpcProtocol.EnsureSuccess(response);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.Warning(
                    "RPC",
                    $"Unsubscribe {eventName} channel_id={channelId} failed ({exception.GetType().Name}); ignored events remain filtered by channel identity.");
            }
        }
    }

    private async Task<JsonElement> GetChannelAsync(
        IDiscordRpcClient client,
        string channelId,
        CancellationToken cancellationToken)
    {
        _metrics?.Increment(RuntimeMetricNames.RpcGetChannel);
        var response = await client.CommandAsync(
                "GET_CHANNEL",
                new { channel_id = channelId },
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        DiscordRpcProtocol.EnsureSuccess(response);
        return response;
    }

    private IReadOnlyList<DiscordMessagePatch> NormalizeSnapshotWithMetrics(
        JsonElement response,
        string channelId,
        string guildId)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return _normalizer.NormalizeSnapshot(response, channelId, guildId);
        }
        finally
        {
            _metrics?.RecordDuration(
                RuntimeMetricNames.ChatNormalizationDuration,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private void HandleDispatch(
        long generation,
        IDiscordRpcClient client,
        JsonElement payload,
        string? guildIdHint,
        string? mainChannelId,
        CancellationToken generationCancellation)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var normalizationStarted = Stopwatch.GetTimestamp();
            var normalized = _normalizer.TryNormalizeDispatch(
                    payload,
                    out var mutation,
                    out var eventName,
                    guildIdHint);
            _metrics?.RecordDuration(
                RuntimeMetricNames.ChatNormalizationDuration,
                Stopwatch.GetElapsedTime(normalizationStarted));
            if (!normalized ||
                mutation is null)
            {
                _logger.Warning("RPC", "A malformed or unsupported dispatch was ignored.");
                return;
            }

            _metrics?.Increment(eventName switch
            {
                "MESSAGE_CREATE" => RuntimeMetricNames.RpcMessageCreate,
                "MESSAGE_UPDATE" => RuntimeMetricNames.RpcMessageUpdate,
                "MESSAGE_DELETE" => RuntimeMetricNames.RpcMessageDelete,
                _ => "rpc.message.other.count",
            });

            if (mutation.Kind is DiscordMessageMutationKind.Update or DiscordMessageMutationKind.Delete)
            {
                CancelForwardLookup(mutation.MessageId);
                CancelOpaqueHydration(mutation.MessageId);
            }

            if (!_messagePipeline.ReceiveLive(generation, mutation))
            {
                _metrics?.Increment(RuntimeMetricNames.RpcStaleDiscards);
                return;
            }

            _logger.Information(
                "RPC",
                $"{eventName} id={mutation.MessageId} channel_id={mutation.ChannelId ?? "unknown"}.");

            var activeMainChannelId = _messagePipeline.Targets?.MainChannelId ?? mainChannelId;
            if (!string.IsNullOrWhiteSpace(activeMainChannelId) &&
                string.Equals(mutation.ChannelId, activeMainChannelId, StringComparison.Ordinal) &&
                mutation.Patch?.Forward.HasValue == true &&
                mutation.Patch.Forward.Value?.RequiresLookup == true)
            {
                StartForwardLookup(
                    generation,
                    client,
                    mutation.Patch,
                    generationCancellation);
            }
            else if (!string.IsNullOrWhiteSpace(activeMainChannelId) &&
                     string.Equals(mutation.ChannelId, activeMainChannelId, StringComparison.Ordinal) &&
                     mutation.Patch?.FallbackKind.HasValue == true &&
                     mutation.Patch.FallbackKind.Value == DiscordMessageFallbackKind.PendingHydration)
            {
                StartOpaqueHydration(
                    generation,
                    client,
                    mutation.Patch,
                    generationCancellation);
            }
        }
        finally
        {
            _metrics?.RecordDuration(
                RuntimeMetricNames.RpcEventDuration,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private static IReadOnlyList<DiscordMainChannelOption> ParseSelectableMainChannels(
        JsonElement response)
    {
        if (!response.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("channels", out var channels) ||
            channels.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("GET_CHANNELS returned no channel array.");
        }

        return channels.EnumerateArray()
            .Where(channel => DiscordJson.GetInt32(channel, "type") == 0)
            .Select(channel => new DiscordMainChannelOption(
                DiscordJson.GetString(channel, "id") ?? string.Empty,
                DiscordJson.GetString(channel, "name") ?? string.Empty))
            .Where(channel =>
                !string.IsNullOrWhiteSpace(channel.ChannelId) &&
                !string.IsNullOrWhiteSpace(channel.Name) &&
                !string.Equals(
                    channel.ChannelId,
                    ProductionServerProfile.SalesChannelId,
                    StringComparison.Ordinal))
            .OrderBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private void StartOpaqueHydration(
        long generation,
        IDiscordRpcClient client,
        DiscordMessagePatch wrapper,
        CancellationToken generationCancellation)
    {
        if (!wrapper.ChannelId.HasValue ||
            string.IsNullOrWhiteSpace(wrapper.ChannelId.Value) ||
            !wrapper.GuildId.HasValue ||
            string.IsNullOrWhiteSpace(wrapper.GuildId.Value))
        {
            ApplyOpaqueFallback(
                generation,
                wrapper.MessageId,
                wrapper.ChannelId,
                DiscordMessageFallbackKind.Message);
            return;
        }

        CancelOpaqueHydration(wrapper.MessageId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(generationCancellation);
        var registration = new OpaqueHydrationRegistration(
            wrapper.MessageId,
            wrapper.ChannelId.Value,
            wrapper.GuildId.Value,
            generation,
            Interlocked.Increment(ref _opaqueHydrationRevision),
            cancellation);
        lock (_sync)
        {
            _opaqueHydrations[wrapper.MessageId] = registration;
        }
        _metrics?.Increment(RuntimeMetricNames.OpaqueAttempts);

        _logger.Information(
            "OPAQUE",
            $"wrapper={wrapper.MessageId} resolution=PendingHydration channel={wrapper.ChannelId.Value}.");
        _ = HydrateOpaqueWrapperAsync(client, registration, generationCancellation);
    }

    private async Task HydrateOpaqueWrapperAsync(
        IDiscordRpcClient client,
        OpaqueHydrationRegistration registration,
        CancellationToken generationCancellation)
    {
        try
        {
            await Task.Delay(OpaqueHydrationDelay, registration.Cancellation.Token)
                .ConfigureAwait(false);
            var response = await GetChannelAsync(
                    client,
                    registration.WrapperChannelId,
                    registration.Cancellation.Token)
                .ConfigureAwait(false);
            var hydrated = NormalizeSnapshotWithMetrics(
                    response,
                    registration.WrapperChannelId,
                    registration.GuildId)
                .FirstOrDefault(patch => string.Equals(
                    patch.MessageId,
                    registration.WrapperMessageId,
                    StringComparison.Ordinal));
            if (IsOpaqueSnapshotFallback(hydrated) && TryCompleteOpaqueHydration(registration))
            {
                _logger.Information(
                    "OPAQUE",
                    $"wrapper={registration.WrapperMessageId} resolution=RetryScheduled.");
                await Task.Delay(OpaqueHydrationRetryDelay, registration.Cancellation.Token)
                    .ConfigureAwait(false);
                response = await GetChannelAsync(
                        client,
                        registration.WrapperChannelId,
                        registration.Cancellation.Token)
                    .ConfigureAwait(false);
                hydrated = NormalizeSnapshotWithMetrics(
                        response,
                        registration.WrapperChannelId,
                        registration.GuildId)
                    .FirstOrDefault(patch => string.Equals(
                        patch.MessageId,
                        registration.WrapperMessageId,
                        StringComparison.Ordinal));
            }

            if (IsOpaqueSnapshotFallback(hydrated) &&
                TryCompleteOpaqueHydration(registration))
            {
                var resolution = _opaqueMessageResolver is null
                    ? new DiscordOpaqueMessageResolution(
                        DiscordOpaqueMessageResolutionKind.Unknown)
                    : await _opaqueMessageResolver.ResolveAsync(
                            registration.WrapperChannelId,
                            registration.WrapperMessageId,
                            registration.Cancellation.Token)
                        .ConfigureAwait(false);
                hydrated = CreateOpaqueResolutionPatch(registration, resolution);
                _logger.Information(
                    "OPAQUE",
                    $"wrapper={registration.WrapperMessageId} resolution=Uia{resolution.Kind} " +
                    $"contentLength={resolution.Content?.Length ?? 0}.");
            }

            if (!TryCompleteOpaqueHydration(registration))
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                _logger.Information(
                    "OPAQUE",
                    $"wrapper={registration.WrapperMessageId} resolution=StaleIgnored.");
                return;
            }

            if (hydrated is null)
            {
                var fallback = CreateOpaqueFallbackPatch(
                    registration.WrapperMessageId,
                    OptionalValue<string>.From(registration.WrapperChannelId),
                    DiscordMessageFallbackKind.Message);
                if (!TryCommitOpaqueHydration(registration, fallback))
                {
                    _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                    _logger.Information(
                        "OPAQUE",
                        $"wrapper={registration.WrapperMessageId} resolution=StaleIgnored.");
                    return;
                }

                _logger.Information(
                    "OPAQUE",
                    $"wrapper={registration.WrapperMessageId} resolution=SnapshotMissing fallback=Message.");
                return;
            }

            if (!TryCommitOpaqueHydration(registration, hydrated))
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                _logger.Information(
                    "OPAQUE",
                    $"wrapper={registration.WrapperMessageId} resolution=StaleIgnored.");
                return;
            }

            _metrics?.Increment(RuntimeMetricNames.OpaqueSucceeded);

            _logger.Information(
                "OPAQUE",
                $"wrapper={registration.WrapperMessageId} resolution=SnapshotHydrated " +
                $"fallback={(hydrated.FallbackKind.HasValue ? hydrated.FallbackKind.Value : DiscordMessageFallbackKind.None)}.");

            if (hydrated.Forward.HasValue && hydrated.Forward.Value?.RequiresLookup == true)
            {
                StartForwardLookup(
                    registration.Generation,
                    client,
                    hydrated,
                    generationCancellation);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information(
                "OPAQUE",
                $"wrapper={registration.WrapperMessageId} resolution=Cancelled.");
        }
        catch (Exception exception)
        {
            _metrics?.Increment(RuntimeMetricNames.OpaqueFailed);
            if (TryCompleteOpaqueHydration(registration))
            {
                _ = TryCommitOpaqueHydration(
                    registration,
                    CreateOpaqueFallbackPatch(
                    registration.WrapperMessageId,
                    OptionalValue<string>.From(registration.WrapperChannelId),
                    DiscordMessageFallbackKind.Message));
            }

            _logger.Warning(
                "OPAQUE",
                $"wrapper={registration.WrapperMessageId} resolution=Failed " +
                $"fallback=Message reason={exception.GetType().Name}.");
        }
        finally
        {
            lock (_sync)
            {
                if (_opaqueHydrations.TryGetValue(
                        registration.WrapperMessageId,
                        out var current) &&
                    current.Revision == registration.Revision)
                {
                    _opaqueHydrations.Remove(registration.WrapperMessageId);
                }
            }

            registration.Cancellation.Dispose();
        }
    }

    private bool TryCompleteOpaqueHydration(OpaqueHydrationRegistration registration)
    {
        lock (_sync)
        {
            if (!_opaqueHydrations.TryGetValue(
                    registration.WrapperMessageId,
                    out var current) ||
                current.Revision != registration.Revision ||
                current.Generation != registration.Generation ||
                registration.Cancellation.IsCancellationRequested)
            {
                return false;
            }
        }

        var state = _messagePipeline.Current;
        return state.Generation == registration.Generation &&
            state.MainChat.Any(message =>
                string.Equals(
                    message.MessageId,
                    registration.WrapperMessageId,
                    StringComparison.Ordinal) &&
                message.FallbackKind == DiscordMessageFallbackKind.PendingHydration);
    }

    private bool TryCommitOpaqueHydration(
        OpaqueHydrationRegistration registration,
        DiscordMessagePatch patch)
    {
        // Validation and mutation share the coordinator lock so a live
        // UPDATE/DELETE cannot cancel this registration between the final
        // authority check and the pipeline commit.
        lock (_sync)
        {
            if (!_opaqueHydrations.TryGetValue(
                    registration.WrapperMessageId,
                    out var current) ||
                current.Revision != registration.Revision ||
                current.Generation != registration.Generation ||
                registration.Cancellation.IsCancellationRequested)
            {
                return false;
            }

            var state = _messagePipeline.Current;
            if (state.Generation != registration.Generation ||
                !state.MainChat.Any(message =>
                    string.Equals(
                        message.MessageId,
                        registration.WrapperMessageId,
                        StringComparison.Ordinal) &&
                    message.FallbackKind == DiscordMessageFallbackKind.PendingHydration))
            {
                return false;
            }

            return _messagePipeline.ReceiveLive(
                registration.Generation,
                DiscordMessageMutation.Update(patch));
        }
    }

    private static bool IsOpaqueSnapshotFallback(DiscordMessagePatch? patch) =>
        patch is null ||
        patch.FallbackKind.HasValue &&
        patch.FallbackKind.Value is
            DiscordMessageFallbackKind.Message or
            DiscordMessageFallbackKind.Sticker;

    private void ApplyOpaqueFallback(
        long generation,
        string wrapperMessageId,
        OptionalValue<string> channelId,
        DiscordMessageFallbackKind fallbackKind)
    {
        _messagePipeline.ReceiveLive(
            generation,
            DiscordMessageMutation.Update(CreateOpaqueFallbackPatch(
                wrapperMessageId,
                channelId,
                fallbackKind)));
    }

    private static DiscordMessagePatch CreateOpaqueFallbackPatch(
        string wrapperMessageId,
        OptionalValue<string> channelId,
        DiscordMessageFallbackKind fallbackKind) =>
        new(wrapperMessageId)
        {
            ChannelId = channelId,
            FallbackKind = OptionalValue<DiscordMessageFallbackKind>.From(
                fallbackKind),
        };

    private static DiscordMessagePatch CreateOpaqueResolutionPatch(
        OpaqueHydrationRegistration registration,
        DiscordOpaqueMessageResolution resolution)
    {
        var fallbackKind = resolution.Kind switch
        {
            DiscordOpaqueMessageResolutionKind.ForwardedMessage =>
                DiscordMessageFallbackKind.ForwardedMessage,
            DiscordOpaqueMessageResolutionKind.Sticker =>
                DiscordMessageFallbackKind.Sticker,
            DiscordOpaqueMessageResolutionKind.Unknown =>
                DiscordMessageFallbackKind.Message,
            _ => DiscordMessageFallbackKind.None,
        };
        var forward = resolution.Kind switch
        {
            DiscordOpaqueMessageResolutionKind.ForwardedText =>
                new DiscordForwardMetadata(
                    DiscordForwardResolutionMode.FlattenedPayload,
                    null,
                    false),
            DiscordOpaqueMessageResolutionKind.ForwardedMessage =>
                new DiscordForwardMetadata(
                    DiscordForwardResolutionMode.Fallback,
                    null,
                    false),
            _ => null,
        };
        return new DiscordMessagePatch(registration.WrapperMessageId)
        {
            ChannelId = OptionalValue<string>.From(registration.WrapperChannelId),
            GuildId = OptionalValue<string>.From(registration.GuildId),
            Content = resolution.Kind == DiscordOpaqueMessageResolutionKind.ForwardedText
                ? OptionalValue<string>.From(resolution.Content ?? string.Empty)
                : default,
            Forward = OptionalValue<DiscordForwardMetadata?>.From(forward),
            FallbackKind = OptionalValue<DiscordMessageFallbackKind>.From(fallbackKind),
        };
    }

    private void CancelOpaqueHydration(string wrapperMessageId)
    {
        OpaqueHydrationRegistration? registration = null;
        lock (_sync)
        {
            if (_opaqueHydrations.Remove(wrapperMessageId, out var removed))
            {
                registration = removed;
            }
        }

        registration?.Cancellation.Cancel();
    }

    private void CancelOpaqueHydrations(long generation)
    {
        OpaqueHydrationRegistration[] registrations;
        lock (_sync)
        {
            registrations = _opaqueHydrations.Values
                .Where(registration => registration.Generation == generation)
                .ToArray();
            foreach (var registration in registrations)
            {
                _opaqueHydrations.Remove(registration.WrapperMessageId);
            }
        }

        foreach (var registration in registrations)
        {
            registration.Cancellation.Cancel();
        }
    }

    private void StartForwardLookups(
        long generation,
        IDiscordRpcClient client,
        string mainChannelId,
        IEnumerable<DiscordMessagePatch> snapshot,
        CancellationToken generationCancellation)
    {
        foreach (var patch in snapshot.Where(patch =>
                     patch.ChannelId.HasValue &&
                     string.Equals(patch.ChannelId.Value, mainChannelId, StringComparison.Ordinal) &&
                     patch.Forward.HasValue &&
                     patch.Forward.Value?.RequiresLookup == true))
        {
            StartForwardLookup(generation, client, patch, generationCancellation);
        }
    }

    private void StartForwardLookup(
        long generation,
        IDiscordRpcClient client,
        DiscordMessagePatch wrapper,
        CancellationToken generationCancellation)
    {
        var sourceKey = wrapper.Forward.Value?.SourceKey;
        if (sourceKey is null ||
            !wrapper.ChannelId.HasValue ||
            string.IsNullOrWhiteSpace(wrapper.ChannelId.Value))
        {
            return;
        }

        CancelForwardLookup(wrapper.MessageId);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(generationCancellation);
        var registration = new ForwardLookupRegistration(
            wrapper.MessageId,
            wrapper.ChannelId.Value,
            sourceKey,
            generation,
            Interlocked.Increment(ref _forwardLookupRevision),
            cancellation);
        lock (_sync)
        {
            _forwardLookups[wrapper.MessageId] = registration;
        }
        _metrics?.Increment(RuntimeMetricNames.ForwardAttempts);

        _logger.Information(
            "FORWARD",
            $"wrapper={wrapper.MessageId} detected=true snapshot=Insufficient " +
            $"sourceChannel={sourceKey.ChannelId} sourceMessage={sourceKey.MessageId} " +
            "resolution=LookupPending.");
        _ = ResolveForwardAsync(client, registration, generationCancellation);
    }

    private async Task ResolveForwardAsync(
        IDiscordRpcClient client,
        ForwardLookupRegistration registration,
        CancellationToken generationCancellation)
    {
        try
        {
            var resolutionTask = _forwardResolver.ResolveAsync(
                registration.SourceKey,
                (channelId, cancellationToken) =>
                    GetChannelAsync(client, channelId, cancellationToken),
                generationCancellation);
            var content = await resolutionTask.WaitAsync(registration.Cancellation.Token)
                .ConfigureAwait(false);
            var resolved = content is not null;
            var patch = new DiscordMessagePatch(registration.WrapperMessageId)
            {
                ChannelId = OptionalValue<string>.From(registration.WrapperChannelId),
                Content = resolved
                    ? OptionalValue<string>.From(content!.Content)
                    : default,
                CustomEmojis = resolved
                    ? OptionalValue<IReadOnlyList<DiscordCustomEmoji>>.From(content!.CustomEmojis)
                    : default,
                Attachments = resolved
                    ? OptionalValue<IReadOnlyList<DiscordAttachmentMetadata>>.From(content!.Attachments)
                    : default,
                Embeds = resolved
                    ? OptionalValue<IReadOnlyList<DiscordEmbedMetadata>>.From(content!.Embeds)
                    : default,
                Mentions = resolved
                    ? OptionalValue<IReadOnlyList<DiscordMention>>.From(content!.Mentions)
                    : default,
                Stickers = resolved
                    ? OptionalValue<IReadOnlyList<DiscordStickerMetadata>>.From(content!.Stickers)
                    : default,
                Forward = OptionalValue<DiscordForwardMetadata?>.From(
                    new DiscordForwardMetadata(
                        resolved
                            ? DiscordForwardResolutionMode.LookupResolved
                            : DiscordForwardResolutionMode.LookupFailed,
                        registration.SourceKey,
                        content?.HasStickerEvidence == true)),
                FallbackKind = OptionalValue<DiscordMessageFallbackKind>.From(
                    resolved
                        ? DiscordMessageFallbackKind.None
                        : DiscordMessageFallbackKind.ForwardedMessage),
            };
            if (!TryCommitForwardLookup(registration, patch))
            {
                _metrics?.Increment(RuntimeMetricNames.ChatStaleDiscards);
                _logger.Information(
                    "FORWARD",
                    $"wrapper={registration.WrapperMessageId} resolution=StaleIgnored.");
                return;
            }

            _metrics?.Increment(resolved
                ? RuntimeMetricNames.ForwardSucceeded
                : RuntimeMetricNames.ForwardFailed);
            _metrics?.Increment(resolved
                ? "chat.forward.mode.lookup_resolved.count"
                : "chat.forward.mode.fallback.count");

            _logger.Information(
                "FORWARD",
                $"wrapper={registration.WrapperMessageId} " +
                $"resolution={(resolved ? "OnDemand" : "Fallback")}.");
        }
        catch (OperationCanceledException)
        {
            _logger.Information(
                "FORWARD",
                $"wrapper={registration.WrapperMessageId} resolution=Cancelled.");
        }
        finally
        {
            lock (_sync)
            {
                if (_forwardLookups.TryGetValue(
                        registration.WrapperMessageId,
                        out var current) &&
                    current.Revision == registration.Revision)
                {
                    _forwardLookups.Remove(registration.WrapperMessageId);
                }
            }

            registration.Cancellation.Dispose();
        }
    }

    private bool TryCommitForwardLookup(
        ForwardLookupRegistration registration,
        DiscordMessagePatch patch)
    {
        // Cancellation is advisory. The registration stamp, RPC generation,
        // source identity and current store membership are the commit authority.
        lock (_sync)
        {
            if (!_forwardLookups.TryGetValue(
                    registration.WrapperMessageId,
                    out var current))
            {
                return false;
            }

            if (current.Revision != registration.Revision ||
                current.Generation != registration.Generation ||
                registration.Cancellation.IsCancellationRequested)
            {
                return false;
            }

            var state = _messagePipeline.Current;
            if (state.Generation != registration.Generation ||
                !state.MainChat.Any(message =>
                    string.Equals(
                        message.MessageId,
                        registration.WrapperMessageId,
                        StringComparison.Ordinal) &&
                    message.Forward?.SourceKey == registration.SourceKey))
            {
                return false;
            }

            return _messagePipeline.ReceiveLive(
                registration.Generation,
                DiscordMessageMutation.Update(patch));
        }
    }

    private void CancelForwardLookup(string wrapperMessageId)
    {
        ForwardLookupRegistration? registration = null;
        lock (_sync)
        {
            if (_forwardLookups.Remove(wrapperMessageId, out var removed))
            {
                registration = removed;
            }
        }

        registration?.Cancellation.Cancel();
    }

    private void CancelForwardLookups(long generation)
    {
        ForwardLookupRegistration[] registrations;
        lock (_sync)
        {
            registrations = _forwardLookups.Values
                .Where(registration => registration.Generation == generation)
                .ToArray();
            foreach (var registration in registrations)
            {
                _forwardLookups.Remove(registration.WrapperMessageId);
            }
        }

        foreach (var registration in registrations)
        {
            registration.Cancellation.Cancel();
        }
    }

    private void SetStatus(
        DiscordConnectionState state,
        long generation,
        string detail)
    {
        DiscordConnectionStatus status;
        var nowTimestamp = Stopwatch.GetTimestamp();
        DiscordConnectionState previousState;
        lock (_sync)
        {
            previousState = _status.State;
            if (previousState == DiscordConnectionState.Connected &&
                state != DiscordConnectionState.Connected &&
                _connectedStartedTimestamp != 0)
            {
                _connectedElapsedStopwatchTicks += nowTimestamp - _connectedStartedTimestamp;
                _connectedStartedTimestamp = 0;
            }
            else if (previousState != DiscordConnectionState.Connected &&
                     state == DiscordConnectionState.Connected)
            {
                _connectedStartedTimestamp = nowTimestamp;
            }

            status = new DiscordConnectionStatus(
                state,
                generation,
                detail,
                DateTimeOffset.UtcNow);
            _status = status;
        }

        if (state == DiscordConnectionState.Reconnecting && previousState != state)
        {
            _metrics?.Increment(RuntimeMetricNames.RpcReconnects);
        }

        RefreshRuntimeMetrics();
        _metrics?.SetState("rpc.state", state.ToString());

        InvokeSafely(StatusChanged, status, "Connection status subscriber failed.");
    }

    private void PublishTargets(DiscordTargetChannels targets) =>
        InvokeSafely(TargetChannelsResolved, targets, "Target channel subscriber failed.");

    private void PublishAuthenticatedUser(DiscordAuthenticatedUser user)
    {
        lock (_sync)
        {
            _authenticatedUser = user;
        }

        InvokeSafely(
            AuthenticatedUserChanged,
            user,
            "Authenticated user subscriber failed.");
    }

    private void OnMessageStateChanged(DiscordMessageState state) =>
        InvokeSafely(MessageStateChanged, state, "Message state subscriber failed.");

    private void InvokeSafely<T>(Action<T>? handlers, T value, string failureMessage)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                _logger.Error("RPC", failureMessage, exception);
            }
        }
    }

    private sealed record ForwardLookupRegistration(
        string WrapperMessageId,
        string WrapperChannelId,
        DiscordForwardSourceKey SourceKey,
        long Generation,
        long Revision,
        CancellationTokenSource Cancellation);

    private sealed record OpaqueHydrationRegistration(
        string WrapperMessageId,
        string WrapperChannelId,
        string GuildId,
        long Generation,
        long Revision,
        CancellationTokenSource Cancellation);
}
