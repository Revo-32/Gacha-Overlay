using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Threading.Channels;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Providers;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;
using LSOverlay.RemoteClient;

namespace GachaOverlay.App.Services;

internal sealed partial class RemoteChatProductionCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    };

    private readonly object _sync = new();
    private readonly ISettingsStore _settingsStore;
    private readonly IRemoteAccessCredentialStore _credentialStore;
    private readonly IOverlayMessageIngress _ingress;
    private readonly IAppLogger _logger;
    private readonly RemoteRecoveryAudit? _recoveryAudit;
    private readonly string _installationIdPath;
    private readonly Func<Uri, ILSOverlayRemoteClient> _clientFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _loginCancellation;
    private Task? _sessionTask;
    private Task? _loginTask;
    private readonly Action<Uri> _openBrowser;
    private RemoteRequestScope? _activeRequests;
    private ILSOverlayRemoteClient? _activeClient;
    private ILSOverlayRemoteSalesClient? _activeSalesClient;
    private Channel<ChatBootstrapResponse>? _channelSwitches;
    private Channel<SalesBootstrapResponse>? _salesResyncs;
    private string? _activeAccessToken;
    private string? _activeSalesGeneration;
    private string? _publishedSalesBootstrapGeneration;
    private long _publishedSalesBootstrapSequence = -1;
    private bool _salesBootstrapPublicationRequired;
    private string? _pendingChannelId;
    private RemoteChatSnapshot _snapshot;
    private bool _lastSalesTrackingEnabled;
    private int _salesRecoveryRestarting;
    private bool _disposed;

    public RemoteChatProductionCoordinator(
        ISettingsStore settingsStore,
        IRemoteAccessCredentialStore credentialStore,
        IOverlayMessageIngress ingress,
        string installationIdPath,
        IAppLogger logger,
        Func<Uri, ILSOverlayRemoteClient>? clientFactory = null,
        RemoteRecoveryAudit? recoveryAudit = null,
        Action<Uri>? openBrowser = null)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _ingress = ingress ?? throw new ArgumentNullException(nameof(ingress));
        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdPath);
        _installationIdPath = installationIdPath;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _recoveryAudit = recoveryAudit;
        _clientFactory = clientFactory ?? (uri => new LSOverlayRemoteClient(uri));
        _openBrowser = openBrowser ?? (uri => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose());
        var settings = settingsStore.Current;
        _snapshot = new RemoteChatSnapshot(
            settings.RemoteBackendBaseUrl,
            RemoteChatHealthState.Disconnected,
            "Starting",
            credentialStore.Status == RemoteCredentialStatus.Available,
            null,
            Array.Empty<RemoteChannelOption>(),
            settings.RemoteSelectedChannelId);
        _lastSalesTrackingEnabled = settings.SalesTrackingEnabled;
        _ingress.StateChanged += OnIngressStateChanged;
    }

    public event Action<RemoteChatSnapshot>? SnapshotChanged;

    public event Action<DiscordMessageState>? MessageStateChanged;

    public event Action<DiscordAuthenticatedUser>? AuthenticatedUserChanged;

    public event Action<BootstrapResponse>? PresenceBootstrapReady;

    public event Action<HostPresenceSnapshot>? HostPresenceChanged;

    public event Action<SalesBootstrapResponse>? SalesBootstrapReady;

    public event Action<SalesMutationEnvelope>? SalesMutationReceived;

    public event Action<string>? SalesStatusChanged;

    public RemoteChatSnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();
        StartSession();
    }

    public async Task<bool> ApplyConfigurationAsync(string backendBaseUrl)
    {
        ThrowIfDisposed();
        CancelLogin();
        if (_loginTask is { } login) await login.ConfigureAwait(false);
        if (!TryCreateEndpoint(backendBaseUrl, out var endpoint))
        {
            SetHealth(RemoteChatHealthState.Error, "InvalidEndpoint");
            return false;
        }

        var normalizedEndpoint = endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        if (!_settingsStore.Update(settings => settings with
        {
            RemoteBackendBaseUrl = normalizedEndpoint,
        }))
        {
            SetHealth(RemoteChatHealthState.Error, "SettingsSaveFailed");
            return false;
        }

        await StopSessionAsync().ConfigureAwait(false);
        if (_settingsStore.Current.SalesTrackingEnabled)
        {
            SetRemoteSalesStatus(RemoteSalesStatusNames.Connecting);
        }
        else
        {
            SetRemoteSalesStatus(RemoteSalesStatusNames.Disabled);
        }
        UpdateSnapshot(current => current with
        {
            BackendBaseUrl = normalizedEndpoint,
            Health = RemoteChatHealthState.Disconnected,
            Detail = "Starting",
            WebAuthExpiresAt = null,
        });
        StartSession();

        _logger.Information(
            "REMOTE",
            "Remote backend configuration updated.");
        return true;
    }

    public Task BeginLoginAsync()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_loginTask is { IsCompleted: false }) return Task.CompletedTask;
            _loginTask = BeginLoginCoreAsync();
            return _loginTask;
        }
    }

    private async Task BeginLoginCoreAsync()
    {
        ThrowIfDisposed();
        await StopSessionAsync().ConfigureAwait(false);
        CancelLogin();
        var loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        lock (_sync)
        {
            _loginCancellation = loginCancellation;
        }

        ILSOverlayRemoteClient? client = null;
        try
        {
            if (!TryCreateEndpoint(_settingsStore.Current.RemoteBackendBaseUrl, out var endpoint))
            {
                SetHealth(RemoteChatHealthState.Error, "InvalidEndpoint");
                return;
            }

            SetHealth(RemoteChatHealthState.LoginInProgress, "WebAuthWaiting");
            client = _clientFactory(endpoint);
            if (client is ILSOverlayDiscordWebAuthClient web)
                await TryWebLoginAsync(web, loginCancellation.Token).ConfigureAwait(false);
            else
                SetHealth(RemoteChatHealthState.LoginRequired, "WebAuthUnavailable");
        }
        catch (OperationCanceledException) when (loginCancellation.IsCancellationRequested)
        {
            SetHealth(RemoteChatHealthState.LoginRequired, "WebAuthCancelled");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or UnauthorizedAccessException or
                OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException)
        {
            _logger.Warning("REMOTE", $"Browser login failed ({exception.GetType().Name}).");
            SetHealth(RemoteChatHealthState.Error, "WebAuthTemporaryFailure");
        }
        finally
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (ReferenceEquals(_loginCancellation, loginCancellation))
                {
                    _loginCancellation = null;
                }
            }

            loginCancellation.Dispose();
        }
    }

    public void CancelLogin()
    {
        lock (_sync)
        {
            _loginCancellation?.Cancel();
        }
    }

    public async Task<bool> ForgetCredentialAsync()
    {
        CancelLogin();
        if (_loginTask is { } login) await login.ConfigureAwait(false);
        await StopSessionAsync().ConfigureAwait(false);
        if (!_credentialStore.Clear())
        {
            SetHealth(RemoteChatHealthState.Error, "ProtectedClearFailed");
            return false;
        }

        _settingsStore.Update(settings => settings with { RemoteSelectedChannelId = null });
        _ingress.ClearForAccessRevocation();
        UpdateSnapshot(current => current with
        {
            Health = RemoteChatHealthState.LoginRequired,
            Detail = "CredentialForgotten",
            HasProtectedCredential = false,
            WebAuthExpiresAt = null,
            Channels = Array.Empty<RemoteChannelOption>(),
            SelectedChannelId = null,
        });
        return true;
    }

    public async Task RefreshAsync()
    {
        ThrowIfDisposed();
        CancelLogin();
        if (_loginTask is { } login) await login.ConfigureAwait(false);
        await StopSessionAsync().ConfigureAwait(false);
        StartSession();
    }

    public ManualSalesResyncResult RequestSalesResync()
    {
        ThrowIfDisposed();
        var settings = _settingsStore.Current;
        if (!settings.SalesTrackingEnabled)
        {
            return ManualSalesResyncResult.TrackingDisabled;
        }

        ILSOverlayRemoteSalesClient? client;
        ChannelWriter<SalesBootstrapResponse>? writer;
        string? accessToken;
        RemoteRequestScope? requests;
        lock (_sync)
        {
            client = _activeSalesClient;
            writer = _salesResyncs?.Writer;
            accessToken = _activeAccessToken;
            requests = _activeRequests;
        }

        if (client is null || writer is null || requests is null || string.IsNullOrWhiteSpace(accessToken))
        {
            return ManualSalesResyncResult.RemoteUnavailable;
        }

        if (Interlocked.Exchange(ref _salesRecoveryRestarting, 1) != 0)
        {
            return ManualSalesResyncResult.Coalesced;
        }

        var request = requests.TryRun(RemoteRequestKind.SalesResync, async token =>
        {
            await ResynchronizeSalesAsync(client, accessToken, writer, token).ConfigureAwait(false);
            return true;
        });
        if (request is null)
        {
            Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
            return ManualSalesResyncResult.Coalesced;
        }
        _ = IgnoreFailureAsync(request);
        return ManualSalesResyncResult.Requested;
    }

    public void NotifySalesTrackingChanged()
    {
        var settings = _settingsStore.Current;
        var enabled = settings.SalesTrackingEnabled;
        bool trackingChanged;
        lock (_sync)
        {
            trackingChanged = _lastSalesTrackingEnabled != enabled;
            if (!trackingChanged)
            {
                return;
            }

            _lastSalesTrackingEnabled = enabled;
        }

        if (!enabled)
        {
            SetRemoteSalesStatus(RemoteSalesStatusNames.Disabled);
        }
        else
        {
            SetRemoteSalesStatus(RemoteSalesStatusNames.Connecting);
        }

        _ = RestartForSalesSettingAsync();
    }

    public async Task<bool> SwitchChannelAsync(string channelId)
    {
        ThrowIfDisposed();
        if (!ulong.TryParse(channelId, out var parsedChannelId))
        {
            return false;
        }

        var snapshot = Snapshot;
        if (!snapshot.Channels.Any(channel => channel.ChannelId == channelId))
        {
            SetHealth(RemoteChatHealthState.ChannelSelectionRequired, "UnauthorizedChannel");
            return false;
        }

        ILSOverlayRemoteClient? client;
        Channel<ChatBootstrapResponse>? switches;
        RemoteRequestScope? requests;
        string? accessToken;
        lock (_sync)
        {
            client = _activeClient;
            switches = _channelSwitches;
            requests = _activeRequests;
            accessToken = _activeAccessToken;
        }

        if (client is null || switches is null || requests is null || string.IsNullOrWhiteSpace(accessToken))
        {
            if (!_settingsStore.Update(settings => settings with
            {
                RemoteSelectedChannelId = channelId,
            }))
            {
                return false;
            }

            UpdateSnapshot(current => current with { SelectedChannelId = channelId });
            await RefreshAsync().ConfigureAwait(false);
            return true;
        }

        try
        {
            SetHealth(RemoteChatHealthState.Bootstrapping, "SwitchingChannel");
            var request = requests.TryRun(RemoteRequestKind.ChannelSwitch, async token =>
            {
                var bootstrap = await client.GetChatBootstrapAsync(accessToken, parsedChannelId, token)
                    .ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (!ReferenceEquals(_activeRequests, requests)) { return false; }
                    _pendingChannelId = channelId;
                    return switches.Writer.TryWrite(bootstrap);
                }
            });

            return request is not null && await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (RemoteAuthenticationRequiredException)
        {
            _ingress.ClearForAccessRevocation();
            SetHealth(RemoteChatHealthState.AccessRevoked, "AuthenticationRejected");
            return false;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            _logger.Warning("REMOTE", $"Remote channel switch failed ({exception.GetType().Name}).");
            SetHealth(RemoteChatHealthState.Reconnecting, "ChannelSwitchFailed");
            return false;
        }
    }

    public async Task<SalesStatusActionResponse?> SetSalesStatusAsync(
        ulong messageId,
        SalesStatus desiredStatus,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ILSOverlayRemoteSalesClient? client;
        string? accessToken;
        string? generation;
        RemoteRequestScope? requests;
        lock (_sync)
        {
            client = _activeSalesClient;
            accessToken = _activeAccessToken;
            generation = _activeSalesGeneration;
            requests = _activeRequests;
        }

        var settings = _settingsStore.Current;
        if (messageId == 0 ||
            client is null || requests is null ||
            string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(generation) ||
            !settings.SalesTrackingEnabled)
        {
            return null;
        }

        var request = new SalesStatusActionRequest(
            OverlayTransportProtocol.Version,
            messageId,
            desiredStatus,
            Guid.NewGuid(),
            generation);
        try
        {
            var pending = requests.TryRun(RemoteRequestKind.SalesAction, async token =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                return await client.SetSalesStatusAsync(accessToken, request, linked.Token).ConfigureAwait(false);
            });
            return pending is null ? null : await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (RemoteAuthenticationRequiredException)
        {
            SetRemoteSalesStatus(OverlayTransportProtocol.SalesAccessRevoked);
            return null;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException)
        {
            _logger.Warning(
                "REMOTE-SALES",
                $"Sales status action failed ({exception.GetType().Name}).");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ingress.StateChanged -= OnIngressStateChanged;
        CancelLogin();
        _lifetime.Cancel();
        if (_loginTask is { } login) await login.ConfigureAwait(false);
        await StopSessionAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void StartSession()
    {
        lock (_sync)
        {
            if (_disposed || _sessionTask is { IsCompleted: false })
            {
                return;
            }

            // A naturally completed session still owns a linked cancellation registration.
            _sessionCancellation?.Cancel();
            _sessionCancellation?.Dispose();
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _sessionTask = RunSessionLoopAsync(_sessionCancellation.Token);
        }
    }

    private async Task StopSessionAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_sync)
        {
            cancellation = _sessionCancellation;
            task = _sessionTask;
            _sessionCancellation = null;
            _sessionTask = null;
        }

        try
        {
            cancellation?.Cancel();
            if (task is not null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true) { }
            }
        }
        finally { cancellation?.Dispose(); }
    }

    private async Task RunSessionLoopAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var auditAttempt = _recoveryAudit?.BeginAttempt(_settingsStore.Current.SalesTrackingEnabled) ?? 0;
            if (_settingsStore.Current.SalesTrackingEnabled)
            {
                SetRemoteSalesStatus(RemoteSalesStatusNames.Connecting);
            }

            if (!_credentialStore.TryLoad(out var accessToken) ||
                string.IsNullOrWhiteSpace(accessToken))
            {
                SetRemoteSalesFailureStatus(RemoteSalesStatusNames.CredentialUnavailable);
                SetHealth(
                    _credentialStore.Status == RemoteCredentialStatus.Unreadable
                        ? RemoteChatHealthState.Error
                        : RemoteChatHealthState.LoginRequired,
                    _credentialStore.Status == RemoteCredentialStatus.Unreadable
                        ? "CredentialUnreadable"
                        : "CredentialMissing");
                return;
            }

            if (!TryCreateEndpoint(_settingsStore.Current.RemoteBackendBaseUrl, out var endpoint))
            {
                SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesFailed);
                SetHealth(RemoteChatHealthState.Error, "InvalidEndpoint");
                return;
            }

            ILSOverlayRemoteClient? client = null;
            RemoteChatIngressAdapter? adapter = null;
            Action? liveHandler = null;
            Action<ChatBootstrapResponse>? channelReadyHandler = null;
            Action<SalesBootstrapResponse>? salesReadyHandler = null;
            Action<HostPresenceSnapshot>? presenceHandler = null;
            using var bootstrapCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var requests = new RemoteRequestScope(cancellationToken);
            Task<BootstrapResponse>? presencePublicationTask = null;
            Task<SalesBootstrapResponse?>? salesPublicationTask = null;
            Task<SalesBootstrapResponse>? salesBootstrapTask = null;
            Task<BootstrapResponse>? presenceTask = null;
            Task<ChatChannelCatalogResponse>? catalogTask = null;
            TimeSpan? retryDelay = null;
            var recoveryPhase = "Bootstrap";
            try
            {
                // Revocation advances the store generation too. Capture from that
                // single authority before I/O so a later revocation still fences
                // out this attempt's pending bootstrap and callbacks.
                var generation = checked(_ingress.Current.Generation + 1);
                SetHealth(RemoteChatHealthState.Authenticating, "Bootstrap");
                client = _clientFactory(endpoint);
                presenceHandler = presence => HostPresenceChanged?.Invoke(presence);
                client.HostPresenceChanged += presenceHandler;
                SalesBootstrapResponse? salesBootstrap = null;
                ILSOverlayRemoteSalesClient? salesClient = null;
                if (_settingsStore.Current.SalesTrackingEnabled &&
                    client is ILSOverlayRemoteSalesClient candidate)
                {
                    SetRemoteSalesStatus(RemoteSalesStatusNames.Bootstrapping);
                    salesClient = candidate;
                    salesBootstrapTask = salesClient.GetSalesBootstrapAsync(
                        accessToken,
                        bootstrapCancellation.Token);
                }
                else if (_settingsStore.Current.SalesTrackingEnabled)
                {
                    SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesFailed);
                }

                presenceTask = client.GetBootstrapAsync(
                    accessToken,
                    bootstrapCancellation.Token);
                catalogTask = client.GetChatChannelsAsync(
                    accessToken,
                    bootstrapCancellation.Token);
                presencePublicationTask = PublishPresenceBootstrapAsync(presenceTask, auditAttempt, bootstrapCancellation.Token);
                salesPublicationTask = PublishInitialSalesBootstrapAsync(salesBootstrapTask, auditAttempt, bootstrapCancellation.Token);
                SetHealth(RemoteChatHealthState.Connecting, "LoadingChannels");
                ChatChannelCatalogResponse catalog;
                try
                {
                    recoveryPhase = "ChannelCatalog";
                    catalog = await catalogTask.ConfigureAwait(false);
                }
                catch
                {
                    bootstrapCancellation.Cancel();
                    await IgnoreFailureAsync(presencePublicationTask).ConfigureAwait(false);
                    await IgnoreFailureAsync(salesPublicationTask).ConfigureAwait(false);
                    throw;
                }
                var channels = catalog.Channels
                    .OrderBy(channel => channel.Position)
                    .ThenBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(channel => new RemoteChannelOption(
                        channel.ChannelId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        channel.Name,
                        channel.GuildId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        channel.Position,
                        channel.IsAnnouncement))
                    .ToArray();
                var selectedId = _settingsStore.Current.RemoteSelectedChannelId;
                if (selectedId is not null &&
                    !channels.Any(channel => channel.ChannelId == selectedId))
                {
                    selectedId = null;
                    _settingsStore.Update(settings => settings with
                    {
                        RemoteSelectedChannelId = null,
                    });
                    _logger.Warning("REMOTE", "Stale remote channel selection was cleared.");
                }

                UpdateSnapshot(current => current with
                {
                    Channels = channels,
                    SelectedChannelId = selectedId,
                    HasProtectedCredential = true,
                });
                if (selectedId is null || !ulong.TryParse(selectedId, out var channelId))
                {
                    bootstrapCancellation.Cancel();
                    await IgnoreFailureAsync(presencePublicationTask).ConfigureAwait(false);
                    await IgnoreFailureAsync(salesPublicationTask).ConfigureAwait(false);
                    SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesChannelUnavailable);
                    SetHealth(
                        RemoteChatHealthState.ChannelSelectionRequired,
                        channels.Length == 0 ? "NoAuthorizedChannels" : "SelectChannel");
                    return;
                }

                SetHealth(RemoteChatHealthState.Bootstrapping, "LoadingRecentMessages");
                ChatBootstrapResponse chatBootstrap;
                try
                {
                    recoveryPhase = "ChatBootstrap";
                    chatBootstrap = await client.GetChatBootstrapAsync(
                            accessToken,
                            channelId,
                            bootstrapCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    bootstrapCancellation.Cancel();
                    await IgnoreFailureAsync(presencePublicationTask).ConfigureAwait(false);
                    await IgnoreFailureAsync(salesPublicationTask).ConfigureAwait(false);
                    throw;
                }

                cancellationToken.ThrowIfCancellationRequested();
                adapter = new RemoteChatIngressAdapter(
                    _ingress,
                    client,
                    generation,
                    authenticatedUserId: null);
                if (!adapter.ApplyBootstrap(chatBootstrap))
                {
                    throw new RemoteResyncRequiredException();
                }
                _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.ChatSnapshot);
                _logger.Information(
                    "REMOTE",
                    $"Published initial chat bootstrap count={chatBootstrap.RecentMessages.Count} without waiting for Sales or Presence synchronization.");
                try
                {
                    recoveryPhase = "SalesBootstrap";
                    salesBootstrap = await salesPublicationTask.ConfigureAwait(false);
                }
                catch
                {
                    bootstrapCancellation.Cancel();
                    await IgnoreFailureAsync(presencePublicationTask).ConfigureAwait(false);
                    throw;
                }

                SetHealth(RemoteChatHealthState.Bootstrapping, "WaitingForPresence");
                BootstrapResponse presence;
                try
                {
                    recoveryPhase = "PresenceBootstrap";
                    presence = await presencePublicationTask.ConfigureAwait(false);
                }
                catch
                {
                    bootstrapCancellation.Cancel();
                    throw;
                }
                var switches = Channel.CreateBounded<ChatBootstrapResponse>(
                    new BoundedChannelOptions(1)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                    });
                var salesResyncs = Channel.CreateBounded<SalesBootstrapResponse>(
                    new BoundedChannelOptions(1)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                    });
                liveHandler = () =>
                {
                    if (_ingress.Current.Generation == generation)
                    {
                        failures = 0;
                        OnStreamLive();
                        _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.PresenceStream);
                    }
                };
                channelReadyHandler = bootstrap =>
                {
                    if (_ingress.Current.Generation == generation &&
                        !_ingress.Current.IsBootstrapping &&
                        _ingress.Targets?.MainChannelId == bootstrap.Channel.ChannelId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture) &&
                        _ingress.Targets?.GuildId == bootstrap.Channel.GuildId.ToString(
                            System.Globalization.CultureInfo.InvariantCulture))
                    {
                        OnChatChannelReady(bootstrap);
                        _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.ChatSnapshot);
                        _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.ChatStream);
                    }
                };
                client.StreamLive += liveHandler;
                client.ChatChannelReady += channelReadyHandler;
                client.ChatStreamStatusChanged += OnChatStreamStatusChanged;
                if (salesClient is not null)
                {
                    salesReadyHandler = bootstrap =>
                    {
                        OnSalesReady(bootstrap);
                        _recoveryAudit?.InvalidateSales();
                        if (bootstrap.Coverage == SalesBootstrapCoverage.Complete)
                        {
                            _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.SalesComplete);
                        }
                        _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.SalesStream);
                    };
                    salesClient.SalesReady += salesReadyHandler;
                    salesClient.SalesMutationReceived += OnSalesMutationReceived;
                    salesClient.SalesStreamStatusChanged += OnSalesStreamStatusChanged;
                }
                lock (_sync)
                {
                    _activeClient = client;
                    _activeRequests = requests;
                    _activeSalesClient = salesClient;
                    _activeAccessToken = accessToken;
                    _channelSwitches = switches;
                    _salesResyncs = salesResyncs;
                }

                recoveryPhase = "LiveStream";
                Task stream;
                if (salesClient is not null && salesBootstrap is not null)
                {
                    stream = salesClient.StreamChatAndSalesAsync(
                            accessToken,
                            presence,
                            chatBootstrap,
                            salesBootstrap,
                            switches.Reader,
                            salesResyncs.Reader,
                            cancellationToken);
                }
                else
                {
                    stream = client.StreamChatAsync(
                            accessToken,
                            presence,
                            chatBootstrap,
                            switches.Reader,
                            cancellationToken);
                }
                // All bootstrap tasks have settled. Do not keep their results or
                // the initial recent20 alive across later channel/sales replacements.
                chatBootstrap = null!;
                salesBootstrap = null;
                presence = null!;
                presenceTask = null;
                presencePublicationTask = null;
                salesBootstrapTask = null;
                salesPublicationTask = null;
                catalogTask = null;
                await stream.ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Remote chat stream closed.");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RemoteAuthenticationRequiredException)
            {
                SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesAccessRevoked);
                _ingress.ClearForAccessRevocation();
                SetHealth(RemoteChatHealthState.AccessRevoked, "AuthenticationRejected");
                return;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or WebSocketException or IOException or
                    RemoteResyncRequiredException)
            {
                var delay = RetryDelays[Math.Min(failures, RetryDelays.Length - 1)];
                failures++;
                var httpStatus = exception is HttpRequestException { StatusCode: { } statusCode }
                    ? ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "none";
                _logger.Warning(
                    "REMOTE",
                    $"Remote chat connection degraded ({exception.GetType().Name}) phase={recoveryPhase} http_status={httpStatus}; retrying.");
                if (_settingsStore.Current.SalesTrackingEnabled)
                {
                    SetRemoteSalesStatus(RemoteSalesStatusNames.Reconnecting);
                }
                SetHealth(RemoteChatHealthState.Reconnecting, "NetworkUnavailable");
                if (failures >= RetryDelays.Length)
                {
                    SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesFailed);
                    SetHealth(RemoteChatHealthState.Error, "RecoveryExhausted");
                    return;
                }
                retryDelay = delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
            }
            catch (Exception exception)
            {
                _logger.Error("REMOTE", "Remote chat session failed.", exception);
                SetRemoteSalesFailureStatus(OverlayTransportProtocol.SalesFailed);
                SetHealth(RemoteChatHealthState.Error, exception.GetType().Name);
                return;
            }
            finally
            {
                _recoveryAudit?.EndAttempt(auditAttempt);
                lock (_sync)
                {
                    if (ReferenceEquals(_activeClient, client))
                    {
                        _activeClient = null;
                        _activeRequests = null;
                        _activeSalesClient = null;
                        _activeAccessToken = null;
                        _activeSalesGeneration = null;
                        _publishedSalesBootstrapGeneration = null;
                        _publishedSalesBootstrapSequence = -1;
                        _salesBootstrapPublicationRequired = false;
                        _channelSwitches?.Writer.TryComplete();
                        _salesResyncs?.Writer.TryComplete();
                        _channelSwitches = null;
                        _salesResyncs = null;
                        _pendingChannelId = null;
                        Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
                    }
                }

                // Includes stale-bootstrap rejection and unexpected callback failures.
                bootstrapCancellation.Cancel();
                await IgnoreFailureAsync(presencePublicationTask).ConfigureAwait(false);
                await IgnoreFailureAsync(salesPublicationTask).ConfigureAwait(false);
                await IgnoreFailureAsync(presenceTask).ConfigureAwait(false);
                await IgnoreFailureAsync(salesBootstrapTask).ConfigureAwait(false);
                await IgnoreFailureAsync(catalogTask).ConfigureAwait(false);
                await requests.DisposeAsync().ConfigureAwait(false);
                if (client is not null)
                {
                    if (liveHandler is not null)
                    {
                        client.StreamLive -= liveHandler;
                    }

                    if (presenceHandler is not null)
                    {
                        client.HostPresenceChanged -= presenceHandler;
                    }

                    if (channelReadyHandler is not null)
                    {
                        client.ChatChannelReady -= channelReadyHandler;
                    }
                    client.ChatStreamStatusChanged -= OnChatStreamStatusChanged;
                    if (client is ILSOverlayRemoteSalesClient salesClient)
                    {
                        if (salesReadyHandler is not null)
                        {
                            salesClient.SalesReady -= salesReadyHandler;
                        }
                        salesClient.SalesMutationReceived -= OnSalesMutationReceived;
                        salesClient.SalesStreamStatusChanged -= OnSalesStreamStatusChanged;
                    }
                }

                adapter?.Dispose();
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (retryDelay is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void OnStreamLive()
    {
        SetHealth(RemoteChatHealthState.Live, "Live");
    }

    private void OnSalesReady(SalesBootstrapResponse bootstrap)
    {
        bool shouldPublish;
        lock (_sync)
        {
            _activeSalesGeneration = bootstrap.Generation;
            shouldPublish =
                _salesBootstrapPublicationRequired ||
                !string.Equals(
                    _publishedSalesBootstrapGeneration,
                    bootstrap.Generation,
                    StringComparison.Ordinal) ||
                _publishedSalesBootstrapSequence != bootstrap.LatestSequence;
            _salesBootstrapPublicationRequired = false;
            _publishedSalesBootstrapGeneration = bootstrap.Generation;
            _publishedSalesBootstrapSequence = bootstrap.LatestSequence;
        }

        if (shouldPublish)
        {
            SalesBootstrapReady?.Invoke(bootstrap);
        }

        SetRemoteSalesStatus(OverlayTransportProtocol.SalesReady);
        Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
    }

    private void OnSalesMutationReceived(SalesMutationEnvelope mutation) =>
        SalesMutationReceived?.Invoke(mutation);

    private void OnSalesStreamStatusChanged(string status)
    {
        if (status != OverlayTransportProtocol.SalesReady)
        {
            lock (_sync)
            {
                _activeSalesGeneration = null;
            }
        }

        SetRemoteSalesStatus(status);
        if (status == OverlayTransportProtocol.SalesResyncRequired)
        {
            // A resync status can also reject the previous replacement cursor.
            // Release its coalescing gate so a fresh canonical bootstrap may follow.
            Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
            _ = RequestSalesResync();
        }
        else if (status != OverlayTransportProtocol.SalesReady)
        {
            Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
        }
    }

    private void SetRemoteSalesFailureStatus(string status)
    {
        if (_settingsStore.Current.SalesTrackingEnabled)
        {
            SetRemoteSalesStatus(status);
        }
    }

    private void SetRemoteSalesStatus(string status)
    {
        if (status != OverlayTransportProtocol.SalesReady)
        {
            _recoveryAudit?.InvalidateSales();
        }
        var display = status switch
        {
            OverlayTransportProtocol.SalesReady => "Live",
            OverlayTransportProtocol.SalesResyncRequired => "Resyncing",
            OverlayTransportProtocol.SalesAuthorizationUnavailable =>
                "AuthorizationUnavailable",
            OverlayTransportProtocol.SalesAccessRevoked => "AccessRevoked",
            OverlayTransportProtocol.SalesChannelUnavailable => "ChannelUnavailable",
            OverlayTransportProtocol.SalesFailed => "Unavailable",
            _ => status,
        };
        UpdateSnapshot(current => current with { RemoteSalesStatus = display });
        SalesStatusChanged?.Invoke(status);
    }

    private void OnChatChannelReady(ChatBootstrapResponse bootstrap)
    {
        var channelId = bootstrap.Channel.ChannelId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string? pending;
        lock (_sync)
        {
            pending = _pendingChannelId;
            if (pending == channelId)
            {
                _pendingChannelId = null;
            }
        }

        if (pending == channelId && !_settingsStore.Update(settings => settings with
        {
            RemoteSelectedChannelId = channelId,
        }))
        {
            SetHealth(RemoteChatHealthState.Error, "ChannelPersistenceFailed");
            return;
        }

        UpdateSnapshot(current => current with
        {
            SelectedChannelId = channelId,
            Health = RemoteChatHealthState.Live,
            Detail = "Live",
        });
    }

    private void OnChatStreamStatusChanged(ulong channelId, string status)
    {
        var health = status switch
        {
            OverlayTransportProtocol.ChatAccessRevoked => RemoteChatHealthState.AccessRevoked,
            OverlayTransportProtocol.ChatAuthorizationUnavailable =>
                RemoteChatHealthState.AuthorizationUnavailable,
            OverlayTransportProtocol.ChatChannelUnavailable =>
                RemoteChatHealthState.ChannelSelectionRequired,
            OverlayTransportProtocol.ChatResyncRequired => RemoteChatHealthState.Reconnecting,
            OverlayTransportProtocol.ChatFailed => RemoteChatHealthState.Error,
            _ => RemoteChatHealthState.Error,
        };
        if (health == RemoteChatHealthState.AccessRevoked)
        {
            _ingress.ClearForAccessRevocation();
        }

        SetHealth(health, status);
    }

    private void OnIngressStateChanged(DiscordMessageState state) =>
        MessageStateChanged?.Invoke(state);

    private void SetHealth(RemoteChatHealthState health, string detail)
    {
        if (health is RemoteChatHealthState.Bootstrapping or RemoteChatHealthState.ChannelSelectionRequired)
        {
            _recoveryAudit?.InvalidateChat();
        }
        if (health is RemoteChatHealthState.Reconnecting or RemoteChatHealthState.Disconnected or
            RemoteChatHealthState.Error or RemoteChatHealthState.AccessRevoked or
            RemoteChatHealthState.LoginRequired or RemoteChatHealthState.LoginInProgress or
            RemoteChatHealthState.AuthorizationUnavailable)
        {
            _recoveryAudit?.InvalidateConnection(
                authenticationRequired: health is RemoteChatHealthState.AccessRevoked or
                    RemoteChatHealthState.LoginRequired or RemoteChatHealthState.LoginInProgress,
                terminalFailure: health == RemoteChatHealthState.Error);
        }
        UpdateSnapshot(current => current with
        {
            BackendBaseUrl = _settingsStore.Current.RemoteBackendBaseUrl,
            Health = health,
            Detail = detail,
            HasProtectedCredential =
                _credentialStore.Status == RemoteCredentialStatus.Available,
            WebAuthExpiresAt = health == RemoteChatHealthState.LoginInProgress
                ? current.WebAuthExpiresAt
                : null,
        });
    }

    private void UpdateSnapshot(Func<RemoteChatSnapshot, RemoteChatSnapshot> update)
    {
        RemoteChatSnapshot snapshot;
        lock (_sync)
        {
            snapshot = update(_snapshot);
            if (snapshot == _snapshot)
            {
                return;
            }

            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private bool TryCreateEndpoint(string? value, out Uri endpoint)
    {
        endpoint = null!;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            !TransportEndpointSecurity.IsAllowed(parsed))
        {
            return false;
        }

        endpoint = parsed;
        return true;
    }

    private Guid GetOrCreateInstallationId()
    {
        try
        {
            if (File.Exists(_installationIdPath) &&
                Guid.TryParse(File.ReadAllText(_installationIdPath).Trim(), out var existing))
            {
                return existing;
            }

            var created = Guid.NewGuid();
            Directory.CreateDirectory(Path.GetDirectoryName(_installationIdPath)
                ?? throw new InvalidOperationException("Installation ID directory is invalid."));
            File.WriteAllText(_installationIdPath, created.ToString("D"));
            return created;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning("REMOTE", "Installation ID could not be persisted for this session.");
            return Guid.NewGuid();
        }
    }

    private async Task<BootstrapResponse> PublishPresenceBootstrapAsync(
        Task<BootstrapResponse> bootstrapTask,
        long auditAttempt,
        CancellationToken cancellationToken)
    {
        var bootstrap = await bootstrapTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var authenticatedUserId = bootstrap.SelfDiscordUserId.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        _ingress.SetAuthenticatedUser(authenticatedUserId);
        PresenceBootstrapReady?.Invoke(bootstrap);
        AuthenticatedUserChanged?.Invoke(new DiscordAuthenticatedUser(
            authenticatedUserId,
            string.Empty));
        _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.PresenceSnapshot, bootstrap.Generation);
        return bootstrap;
    }

    private async Task<SalesBootstrapResponse?> PublishInitialSalesBootstrapAsync(
        Task<SalesBootstrapResponse>? bootstrapTask,
        long auditAttempt,
        CancellationToken cancellationToken)
    {
        if (bootstrapTask is null)
        {
            return null;
        }

        try
        {
            var bootstrap = await bootstrapTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _publishedSalesBootstrapGeneration = bootstrap.Generation;
                _publishedSalesBootstrapSequence = bootstrap.LatestSequence;
            }

            SalesBootstrapReady?.Invoke(bootstrap);
            if (bootstrap.Coverage == SalesBootstrapCoverage.Complete)
            {
                _recoveryAudit?.Mark(auditAttempt, RemoteRecoverySignal.SalesComplete);
            }
            _logger.Information(
                "REMOTE-SALES",
                "Published initial Sales bootstrap without waiting for Chat or Presence synchronization.");
            return bootstrap;
        }
        catch (RemoteAuthenticationRequiredException)
        {
            SetRemoteSalesStatus(OverlayTransportProtocol.SalesAccessRevoked);
            _logger.Warning(
                "REMOTE-SALES",
                "Remote Sales access was denied; production evidence is blocked.");
            return null;
        }
        catch (HttpRequestException)
        {
            SetRemoteSalesStatus(OverlayTransportProtocol.SalesAuthorizationUnavailable);
            _logger.Warning(
                "REMOTE-SALES",
                "Remote Sales authorization verification is temporarily unavailable.");
            throw;
        }
    }

    private static async Task IgnoreFailureAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // A sibling bootstrap failure remains the primary attempt result.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private async Task RestartForSalesSettingAsync()
    {
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Error("REMOTE-SALES", "Sales opt-in refresh failed.", exception);
        }
    }

    private async Task ResynchronizeSalesAsync(
        ILSOverlayRemoteSalesClient client,
        string accessToken,
        ChannelWriter<SalesBootstrapResponse> writer,
        CancellationToken cancellationToken)
    {
        var queued = false;
        try
        {
            SetRemoteSalesStatus(OverlayTransportProtocol.SalesResyncRequired);
            var bootstrap = await client.GetSalesBootstrapAsync(
                    accessToken,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_sync)
            {
                var isCurrent = ReferenceEquals(_activeSalesClient, client) &&
                    ReferenceEquals(_salesResyncs?.Writer, writer) &&
                    string.Equals(_activeAccessToken, accessToken, StringComparison.Ordinal);
                if (!isCurrent)
                {
                    return;
                }

                _salesBootstrapPublicationRequired = true;
                if (!writer.TryWrite(bootstrap))
                {
                    _salesBootstrapPublicationRequired = false;
                    return;
                }
            }

            queued = true;
            _logger.Information(
                "REMOTE-SALES",
                $"Canonical Sales-only resync queued count={bootstrap.RecentMessages.Count}; Main Chat and Presence remained connected.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or WebSocketException or IOException)
        {
            _logger.Warning(
                "REMOTE-SALES",
                $"Sales-only resync failed ({exception.GetType().Name}); preserving the active Main Chat and Presence session.");
            SetRemoteSalesStatus(RemoteSalesStatusNames.Reconnecting);
        }
        finally
        {
            if (!queued)
            {
                Interlocked.Exchange(ref _salesRecoveryRestarting, 0);
            }
        }
    }
}
