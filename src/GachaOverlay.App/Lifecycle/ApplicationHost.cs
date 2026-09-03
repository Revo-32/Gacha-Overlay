using GachaOverlay.App.Presentation;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Diagnostics;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Game;
using GachaOverlay.Core.Hud.Geometry;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Infrastructure.Discord.Normalization;
using GachaOverlay.Infrastructure.Diagnostics;
using GachaOverlay.Infrastructure.Lifecycle;
using GachaOverlay.Infrastructure.Localization;
using GachaOverlay.Infrastructure.Logging;
using GachaOverlay.Infrastructure.Paths;
using GachaOverlay.Infrastructure.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Infrastructure.Sales;
using GachaOverlay.Core.Themes;

namespace GachaOverlay.App.Lifecycle;

internal sealed class ApplicationHost : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly Action _requestShutdown;
    private ApplicationLifetime? _lifetime;
    private RollingFileLogger? _fileLogger;
    private ISettingsStore? _settingsStore;
    private ILocalizationService? _localization;
    private FoundationViewModel? _foundationViewModel;
    private SettingsWindowService? _settingsWindowService;
    private TrayIconService? _trayIcon;
    private HudWindowController? _hudController;
    private RemoteChatProductionCoordinator? _remoteChatCoordinator;
    private RemoteRecoveryAudit? _recoveryAudit;
    private IRemoteAccessCredentialStore? _remoteAccessCredentials;
    private IGuildDisplayNameResolver? _displayNameResolver;
    private SalesPresentationCoordinator? _salesCoordinator;
    private SessionHudViewModel? _sessionHudViewModel;
    private ISalesNotificationSoundService? _salesNotificationSoundService;
    private EffectiveSalesProductCatalogStore? _productCatalogStore;
    private ProductMappingManagerWindow? _productMappingWindow;
    private SalesPreviewWindow? _salesPreviewWindow;
    private ColorThemeManager? _colorThemeManager;
    private WindowsAutoStartService? _windowsAutoStartService;
    private OnboardingWindow? _onboardingWindow;
    private OnboardingViewModel? _onboardingViewModel;
    private readonly RuntimeMetricsCollector _runtimeMetrics = new();
    private readonly ProcessMetricsSampler _processMetrics = new();
    private LocalApplicationPaths? _paths;
    private DiagnosticBundleExporter? _diagnosticExporter;
    private CrashMetadataWriter? _crashMetadataWriter;
    private int _started;
    private int _shutdownPrepared;
    private int _disposed;
    private int _imagePayloadObserved;
    private DiscordMessageState _lastRemoteMessageState = DiscordMessageState.Empty;

    public ApplicationHost(System.Windows.Application application, Action requestShutdown)
    {
        _application = application;
        _requestShutdown = requestShutdown;
    }

    public IAppLogger Logger => _fileLogger is null ? NullAppLogger.Instance : _fileLogger;

    private System.Windows.Window? SettingsOwnerWindow =>
        _settingsWindowService?.CurrentWindow as System.Windows.Window;

    public string GetLocalizedString(string key, string fallback) =>
        _localization?.GetString(key) ?? fallback;

    public void RecordCrash(Exception exception, string subsystemContext) =>
        _crashMetadataWriter?.TryWrite(exception, subsystemContext);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The application host has already started.");
        }

        var paths = new LocalApplicationPaths();
        _paths = paths;
        _fileLogger = new RollingFileLogger(paths.LogDirectory);
        _diagnosticExporter = new DiagnosticBundleExporter(Logger, _runtimeMetrics);
        _crashMetadataWriter = new CrashMetadataWriter(paths.CrashSummaryFilePath, Logger);
        _ = _processMetrics.Sample();
        Logger.Information("APP", "Starting.");
        _ = new LegacyCredentialRetirementService(
            paths.LegacyDiscordClientSecretFilePath,
            paths.LegacyDiscordOAuthTokenFilePath,
            Logger).Retire();

        _lifetime = new ApplicationLifetime();
        _settingsStore = new JsonSettingsStore(paths.SettingsFilePath, Logger);
        var settings = _settingsStore.Load();
        _windowsAutoStartService = new WindowsAutoStartService();
        if (settings.WindowsAutoStart && !_windowsAutoStartService.Apply(enabled: true))
        {
            _settingsStore.Update(current => current with { WindowsAutoStart = false });
            settings = _settingsStore.Current;
            Logger.Warning("STARTUP", "Windows auto-start registration could not be repaired and was disabled.");
        }
        _colorThemeManager = new ColorThemeManager(_application);
        _colorThemeManager.Apply(settings.ColorTheme);
        Logger.Information("THEME", $"Applied {settings.ColorTheme}.");
        _localization = new ResourceLocalizationService(settings.Language, Logger);
        Logger.Information("LOCALIZATION", $"Language = {_localization.CurrentLocale}");

        _displayNameResolver = new GuildDisplayNameResolver(
            new JsonGuildDisplayNameCacheStore(
                paths.GuildDisplayNameCacheFilePath,
                Logger));
        var builtInProductCatalog = EmbeddedSalesProductCatalogLoader.Load(Logger);
        _productCatalogStore = new EffectiveSalesProductCatalogStore(
            builtInProductCatalog,
            paths.SalesProductOverrideFilePath,
            paths.SalesProductCatalogFilePath,
            Logger);
        var productCatalog = _productCatalogStore.EffectiveCatalog;

        var hudState = new HudStateService(settings.HudVisibilityMode);
        var hudWindow = new HudWindow();
        var chatViewModel = new ChatViewModel();
        var salesViewModel = new SalesQueueViewModel(_localization);
        var sessionViewModel = new SessionHudViewModel(_localization, settings);
        sessionViewModel.UpdateRemoteState(true, SessionRemoteState.Awaiting);
        _sessionHudViewModel = sessionViewModel;
        var hudViewModel = new HudShellViewModel(
            _localization,
            chatViewModel,
            salesViewModel,
            sessionViewModel);
        var typographyResolver = new ChatTypographyResolver(Logger);
        var chatCoordinator = new ChatPresentationCoordinator(
            chatViewModel,
            new DiscordMediaAssetService(Logger, _runtimeMetrics),
            _localization,
            Logger,
            settings,
            typographyResolver,
            _runtimeMetrics);
        var interop = new WindowInteropService(hudWindow, () => hudState.Current.IsLocked, Logger);
        var placement = new WindowPlacementService(
            hudWindow,
            interop,
            new DisplayTopologyService(),
            new WindowPlacementEngine(),
            _settingsStore,
            Logger);
        var hotkeys = new GlobalHotkeyService(interop, Logger);
        var gameMonitor = new GameForegroundMonitor(new TargetGameMatcher(), Logger);
        var modifierDrag = new ModifierDragService(interop, Logger);
        _salesNotificationSoundService = new SalesNotificationSoundService(
            hudWindow.Dispatcher,
            paths.NotificationToneDirectory,
            Logger);
        var salesTurnNotifications = new SalesTurnNotificationCoordinator(
            () => _settingsStore!.Current,
            _salesNotificationSoundService,
            Logger);
        _salesCoordinator = new SalesPresentationCoordinator(
            new SalesStateEngine(
                _displayNameResolver,
                productCatalog,
                Logger,
                _localization.CurrentLocale),
            salesViewModel,
            _localization,
            Logger,
            settings,
            hudWindow.Dispatcher,
            _runtimeMetrics,
            salesTurnNotifications);
        _salesCoordinator.Start();
        _hudController = new HudWindowController(
            hudWindow,
            hudViewModel,
            hudState,
            interop,
            placement,
            hotkeys,
            gameMonitor,
            modifierDrag,
            chatCoordinator,
            () => OpenSettings(SettingsOpenSource.HudGear, SettingsCategory.Hud),
            _localization,
            Logger,
            settings);

        var remoteMessagePipeline = new DiscordMessagePipeline(
            Logger,
            metrics: _runtimeMetrics);
        _remoteAccessCredentials = new DpapiRemoteAccessCredentialStore(
            paths.RemoteAccessTokenFilePath,
            Logger);
        _recoveryAudit = RemoteRecoveryAudit.FromEnvironment(Logger);
        _remoteChatCoordinator = new RemoteChatProductionCoordinator(
            _settingsStore,
            _remoteAccessCredentials,
            remoteMessagePipeline,
            paths.RemoteInstallationIdFilePath,
            Logger,
            recoveryAudit: _recoveryAudit);
        salesViewModel.ConfigureStatusAction(
            _remoteChatCoordinator.SetSalesStatusAsync);
        _remoteChatCoordinator.MessageStateChanged += OnRemoteMessageStateChanged;
        _remoteChatCoordinator.AuthenticatedUserChanged += OnRemoteAuthenticatedUserChanged;
        _remoteChatCoordinator.PresenceBootstrapReady += OnRemotePresenceBootstrapReady;
        _remoteChatCoordinator.HostPresenceChanged += OnRemoteHostPresenceChanged;
        _remoteChatCoordinator.SnapshotChanged += OnRemoteSnapshotChanged;
        _remoteChatCoordinator.SalesBootstrapReady += OnRemoteSalesBootstrapReady;
        _remoteChatCoordinator.SalesMutationReceived += OnRemoteSalesMutationReceived;
        _remoteChatCoordinator.SalesStatusChanged += OnRemoteSalesStatusChanged;
        var remoteSettingsViewModel = new RemoteChatSettingsViewModel(
            _localization,
            _remoteChatCoordinator.Snapshot,
            ApplyRemoteConfigurationAsync,
            _remoteChatCoordinator.BeginLoginAsync,
            _remoteChatCoordinator.CancelLogin,
            _remoteChatCoordinator.ForgetCredentialAsync,
            _remoteChatCoordinator.RefreshAsync,
            _remoteChatCoordinator.SwitchChannelAsync);

        _foundationViewModel = new FoundationViewModel(
            _settingsStore,
            _localization,
            Logger,
            typographyResolver,
            () => _settingsWindowService?.Hide(),
            updated =>
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    _hudController?.ApplySettings(updated);
                    _salesCoordinator?.ApplySettings(updated);
                    _remoteChatCoordinator?.NotifySalesTrackingChanged();
                }
                finally
                {
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
                    _runtimeMetrics.RecordDuration(
                        RuntimeMetricNames.SettingsUpdateDuration,
                        elapsed);
                    if (elapsed >= TimeSpan.FromMilliseconds(50))
                    {
                        _runtimeMetrics.Increment(RuntimeMetricNames.DispatcherLongOperations);
                    }
                }
            },
            () => _hudController?.ResetPlacement(),
            (lockSetting, visibilitySetting) =>
                _hudController?.TryApplyHotkeys(lockSetting, visibilitySetting) == true,
            ShowProductMappingManager,
            ExportProductMappings,
            ShowSalesPreview,
            RequestManualSalesResync,
            ClearMediaCache,
            ResetAllSettings,
            () => _salesCoordinator?.GetHealthSnapshot() ?? SalesFeatureHealthSnapshot.Disabled,
            OpenLogFolder,
            OpenLatestLog,
            () => _hudController?.ResetPosition(),
            () => _hudController?.ResetSize(),
            () => _hudController?.CenterOnCurrentDisplay(),
            ApplyColorTheme,
            GetProductCatalogSnapshot,
            ResetProductOverrides,
            ShowCredits,
            OpenBundledLicenseNotices,
            applyWindowsAutoStart: ApplyWindowsAutoStart,
            rerunOnboarding: () => ShowOnboarding(restartFromBeginning: true),
            createDiagnosticBundle: CreateDiagnosticBundleAsync,
            remoteChatSettings: remoteSettingsViewModel,
            testSalesTurnSound: () => _salesNotificationSoundService?.Play(
                SalesTurnNotificationKind.Current,
                _settingsStore!.Current.SalesTurnSoundVolume));
        _settingsWindowService = new SettingsWindowService(
            new UiDispatcherAdapter(_application.Dispatcher),
            () => new FoundationWindow
            {
                DataContext = _foundationViewModel,
            },
            Logger,
            ShowSettingsOpenFailure);

        _trayIcon = new TrayIconService(
            _localization,
            Logger,
            () => _hudController?.ToggleUserVisibility(),
            () => _hudController?.ToggleLock(),
            () => OpenSettings(SettingsOpenSource.Tray, null),
            () => _ = _remoteChatCoordinator?.RefreshAsync(),
            _requestShutdown);
        _hudController.StateApplied += OnHudStateApplied;
        _hudController.Start();
        _trayIcon.UpdateHudState(_hudController.State);

        _remoteChatCoordinator.Start();
        if (_settingsStore.Current.OnboardingVersion < AppSettings.CurrentOnboardingVersion)
        {
            ShowOnboarding(restartFromBeginning: false);
        }
        Logger.Information("APP", "Started; HUD foundation ready and initial connection gate closed.");
    }

    public void PrepareForShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownPrepared, 1) != 0)
        {
            return;
        }

        Logger.Information("APP", "Shutdown requested.");
        _lifetime?.Stop();
        _settingsWindowService?.PrepareForApplicationExit();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        PrepareForShutdown();

        var productMappingWindow = _productMappingWindow;
        _productMappingWindow = null;
        productMappingWindow?.Close();
        var salesPreviewWindow = _salesPreviewWindow;
        _salesPreviewWindow = null;
        salesPreviewWindow?.Close();
        var onboardingWindow = _onboardingWindow;
        _onboardingWindow = null;
        onboardingWindow?.Close();
        _onboardingViewModel?.Dispose();
        _onboardingViewModel = null;

        _settingsWindowService?.Dispose();
        _settingsWindowService = null;
        _foundationViewModel?.Dispose();
        _foundationViewModel = null;

        _salesCoordinator?.Dispose();
        _salesCoordinator = null;
        _salesNotificationSoundService?.Dispose();
        _salesNotificationSoundService = null;
        _sessionHudViewModel = null;

        if (_remoteChatCoordinator is not null)
        {
            _remoteChatCoordinator.MessageStateChanged -= OnRemoteMessageStateChanged;
            _remoteChatCoordinator.PresenceBootstrapReady -= OnRemotePresenceBootstrapReady;
            _remoteChatCoordinator.HostPresenceChanged -= OnRemoteHostPresenceChanged;
            _remoteChatCoordinator.SalesBootstrapReady -= OnRemoteSalesBootstrapReady;
            _remoteChatCoordinator.SalesMutationReceived -= OnRemoteSalesMutationReceived;
            _remoteChatCoordinator.SalesStatusChanged -= OnRemoteSalesStatusChanged;
            _remoteChatCoordinator.AuthenticatedUserChanged -= OnRemoteAuthenticatedUserChanged;
            _remoteChatCoordinator.SnapshotChanged -= OnRemoteSnapshotChanged;
            try
            {
                _remoteChatCoordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Logger.Error("REMOTE", "Remote chat coordinator shutdown failed.", exception);
            }

            _remoteChatCoordinator = null;
        }

        _recoveryAudit?.Dispose();
        _recoveryAudit = null;
        _remoteAccessCredentials = null;

        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_hudController is not null)
        {
            _hudController.StateApplied -= OnHudStateApplied;
            _hudController.Dispose();
            _hudController = null;
        }

        _displayNameResolver = null;
        _lifetime?.Dispose();
        _lifetime = null;

        Logger.Information("APP", "Ordered shutdown cleanup completed; Dispatcher shutdown may proceed.");
        Logger.Information("APP", "Exit.");
        _fileLogger?.Dispose();
        _fileLogger = null;
        _diagnosticExporter = null;
        _crashMetadataWriter = null;
        _paths = null;
    }

    private void OnRemoteMessageStateChanged(DiscordMessageState state)
    {
        _lastRemoteMessageState = state;
        _runtimeMetrics.SetGauge(RuntimeMetricNames.ChatActiveMainMessages, state.MainChat.Count);
        _hudController?.OnDiscordMessageStateChanged(state);
        if (!state.IsBootstrapping)
        {
            Logger.Information(
                "REMOTE",
                $"Published remote generation={state.Generation} main_count={state.MainChat.Count}.");
            if (_imagePayloadObserved == 0 && state.MainChat.Any(message =>
                    message.Attachments.Any(attachment =>
                        !string.IsNullOrWhiteSpace(attachment.Url) ||
                        !string.IsNullOrWhiteSpace(attachment.ProxyUrl)) ||
                    message.Embeds.Any(embed =>
                        !string.IsNullOrWhiteSpace(embed.ImageUrl) ||
                        !string.IsNullOrWhiteSpace(embed.ThumbnailUrl)) ||
                    message.Stickers.Count > 0) &&
                Interlocked.Exchange(ref _imagePayloadObserved, 1) == 0)
            {
                Logger.Information(
                    "MEDIA",
                    "Remote Main Chat image or sticker metadata observed.");
            }
        }
    }

    private void OnRemoteAuthenticatedUserChanged(DiscordAuthenticatedUser user)
    {
        _salesCoordinator?.SetAuthenticatedUser(user.UserId);
        _hudController?.OnAuthenticatedUserChanged(user);
    }

    private void OnRemoteSalesBootstrapReady(LSOverlay.Protocol.SalesBootstrapResponse bootstrap) =>
        _salesCoordinator?.ApplyRemoteSalesBootstrap(bootstrap);

    private void OnRemoteSalesMutationReceived(LSOverlay.Protocol.SalesMutationEnvelope mutation) =>
        _salesCoordinator?.ApplyRemoteSalesMutation(mutation);

    private void OnRemoteSalesStatusChanged(string status) =>
        _salesCoordinator?.ApplyRemoteSalesStatus(status);

    private void OnRemotePresenceBootstrapReady(LSOverlay.Protocol.BootstrapResponse bootstrap) =>
        DispatchToUi(() => _sessionHudViewModel?.ApplyBootstrap(bootstrap));

    private void OnRemoteHostPresenceChanged(LSOverlay.Protocol.HostPresenceSnapshot presence) =>
        DispatchToUi(() => _sessionHudViewModel?.ApplyPresence(presence));

    private void OnRemoteSnapshotChanged(RemoteChatSnapshot snapshot) =>
        DispatchToUi(() =>
        {
            _hudController?.OnRemoteConnectionStatus(snapshot);
            _trayIcon?.UpdateRemoteStatus(snapshot);
            _foundationViewModel?.RemoteChatSettings?.UpdateSnapshot(snapshot);
            _sessionHudViewModel?.UpdateRemoteState(
                true,
                snapshot.Health switch
                {
                    RemoteChatHealthState.Live => SessionRemoteState.Live,
                    RemoteChatHealthState.Reconnecting => SessionRemoteState.Reconnecting,
                    RemoteChatHealthState.Disconnected or
                        RemoteChatHealthState.AuthorizationUnavailable or
                        RemoteChatHealthState.AccessRevoked or
                        RemoteChatHealthState.Error => SessionRemoteState.Unavailable,
                    _ => SessionRemoteState.Awaiting,
                });
        });

    private async Task<bool> ApplyRemoteConfigurationAsync(string backendBaseUrl)
    {
        if (_remoteChatCoordinator is null)
        {
            return false;
        }

        return await _remoteChatCoordinator.ApplyConfigurationAsync(backendBaseUrl)
            .ConfigureAwait(false);
    }

    private void OnHudStateApplied(HudSessionState state) =>
        _trayIcon?.UpdateHudState(state);

    private bool ApplyWindowsAutoStart(bool enabled)
    {
        var applied = _windowsAutoStartService?.Apply(enabled) == true;
        Logger.Information("STARTUP", $"Windows auto-start update result={(applied ? "Applied" : "Failed")}.");
        return applied;
    }

    private void ShowOnboarding(bool restartFromBeginning)
    {
        if (_foundationViewModel is null || _settingsStore is null || _localization is null)
        {
            return;
        }

        if (_onboardingWindow is not null)
        {
            if (!_onboardingWindow.IsVisible)
            {
                _onboardingWindow.Show();
            }

            _onboardingWindow.Activate();
            return;
        }

        _settingsWindowService?.Hide();
        _onboardingViewModel = new OnboardingViewModel(
            _foundationViewModel,
            _settingsStore,
            _localization,
            CompleteOnboarding,
            restartFromBeginning);
        var window = new OnboardingWindow
        {
            DataContext = _onboardingViewModel,
        };
        window.Closed += OnOnboardingClosed;
        _onboardingWindow = window;
        window.Show();
        window.Activate();
    }

    private void CompleteOnboarding()
    {
        Logger.Information("ONBOARDING", "Remote-only setup completed.");
        var window = _onboardingWindow;
        window?.Close();
        _ = _remoteChatCoordinator?.RefreshAsync();
    }

    private void OnOnboardingClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is OnboardingWindow window)
        {
            window.Closed -= OnOnboardingClosed;
        }

        _onboardingWindow = null;
        _onboardingViewModel?.Dispose();
        _onboardingViewModel = null;
    }

    private void DispatchToUi(Action action)
    {
        if (_application.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _application.Dispatcher.BeginInvoke(action);
        }
    }

    private void OpenSettings(SettingsOpenSource source, SettingsCategory? category)
    {
        if (Volatile.Read(ref _shutdownPrepared) != 0)
        {
            Logger.Warning(
                "WINDOW",
                $"Settings open ignored source={source} category={category?.ToString() ?? "LastVisited"} because application exit is in progress.");
            return;
        }

        _settingsWindowService?.Open(source, category);
    }

    private void ShowSettingsOpenFailure()
    {
        var message = _localization?["SettingsOpenFailed"]
            ?? "The Settings window could not be opened. Check the application log.";
        var title = _localization?["SettingsTitle"] ?? "Settings";
        System.Windows.MessageBox.Show(
            message,
            title,
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
    }

    private void ShowProductMappingManager()
    {
        if (_productCatalogStore is null || _salesCoordinator is null || _localization is null)
        {
            return;
        }

        if (_productMappingWindow is { IsVisible: true })
        {
            _productMappingWindow.Activate();
            return;
        }

        var window = new ProductMappingManagerWindow
        {
            Owner = SettingsOwnerWindow,
            DataContext = new ProductMappingManagerViewModel(
                _productCatalogStore,
                _salesCoordinator.GetEmojiInventory,
                ApplyProductCatalog,
                _localization,
                () => System.Windows.MessageBox.Show(
                    (System.Windows.Window?)_productMappingWindow ?? SettingsOwnerWindow,
                    _localization["SettingsProductDeleteConfirm"],
                    _localization["SettingsProductMappingManager"],
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes),
        };
        window.Closed += (_, _) => _productMappingWindow = null;
        _productMappingWindow = window;
        window.Show();
    }

    private void ExportProductMappings()
    {
        if (_productCatalogStore is null || _salesCoordinator is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            FileName = $"gacha-overlay-products-{DateTime.Now:yyyyMMdd-HHmmss}.json",
            Filter = _localization?["SettingsProductExportFilter"] ?? "JSON files (*.json)|*.json",
            Title = _localization?["SettingsExportProductMappings"] ?? "Export product mappings",
        };
        if (dialog.ShowDialog(SettingsOwnerWindow) == true)
        {
            _productCatalogStore.Export(
                dialog.FileName,
                _salesCoordinator.Engine.ProductCatalog);
        }
    }

    private void ApplyProductCatalog(SalesProductCatalog catalog)
    {
        _salesCoordinator?.ReplaceProductCatalog(catalog);
        _foundationViewModel?.RefreshDiagnostics();
    }

    private ProductCatalogUiSnapshot GetProductCatalogSnapshot()
    {
        if (_productCatalogStore is null)
        {
            return ProductCatalogUiSnapshot.Empty;
        }

        return new ProductCatalogUiSnapshot(
            _productCatalogStore.BuiltInLoaded,
            _productCatalogStore.BuiltInCatalog.Products.Count,
            _productCatalogStore.BuiltInCatalog.Products
                .Select(product => product.ProductId)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            _productCatalogStore.OverrideCount);
    }

    private bool ResetProductOverrides()
    {
        if (_productCatalogStore is null || _localization is null ||
            System.Windows.MessageBox.Show(
                SettingsOwnerWindow,
                _localization["SettingsDeveloperResetOverridesConfirm"],
                _localization["SettingsDeveloperResetOverrides"],
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return false;
        }

        if (!_productCatalogStore.ResetOverrides())
        {
            return false;
        }

        ApplyProductCatalog(_productCatalogStore.EffectiveCatalog);
        _productMappingWindow?.Close();
        Logger.Information("PRODUCT", "All developer product overrides reset; built-in catalog restored.");
        return true;
    }

    private void ShowCredits()
    {
        if (_localization is null)
        {
            return;
        }

        System.Windows.MessageBox.Show(
            SettingsOwnerWindow,
            _localization["SettingsDeveloperCreditsContent"],
            _localization["SettingsDeveloperCredits"],
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private static void OpenBundledLicenseNotices()
    {
        var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "Licenses");
        if (!System.IO.Directory.Exists(directory))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private void ShowSalesPreview()
    {
        if (_settingsStore is null || _localization is null)
        {
            return;
        }

        if (_salesPreviewWindow is { IsVisible: true })
        {
            _salesPreviewWindow.Activate();
            return;
        }

        var window = new SalesPreviewWindow
        {
            Owner = SettingsOwnerWindow,
            DataContext = new SalesPreviewViewModel(_localization, _settingsStore.Current),
        };
        window.Closed += (_, _) => _salesPreviewWindow = null;
        _salesPreviewWindow = window;
        window.Show();
    }

    private ManualSalesResyncResult RequestManualSalesResync() =>
        _remoteChatCoordinator?.RequestSalesResync() ??
        ManualSalesResyncResult.RemoteUnavailable;

    private static void OpenLogFolder()
    {
        var directory = new LocalApplicationPaths().LogDirectory;
        System.IO.Directory.CreateDirectory(directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true,
        });
    }

    private static void OpenLatestLog()
    {
        var directory = new LocalApplicationPaths().LogDirectory;
        System.IO.Directory.CreateDirectory(directory);
        var latest = System.IO.Directory.EnumerateFiles(
                directory,
                "*.log",
                System.IO.SearchOption.TopDirectoryOnly)
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = latest ?? directory,
            UseShellExecute = true,
        });
    }

    private async Task<string> CreateDiagnosticBundleAsync()
    {
        if (_diagnosticExporter is null || _paths is null || _settingsStore is null ||
            _localization is null)
        {
            return _localization?["SettingsDiagnosticFailed"] ??
                "The diagnostic file could not be created.";
        }

        var stage = DiagnosticExportStage.SelectDestination;
        try
        {
            var diagnosticDirectory = System.IO.Path.Combine(_paths.DataDirectory, "Diagnostics");
            System.IO.Directory.CreateDirectory(diagnosticDirectory);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".zip",
                FileName = $"GachaOverlay-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                Filter = _localization["SettingsDiagnosticZipFilter"],
                Title = _localization["SettingsCreateDiagnosticBundle"],
                InitialDirectory = diagnosticDirectory,
            };
            if (dialog.ShowDialog(SettingsOwnerWindow) != true)
            {
                return _localization["SettingsDiagnosticCancelled"];
            }

            stage = DiagnosticExportStage.CreateSnapshot;
            var request = BuildDiagnosticRequest(dialog.FileName);
            var result = await _diagnosticExporter.ExportAsync(request).ConfigureAwait(true);
            return result.Status switch
            {
                DiagnosticBundleExportStatus.Succeeded => string.Format(
                    System.Globalization.CultureInfo.CurrentUICulture,
                    _localization["SettingsDiagnosticCreated"],
                    result.DestinationPath),
                DiagnosticBundleExportStatus.Busy => _localization["SettingsDiagnosticBusy"],
                DiagnosticBundleExportStatus.Cancelled =>
                    _localization["SettingsDiagnosticCancelled"],
                _ => $"{_localization["SettingsDiagnosticFailed"]} [{result.FailureStage}/{result.FailureType}]",
            };
        }
        catch (Exception exception)
        {
            Logger.Error("DIAGNOSTICS", $"exportStage={stage} entry=none result=Failed exception={exception.GetType().Name}");
            return $"{_localization["SettingsDiagnosticFailed"]} [{stage}/{exception.GetType().Name}]";
        }
    }

    internal DiagnosticBundleRequest BuildDiagnosticRequest(string destinationPath)
    {
        var settings = _settingsStore?.Current ?? AppSettings.CreateDefault();
        var runtime = _runtimeMetrics.Snapshot();
        var process = _processMetrics.Sample();
        var messageState = _lastRemoteMessageState;
        var remote = _remoteChatCoordinator?.Snapshot ??
            RemoteChatSnapshot.Disconnected(settings.RemoteBackendBaseUrl);
        var sales = _salesCoordinator?.GetHealthSnapshot() ?? SalesFeatureHealthSnapshot.Disabled;
        var catalog = GetProductCatalogSnapshot();
        var counters = runtime.Counters;
        var latestErrorCount = counters
            .Where(pair =>
                pair.Key.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("failure", StringComparison.OrdinalIgnoreCase))
            .Sum(pair => pair.Value);
        var displays = GetDisplaySummary();
        var artifacts = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["diagnostic-summary.json"] = new
            {
                AppVersion = System.Reflection.Assembly.GetEntryAssembly()
                    ?.GetName().Version?.ToString(3) ?? "unknown",
                BuildConfiguration = GetBuildConfiguration(),
                UptimeSeconds = runtime.UptimeSeconds,
                OsVersion = Environment.OSVersion.VersionString,
                RuntimeVersion = Environment.Version.ToString(),
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture.ToString(),
                CurrentLanguage = settings.Language,
                CurrentTheme = settings.ColorTheme.ToString(),
                Remote = new
                {
                    remote.Health,
                    remote.Detail,
                    remote.HasProtectedCredential,
                    AuthorizedChannelCount = remote.Channels.Count,
                    ChannelSelected = remote.SelectedChannelId is not null,
                },
                MainSource = new
                {
                    messageState.Generation,
                    messageState.IsBootstrapping,
                    MessageCount = messageState.MainChat.Count,
                },
                Sales = new
                {
                    sales.State,
                    sales.Reason,
                    sales.Coverage,
                    sales.EffectiveSource,
                    sales.RemotePhase,
                },
                LatestErrorCount = latestErrorCount,
                CatalogLoaded = catalog.BuiltInLoaded,
            },
            ["sanitized-settings.json"] = SanitizedSettingsSnapshot.From(settings),
            ["runtime-metrics.json"] = new { Runtime = runtime, Process = process },
            ["health-snapshot.json"] = new
            {
                Remote = new
                {
                    remote.Health,
                    remote.Detail,
                    remote.HasProtectedCredential,
                    AuthorizedChannelCount = remote.Channels.Count,
                    ChannelSelected = remote.SelectedChannelId is not null,
                },
                MainMessageCount = messageState.MainChat.Count,
                SalesSourceCount = messageState.SalesSource.Count,
                Sales = sales,
            },
            ["environment-summary.json"] = new
            {
                WindowsVersion = Environment.OSVersion.VersionString,
                RuntimeVersion = Environment.Version.ToString(),
                ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation
                    .ProcessArchitecture.ToString(),
                MonitorCount = displays.Count,
                Monitors = displays,
                CurrentHudMonitor = settings.HudWindowGeometry?.DisplayId,
                TempAvailable = IsDirectoryAvailable(System.IO.Path.GetTempPath()),
                LocalAppDataAvailable = _paths is not null &&
                    IsDirectoryAvailable(_paths.DataDirectory),
            },
            ["catalog-summary.json"] = new
            {
                catalog.BuiltInLoaded,
                BuiltInMappingCount = catalog.BuiltInMappingCount,
                BuiltInGroupCount = catalog.BuiltInGroupCount,
                catalog.OverrideCount,
                EffectiveCount = _productCatalogStore?.EffectiveCatalog.Products.Count ?? 0,
            },
        };
        return new DiagnosticBundleRequest(
            destinationPath,
            artifacts,
            _paths?.LogDirectory,
            _paths?.CrashSummaryFilePath);
    }

    private static IReadOnlyList<object> GetDisplaySummary()
    {
        try
        {
            return new DisplayTopologyService().GetWorkingAreas()
                .Select(display => (object)new
                {
                    display.Id,
                    Width = display.Bounds.Width,
                    Height = display.Bounds.Height,
                    display.Dpi,
                    display.IsPrimary,
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static bool IsDirectoryAvailable(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            return System.IO.Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string GetBuildConfiguration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private void ClearMediaCache()
    {
        _hudController?.ClearMediaCache();
        Logger.Information("MEDIA", "In-memory media caches cleared by user request.");
    }

    private void ResetAllSettings()
    {
        if (_settingsStore is null || _localization is null ||
            System.Windows.MessageBox.Show(
                SettingsOwnerWindow,
                _localization["SettingsResetAllConfirm"],
                _localization["SettingsResetAll"],
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        if (!_settingsStore.Save(AppSettings.CreateDefault()))
        {
            return;
        }

        var defaults = _settingsStore.Current;
        _localization.SetLanguage(defaults.Language);
        ApplyColorTheme(defaults.ColorTheme);
        _hudController?.ApplySettings(defaults);
        _salesCoordinator?.ApplySettings(defaults);
        _foundationViewModel?.ReloadFromCurrentSettings();
        Logger.Information("SETTINGS", "All application settings reset by user request.");
    }

    private void ApplyColorTheme(ColorThemeId theme)
    {
        _colorThemeManager?.Apply(theme);
        _hudController?.RefreshTheme();
        _salesPreviewWindow?.RefreshTheme();
        Logger.Information("THEME", $"Applied {ColorThemeCatalog.Get(theme).Id}.");
    }
}
