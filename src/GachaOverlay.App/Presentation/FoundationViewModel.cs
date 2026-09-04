using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Logging;
using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Sales;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Hud.Hotkeys;
using GachaOverlay.Core.Chat;
using GachaOverlay.Core.Themes;
using GachaOverlay.Core.Timers;

namespace GachaOverlay.App.Presentation;

internal sealed class FoundationViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly IAppLogger _logger;
    private readonly ChatTypographyResolver _typographyResolver;
    private readonly Action<AppSettings> _applyHudSettings;
    private readonly Func<HotkeySetting, HotkeySetting, bool> _applyHotkeys;
    private readonly Func<AppSettings, bool> _applyAllHotkeys;
    private string _previousChannelHotkeyText = string.Empty;
    private string _nextChannelHotkeyText = string.Empty;
    private string _generalTimerHotkeyText = string.Empty;
    private string _bunkerTimerHotkeyText = string.Empty;
    private string _lsdTimerHotkeyText = string.Empty;
    private int _generalTimerMinutes;
    private int _bunkerTimerMinutes;
    private int _lsdTimerMinutes;
    private bool _timerCompletionSoundEnabled;
    private readonly Func<SalesFeatureHealthSnapshot> _getSalesHealthSnapshot;
    private readonly Func<ManualSalesResyncResult> _manualSalesResync;
    private readonly Action _clearMediaCache;
    private readonly Action<ColorThemeId> _applyColorTheme;
    private readonly Func<ProductCatalogUiSnapshot> _getProductCatalogSnapshot;
    private readonly Func<bool> _resetProductOverrides;
    private ProductCatalogUiSnapshot _productCatalogSnapshot = ProductCatalogUiSnapshot.Empty;
    private SettingsCategory _selectedSettingsCategory;
    private IReadOnlyList<SettingsCategoryOption> _settingsCategories;
    private string _selectedLanguage;
    private ColorThemeId _selectedColorTheme;
    private double _hudSurfaceOpacity;
    private double _hudChromeOpacity;
    private double _chatSurfaceOpacity;
    private double _salesSurfaceOpacity;
    private double _queueDetailSurfaceOpacity;
    private bool _minimalHudMode;
    private bool _showGtaSession;
    private SessionHostSelection _selectedSessionHost;
    private IReadOnlyList<SessionHostOption> _sessionHostOptions;
    private bool _hudModifierDragEnabled;
    private HudVisibilityMode _selectedVisibilityMode;
    private string _lockHotkeyText;
    private string _visibilityHotkeyText;
    private string _hotkeyValidationMessage = string.Empty;
    private IReadOnlyList<HudVisibilityModeOption> _visibilityModes;
    private ChatLayoutMode _selectedChatLayoutMode;
    private ChatStylePreset _selectedChatStylePreset;
    private string _chatPresetStatusMessage = string.Empty;
    private bool _chatShowTime;
    private ChatFontPreset _selectedChatFontPreset;
    private double _chatFontSizePoints;
    private bool _chatNicknameOutlineEnabled;
    private bool _chatMessageOutlineEnabled;
    private double _chatNicknameOutlineThickness;
    private double _chatMessageOutlineThickness;
    private int _chatMaxLines;
    private double _chatLineHeightMultiplier;
    private double _chatMessageSpacing;
    private RoleIconPosition _selectedRoleIconPosition;
    private double _chatReactionSize;
    private bool _chatShowImages;
    private ChatImageMode _selectedChatImageMode;
    private ChatImageSizeMode _selectedChatImageSizeMode;
    private bool _chatCustomEmojiEnabled;
    private bool _chatStickerEnabled;
    private bool _hidePreviewSourceUrl;
    private bool _salesTrackingEnabled;
    private bool _salesShowCurrentSeller;
    private bool _salesShowWaitingCount;
    private bool _salesShowProduct;
    private bool _salesShowNextWaitingUser;
    private double _salesQueueDetailMaxHeight;
    private bool _salesTurnSoundEnabled;
    private double _salesTurnSoundVolume;
    private bool _notifySalesNext;
    private bool _notifySalesCurrent;
    private SalesFeatureHealthSnapshot _salesHealthSnapshot = SalesFeatureHealthSnapshot.Disabled;
    private string _manualSalesResyncStatusMessage = string.Empty;
    private string _mediaCacheStatusMessage = string.Empty;
    private IReadOnlyList<ColorThemeOption> _colorThemes;
    private IReadOnlyList<ChatLayoutModeOption> _chatLayoutModes;
    private IReadOnlyList<ChatFontPresetOption> _chatFontPresets;
    private IReadOnlyList<ChatImageModeOption> _chatImageModes;
    private IReadOnlyList<ChatImageSizeModeOption> _chatImageSizeModes;
    private IReadOnlyList<ChatStylePresetOption> _chatStylePresets;
    private IReadOnlyList<ChatLineLimitOption> _chatMaxLineOptions;
    private IReadOnlyList<RoleIconPositionOption> _roleIconPositions;
    private bool _disposed;
    private readonly Func<bool, bool> _applyWindowsAutoStart;
    private bool _windowsAutoStart;
    private readonly Func<Task<string>> _createDiagnosticBundle;
    private string _diagnosticExportStatusMessage = string.Empty;
    private readonly Action _testSalesTurnSound;

    public FoundationViewModel(
        ISettingsStore settingsStore,
        ILocalizationService localization,
        IAppLogger logger,
        ChatTypographyResolver typographyResolver,
        Action hideWindow,
        Action<AppSettings> applyHudSettings,
        Action resetHudPlacement,
        Func<HotkeySetting, HotkeySetting, bool>? applyHotkeys = null,
        Action? openProductMappingManager = null,
        Action? exportProductMappings = null,
        Action? openSalesPreview = null,
        Func<ManualSalesResyncResult>? manualSalesResync = null,
        Action? clearMediaCache = null,
        Action? resetAllSettings = null,
        Func<SalesFeatureHealthSnapshot>? getSalesHealthSnapshot = null,
        Action? openLogFolder = null,
        Action? openLatestLog = null,
        Action? resetHudPosition = null,
        Action? resetHudSize = null,
        Action? centerHudOnCurrentDisplay = null,
        Action<ColorThemeId>? applyColorTheme = null,
        Func<ProductCatalogUiSnapshot>? getProductCatalogSnapshot = null,
        Func<bool>? resetProductOverrides = null,
        Action? showCredits = null,
        Action? openLicenseNotices = null,
        Func<bool, bool>? applyWindowsAutoStart = null,
        Action? rerunOnboarding = null,
        Func<Task<string>>? createDiagnosticBundle = null,
        RemoteChatSettingsViewModel? remoteChatSettings = null,
        Action? testSalesTurnSound = null,
        Func<AppSettings, bool>? applyAllHotkeys = null,
        SalesHistoryViewModel? salesHistory = null)
    {
        _settingsStore = settingsStore;
        Localization = localization;
        _logger = logger;
        _typographyResolver = typographyResolver;
        _applyHudSettings = applyHudSettings;
        _applyHotkeys = applyHotkeys ?? ((_, _) => true);
        _applyAllHotkeys = applyAllHotkeys ?? (value => _applyHotkeys(value.HudLockHotkey, value.HudVisibilityHotkey));
        _getSalesHealthSnapshot = getSalesHealthSnapshot ?? (() => SalesFeatureHealthSnapshot.Disabled);
        _manualSalesResync = manualSalesResync ?? (() => ManualSalesResyncResult.TrackingDisabled);
        _clearMediaCache = clearMediaCache ?? (() => { });
        _applyColorTheme = applyColorTheme ?? (_ => { });
        _getProductCatalogSnapshot = getProductCatalogSnapshot ??
            (() => ProductCatalogUiSnapshot.Empty);
        _resetProductOverrides = resetProductOverrides ?? (() => false);
        _applyWindowsAutoStart = applyWindowsAutoStart ?? (_ => true);
        _createDiagnosticBundle = createDiagnosticBundle ??
            (() => Task.FromResult(string.Empty));
        RemoteChatSettings = remoteChatSettings;
        SalesHistory = salesHistory;
        _testSalesTurnSound = testSalesTurnSound ?? (() => { });
        _selectedLanguage = localization.CurrentLocale;
        var settings = settingsStore.Current;
        _selectedColorTheme = settings.ColorTheme;
        _selectedSettingsCategory = settings.LastSettingsCategory;
        _settingsCategories = CreateSettingsCategories();
        _hudSurfaceOpacity = settings.HudSurfaceOpacity;
        _hudChromeOpacity = settings.HudChromeOpacity;
        _chatSurfaceOpacity = settings.ChatSurfaceOpacity;
        _salesSurfaceOpacity = settings.SalesSurfaceOpacity;
        _queueDetailSurfaceOpacity = settings.QueueDetailSurfaceOpacity;
        _minimalHudMode = settings.MinimalHudMode;
        _showGtaSession = settings.ShowGtaSession;
        _selectedSessionHost = settings.SelectedSessionHost;
        _hudModifierDragEnabled = settings.HudModifierDragEnabled;
        _selectedVisibilityMode = settings.HudVisibilityMode;
        _lockHotkeyText = FormatHotkey(settings.HudLockHotkey, HotkeySetting.DefaultLockToggle);
        _previousChannelHotkeyText = FormatOptionalHotkey(settings.PreviousMainChannelHotkey);
        _nextChannelHotkeyText = FormatOptionalHotkey(settings.NextMainChannelHotkey);
        _generalTimerHotkeyText = FormatOptionalHotkey(settings.GeneralTimerHotkey);
        _bunkerTimerHotkeyText = FormatOptionalHotkey(settings.BunkerTimerHotkey);
        _lsdTimerHotkeyText = FormatOptionalHotkey(settings.LsdTimerHotkey);
        _generalTimerMinutes = settings.GeneralTimerMinutes;
        _bunkerTimerMinutes = settings.BunkerTimerMinutes;
        _lsdTimerMinutes = settings.LsdTimerMinutes;
        _timerCompletionSoundEnabled = settings.TimerCompletionSoundEnabled;
        _visibilityHotkeyText = FormatHotkey(
            settings.HudVisibilityHotkey,
            HotkeySetting.DefaultVisibilityToggle);
        _windowsAutoStart = settings.WindowsAutoStart;
        _visibilityModes = CreateVisibilityModes();
        _sessionHostOptions = CreateSessionHostOptions();
        _selectedChatLayoutMode = settings.ChatLayoutMode;
        _chatShowTime = settings.ChatShowTime;
        _selectedChatFontPreset = settings.ChatFontPreset;
        _chatFontSizePoints = settings.ChatFontSizePoints;
        _chatNicknameOutlineEnabled = settings.ChatNicknameOutlineEnabled;
        _chatMessageOutlineEnabled = settings.ChatMessageOutlineEnabled;
        _chatNicknameOutlineThickness = settings.ChatNicknameOutlineThickness;
        _chatMessageOutlineThickness = settings.ChatMessageOutlineThickness;
        _chatMaxLines = settings.ChatMaxLines;
        _chatLineHeightMultiplier = settings.ChatLineHeightMultiplier;
        _chatMessageSpacing = settings.ChatMessageSpacing;
        _selectedRoleIconPosition = settings.ChatRoleIconPosition;
        _chatReactionSize = settings.ChatReactionSize;
        _chatShowImages = settings.ChatShowImages;
        _selectedChatImageMode = settings.ChatImageMode;
        _selectedChatImageSizeMode = settings.ChatImageSizeMode;
        _chatCustomEmojiEnabled = settings.ChatCustomEmojiEnabled;
        _chatStickerEnabled = settings.ChatStickerEnabled;
        _hidePreviewSourceUrl = settings.HidePreviewSourceUrl;
        _salesTrackingEnabled = settings.SalesTrackingEnabled;
        _salesShowCurrentSeller = settings.SalesShowCurrentSeller;
        _salesShowWaitingCount = settings.SalesShowWaitingCount;
        _salesShowProduct = settings.SalesShowProduct;
        _salesShowNextWaitingUser = settings.SalesShowNextWaitingUser;
        _salesQueueDetailMaxHeight = settings.SalesQueueDetailMaxHeight;
        _salesTurnSoundEnabled = settings.SalesTurnSoundEnabled;
        _salesTurnSoundVolume = settings.SalesTurnSoundVolume;
        _notifySalesNext = settings.NotifySalesNext;
        _notifySalesCurrent = settings.NotifySalesCurrent;
        _chatLayoutModes = CreateChatLayoutModes();
        _chatFontPresets = CreateChatFontPresets();
        _chatImageModes = CreateChatImageModes();
        _chatImageSizeModes = CreateChatImageSizeModes();
        _chatStylePresets = CreateChatStylePresets();
        _chatMaxLineOptions = CreateChatMaxLineOptions();
        _roleIconPositions = CreateRoleIconPositions();
        _colorThemes = CreateColorThemes();
        HideToTrayCommand = new RelayCommand(hideWindow);
        ApplyHotkeysCommand = new RelayCommand(ApplyHotkeys);
        ResetHotkeysCommand = new RelayCommand(ResetHotkeys);
        ResetHudPlacementCommand = new RelayCommand(resetHudPlacement);
        ResetHudPositionCommand = new RelayCommand(resetHudPosition ?? resetHudPlacement);
        ResetHudSizeCommand = new RelayCommand(resetHudSize ?? resetHudPlacement);
        CenterHudOnCurrentDisplayCommand = new RelayCommand(
            centerHudOnCurrentDisplay ?? resetHudPlacement);
        ApplyChatPresetCommand = new RelayCommand(ApplyChatPreset);
        OpenProductMappingManagerCommand = new RelayCommand(openProductMappingManager ?? (() => { }));
        ExportProductMappingsCommand = new RelayCommand(exportProductMappings ?? (() => { }));
        OpenSalesPreviewCommand = new RelayCommand(openSalesPreview ?? (() => { }));
        ManualSalesResyncCommand = new RelayCommand(
            RequestManualSalesResync,
            () => SalesTrackingEnabled);
        TestSalesTurnSoundCommand = new RelayCommand(
            _testSalesTurnSound,
            () => SalesTurnSoundEnabled && SalesTurnSoundVolume > 0);
        ClearMediaCacheCommand = new RelayCommand(ClearMediaCache);
        ResetAllSettingsCommand = new RelayCommand(resetAllSettings ?? (() => { }));
        OpenLogFolderCommand = new RelayCommand(openLogFolder ?? (() => { }));
        OpenLatestLogCommand = new RelayCommand(openLatestLog ?? (() => { }));
        ResetProductOverridesCommand = new RelayCommand(ResetProductOverrides);
        ShowCreditsCommand = new RelayCommand(showCredits ?? (() => { }));
        OpenLicenseNoticesCommand = new RelayCommand(openLicenseNotices ?? (() => { }));
        RerunOnboardingCommand = new RelayCommand(rerunOnboarding ?? (() => { }));
        CreateDiagnosticBundleCommand = new AsyncRelayCommand(CreateDiagnosticBundleAsync);
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshDiagnostics();
        RefreshChatPresetState();
        RefreshThemeSelection();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ILocalizationService Localization { get; }

    public IReadOnlyList<LanguageOption> Languages { get; } =
        new[]
        {
            new LanguageOption(SupportedLocales.English, "English"),
            new LanguageOption(SupportedLocales.Korean, "한국어"),
            new LanguageOption(SupportedLocales.Japanese, "日本語"),
        };

    public ICommand HideToTrayCommand { get; }

    public ICommand ApplyHotkeysCommand { get; }

    public ICommand ResetHotkeysCommand { get; }

    public ICommand ResetHudPlacementCommand { get; }

    public ICommand ResetHudPositionCommand { get; }

    public ICommand ResetHudSizeCommand { get; }

    public ICommand CenterHudOnCurrentDisplayCommand { get; }

    public ICommand ApplyChatPresetCommand { get; }

    public ICommand OpenProductMappingManagerCommand { get; }

    public ICommand ExportProductMappingsCommand { get; }

    public ICommand OpenSalesPreviewCommand { get; }

    public RelayCommand ManualSalesResyncCommand { get; }

    public RelayCommand TestSalesTurnSoundCommand { get; }

    public ICommand ClearMediaCacheCommand { get; }

    public ICommand ResetAllSettingsCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand OpenLatestLogCommand { get; }

    public ICommand ResetProductOverridesCommand { get; }

    public ICommand ShowCreditsCommand { get; }

    public ICommand OpenLicenseNoticesCommand { get; }

    public ICommand RerunOnboardingCommand { get; }

    public ICommand CreateDiagnosticBundleCommand { get; }

    public RemoteChatSettingsViewModel? RemoteChatSettings { get; }

    public SalesHistoryViewModel? SalesHistory { get; }

    public string AppVersionText => ResolveAppVersionText();

    public string ProductCatalogSummaryText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsDeveloperCatalogSummary"],
        _productCatalogSnapshot.BuiltInMappingCount,
        _productCatalogSnapshot.BuiltInGroupCount);

    private static string ResolveAppVersionText()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        return assembly?.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public string ProductOverrideCountText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsDeveloperOverrideCount"],
        _productCatalogSnapshot.OverrideCount);

    public string ProductCatalogLoadStatusText => Localization[
        _productCatalogSnapshot.BuiltInLoaded
            ? "SettingsDeveloperCatalogReady"
            : "SettingsDeveloperCatalogUnavailable"];

    public bool HasProductOverrides => _productCatalogSnapshot.OverrideCount > 0;

    public void ReloadFromCurrentSettings()
    {
        var settings = _settingsStore.Current;
        _selectedSettingsCategory = settings.LastSettingsCategory;
        _selectedLanguage = settings.Language;
        _selectedColorTheme = settings.ColorTheme;
        _hudSurfaceOpacity = settings.HudSurfaceOpacity;
        _hudChromeOpacity = settings.HudChromeOpacity;
        _chatSurfaceOpacity = settings.ChatSurfaceOpacity;
        _salesSurfaceOpacity = settings.SalesSurfaceOpacity;
        _queueDetailSurfaceOpacity = settings.QueueDetailSurfaceOpacity;
        _minimalHudMode = settings.MinimalHudMode;
        _showGtaSession = settings.ShowGtaSession;
        _selectedSessionHost = settings.SelectedSessionHost;
        _hudModifierDragEnabled = settings.HudModifierDragEnabled;
        _selectedVisibilityMode = settings.HudVisibilityMode;
        _lockHotkeyText = FormatHotkey(settings.HudLockHotkey, HotkeySetting.DefaultLockToggle);
        _previousChannelHotkeyText = FormatOptionalHotkey(settings.PreviousMainChannelHotkey);
        _nextChannelHotkeyText = FormatOptionalHotkey(settings.NextMainChannelHotkey);
        _generalTimerHotkeyText = FormatOptionalHotkey(settings.GeneralTimerHotkey);
        _bunkerTimerHotkeyText = FormatOptionalHotkey(settings.BunkerTimerHotkey);
        _lsdTimerHotkeyText = FormatOptionalHotkey(settings.LsdTimerHotkey);
        _generalTimerMinutes = settings.GeneralTimerMinutes;
        _bunkerTimerMinutes = settings.BunkerTimerMinutes;
        _lsdTimerMinutes = settings.LsdTimerMinutes;
        _timerCompletionSoundEnabled = settings.TimerCompletionSoundEnabled;
        _visibilityHotkeyText = FormatHotkey(
            settings.HudVisibilityHotkey,
            HotkeySetting.DefaultVisibilityToggle);
        _windowsAutoStart = settings.WindowsAutoStart;
        _salesTrackingEnabled = settings.SalesTrackingEnabled;
        _salesShowCurrentSeller = settings.SalesShowCurrentSeller;
        _salesShowWaitingCount = settings.SalesShowWaitingCount;
        _salesShowProduct = settings.SalesShowProduct;
        _salesShowNextWaitingUser = settings.SalesShowNextWaitingUser;
        _salesQueueDetailMaxHeight = settings.SalesQueueDetailMaxHeight;
        _salesTurnSoundEnabled = settings.SalesTurnSoundEnabled;
        _salesTurnSoundVolume = settings.SalesTurnSoundVolume;
        _notifySalesNext = settings.NotifySalesNext;
        _notifySalesCurrent = settings.NotifySalesCurrent;
        LoadChatSettings(settings);
        ColorThemes = CreateColorThemes();
        RefreshThemeSelection();
        Localization.SetLanguage(settings.Language);
        RefreshDiagnostics();
        OnPropertyChanged(nameof(WindowsAutoStart));
        ManualSalesResyncCommand.RaiseCanExecuteChanged();
        TestSalesTurnSoundCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    public IReadOnlyList<SettingsCategoryOption> SettingsCategories
    {
        get => _settingsCategories;
        private set { _settingsCategories = value; OnPropertyChanged(); }
    }

    public SettingsCategory SelectedSettingsCategory
    {
        get => _selectedSettingsCategory;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : SettingsCategory.General;
            if (_selectedSettingsCategory == normalized)
            {
                return;
            }

            _selectedSettingsCategory = normalized;
            _settingsStore.Update(settings => settings with { LastSettingsCategory = normalized });
            OnPropertyChanged();
        }
    }

    public void OpenCategory(SettingsCategory category) => SelectedSettingsCategory = category;

    public double GetCategoryScrollPosition(SettingsCategory category) =>
        _settingsStore.Current.SettingsCategoryScrollPositions.TryGetValue(
            category.ToString(),
            out var offset)
                ? Math.Max(0, offset)
                : 0;

    public void SaveCategoryScrollPosition(SettingsCategory category, double offset)
    {
        var positions = new Dictionary<string, double>(
            _settingsStore.Current.SettingsCategoryScrollPositions,
            StringComparer.OrdinalIgnoreCase)
        {
            [category.ToString()] = Math.Max(0, double.IsFinite(offset) ? offset : 0),
        };
        _settingsStore.Update(settings => settings with
        {
            SettingsCategoryScrollPositions = positions,
        });
    }

    public IReadOnlyList<HudVisibilityModeOption> VisibilityModes
    {
        get => _visibilityModes;
        private set
        {
            _visibilityModes = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ColorThemeOption> ColorThemes
    {
        get => _colorThemes;
        private set { _colorThemes = value; OnPropertyChanged(); }
    }

    public ColorThemeId SelectedColorTheme
    {
        get => _selectedColorTheme;
        set
        {
            var normalized = Enum.IsDefined(value)
                ? value
                : ColorThemeCatalog.DefaultTheme;
            if (_selectedColorTheme == normalized)
            {
                return;
            }

            _selectedColorTheme = normalized;
            if (!_settingsStore.Update(settings => settings with { ColorTheme = normalized }))
            {
                _logger.Warning(
                    "THEME",
                    "The selected color theme changed for this session but could not be persisted.");
            }

            _applyColorTheme(normalized);
            RefreshThemeSelection();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ChatLayoutModeOption> ChatLayoutModes
    {
        get => _chatLayoutModes;
        private set { _chatLayoutModes = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ChatFontPresetOption> ChatFontPresets
    {
        get => _chatFontPresets;
        private set { _chatFontPresets = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ChatImageModeOption> ChatImageModes
    {
        get => _chatImageModes;
        private set { _chatImageModes = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ChatStylePresetOption> ChatStylePresets
    {
        get => _chatStylePresets;
        private set { _chatStylePresets = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<ChatLineLimitOption> ChatMaxLineOptions
    {
        get => _chatMaxLineOptions;
        private set { _chatMaxLineOptions = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<RoleIconPositionOption> RoleIconPositions
    {
        get => _roleIconPositions;
        private set { _roleIconPositions = value; OnPropertyChanged(); }
    }

    public ChatStylePreset SelectedChatStylePreset
    {
        get => _selectedChatStylePreset;
        set
        {
            if (_selectedChatStylePreset == value)
            {
                return;
            }

            _selectedChatStylePreset = value;
            ChatPresetStatusMessage = string.Empty;
            OnPropertyChanged();
        }
    }

    public string ChatPresetStatusMessage
    {
        get => _chatPresetStatusMessage;
        private set
        {
            _chatPresetStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public string SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            var normalized = SupportedLocales.Korean;
            if (string.Equals(_selectedLanguage, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _selectedLanguage = normalized;
            Localization.SetLanguage(normalized);

            var saved = _settingsStore.Update(current => current with
            {
                Language = normalized,
            });

            if (!saved)
            {
                _logger.Warning(
                    "SETTINGS",
                    "The language changed for this session but could not be persisted.");
            }

            OnPropertyChanged();
        }
    }

    public double HudSurfaceOpacity
    {
        get => _hudSurfaceOpacity;
        set
        {
            var normalized = HudSettingsDefaults.NormalizeSurfaceOpacity(value);
            if (Math.Abs(_hudSurfaceOpacity - normalized) < 0.001)
            {
                return;
            }

            _hudSurfaceOpacity = normalized;
            SaveAndApply(settings => settings with { HudSurfaceOpacity = normalized });
            OnPropertyChanged();
        }
    }

    public double HudChromeOpacity
    {
        get => _hudChromeOpacity;
        set
        {
            var normalized = ChatSettings.NormalizeSurfaceOpacity(value);
            SetAndSave(ref _hudChromeOpacity, normalized, settings => settings with
            {
                HudChromeOpacity = normalized,
            });
        }
    }

    public double ChatSurfaceOpacity
    {
        get => _chatSurfaceOpacity;
        set
        {
            var normalized = ChatSettings.NormalizeSurfaceOpacity(value);
            SetAndSave(ref _chatSurfaceOpacity, normalized, settings => settings with
            {
                ChatSurfaceOpacity = normalized,
            });
        }
    }

    public double SalesSurfaceOpacity
    {
        get => _salesSurfaceOpacity;
        set
        {
            var normalized = ChatSettings.NormalizeSurfaceOpacity(value);
            SetAndSave(ref _salesSurfaceOpacity, normalized, settings => settings with
            {
                SalesSurfaceOpacity = normalized,
            });
        }
    }

    public double QueueDetailSurfaceOpacity
    {
        get => _queueDetailSurfaceOpacity;
        set
        {
            var normalized = ChatSettings.NormalizeSurfaceOpacity(value);
            SetAndSave(ref _queueDetailSurfaceOpacity, normalized, settings => settings with
            {
                QueueDetailSurfaceOpacity = normalized,
            });
        }
    }

    public bool MinimalHudMode
    {
        get => _minimalHudMode;
        set => SetAndSave(ref _minimalHudMode, value, settings => settings with
        {
            MinimalHudMode = value,
        });
    }

    public bool ShowGtaSession
    {
        get => _showGtaSession;
        set => SetAndSave(ref _showGtaSession, value, settings => settings with
        {
            ShowGtaSession = value,
        });
    }

    public IReadOnlyList<SessionHostOption> SessionHostOptions
    {
        get => _sessionHostOptions;
        private set
        {
            _sessionHostOptions = value;
            OnPropertyChanged();
        }
    }

    public SessionHostSelection SelectedSessionHost
    {
        get => _selectedSessionHost;
        set
        {
            var normalized = Enum.IsDefined(value)
                ? value
                : SessionHostSelection.Host1;
            SetAndSave(ref _selectedSessionHost, normalized, settings => settings with
            {
                SelectedSessionHost = normalized,
            });
        }
    }

    public bool HudModifierDragEnabled
    {
        get => _hudModifierDragEnabled;
        set
        {
            if (_hudModifierDragEnabled == value)
            {
                return;
            }

            _hudModifierDragEnabled = value;
            SaveAndApply(settings => settings with { HudModifierDragEnabled = value });
            OnPropertyChanged();
        }
    }

    public HudVisibilityMode SelectedVisibilityMode
    {
        get => _selectedVisibilityMode;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : HudVisibilityMode.Always;
            if (_selectedVisibilityMode == normalized)
            {
                return;
            }

            _selectedVisibilityMode = normalized;
            SaveAndApply(settings => settings with { HudVisibilityMode = normalized });
            OnPropertyChanged();
        }
    }

    public string PreviousChannelHotkeyText
    {
        get => _previousChannelHotkeyText;
        set { _previousChannelHotkeyText = value; OnPropertyChanged(); }
    }

    public string NextChannelHotkeyText
    {
        get => _nextChannelHotkeyText;
        set { _nextChannelHotkeyText = value; OnPropertyChanged(); }
    }

    public string GeneralTimerHotkeyText
    {
        get => _generalTimerHotkeyText;
        set { _generalTimerHotkeyText = value; OnPropertyChanged(); }
    }

    public string BunkerTimerHotkeyText
    {
        get => _bunkerTimerHotkeyText;
        set { _bunkerTimerHotkeyText = value; OnPropertyChanged(); }
    }

    public string LsdTimerHotkeyText
    {
        get => _lsdTimerHotkeyText;
        set { _lsdTimerHotkeyText = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<TimerPresetOption> GeneralTimerPresets =>
        GtaoTimerPresets.General.Select(value => new TimerPresetOption(
            value,
            string.Format(Localization["TimerMinutesFormat"], value))).ToArray();

    public IReadOnlyList<TimerPresetOption> BunkerTimerPresets =>
    [
        new(40, Localization["TimerBunkerMansion"]),
        new(130, Localization["TimerBunkerNormal"]),
    ];

    public IReadOnlyList<TimerPresetOption> LsdTimerPresets =>
        GtaoTimerPresets.Lsd.Select(value => new TimerPresetOption(
            value,
            string.Format(Localization["TimerMinutesFormat"], value))).ToArray();

    public int GeneralTimerMinutes
    {
        get => _generalTimerMinutes;
        set
        {
            var normalized = GtaoTimerPresets.Normalize(GtaoTimerSlot.General, value);
            SetAndSave(ref _generalTimerMinutes, normalized,
                settings => settings with { GeneralTimerMinutes = normalized });
        }
    }

    public int BunkerTimerMinutes
    {
        get => _bunkerTimerMinutes;
        set
        {
            var normalized = GtaoTimerPresets.Normalize(GtaoTimerSlot.Bunker, value);
            SetAndSave(ref _bunkerTimerMinutes, normalized,
                settings => settings with { BunkerTimerMinutes = normalized });
        }
    }

    public int LsdTimerMinutes
    {
        get => _lsdTimerMinutes;
        set
        {
            var normalized = GtaoTimerPresets.Normalize(GtaoTimerSlot.Lsd, value);
            SetAndSave(ref _lsdTimerMinutes, normalized,
                settings => settings with { LsdTimerMinutes = normalized });
        }
    }

    public bool TimerCompletionSoundEnabled
    {
        get => _timerCompletionSoundEnabled;
        set => SetAndSave(
            ref _timerCompletionSoundEnabled,
            value,
            settings => settings with { TimerCompletionSoundEnabled = value });
    }

    private static string FormatOptionalHotkey(HotkeySetting? setting) =>
        setting is not null && HotkeyGesture.TryParse(setting, out var gesture) ? gesture.ToString() : string.Empty;

    public string LockHotkeyText
    {
        get => _lockHotkeyText;
        set
        {
            _lockHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string VisibilityHotkeyText
    {
        get => _visibilityHotkeyText;
        set
        {
            _visibilityHotkeyText = value;
            OnPropertyChanged();
        }
    }

    public string HotkeyValidationMessage
    {
        get => _hotkeyValidationMessage;
        private set
        {
            _hotkeyValidationMessage = value;
            OnPropertyChanged();
        }
    }

    public bool WindowsAutoStart
    {
        get => _windowsAutoStart;
        set
        {
            if (_windowsAutoStart == value)
            {
                return;
            }

            if (!_applyWindowsAutoStart(value))
            {
                _logger.Warning("STARTUP", "Windows auto-start setting could not be applied.");
                OnPropertyChanged();
                return;
            }

            _windowsAutoStart = value;
            _settingsStore.Update(settings => settings with { WindowsAutoStart = value });
            OnPropertyChanged();
        }
    }

    public ChatLayoutMode SelectedChatLayoutMode
    {
        get => _selectedChatLayoutMode;
        set => SetAndSave(ref _selectedChatLayoutMode, value, settings => settings with { ChatLayoutMode = value });
    }

    public bool ChatShowTime
    {
        get => _chatShowTime;
        set => SetAndSave(ref _chatShowTime, value, settings => settings with { ChatShowTime = value });
    }

    public ChatFontPreset SelectedChatFontPreset
    {
        get => _selectedChatFontPreset;
        set
        {
            var previous = _selectedChatFontPreset;
            SetAndSave(
                ref _selectedChatFontPreset,
                value,
                settings => settings with { ChatFontPreset = value });
            if (previous != _selectedChatFontPreset)
            {
                OnPropertyChanged(nameof(SelectedChatFontPreviewFamily));
                OnPropertyChanged(nameof(SelectedChatFontPreviewWeight));
                OnPropertyChanged(nameof(SelectedChatTypography));
                OnPropertyChanged(nameof(SelectedChatFontNotice));
            }
        }
    }

    public ResolvedChatTypography SelectedChatTypography =>
        _typographyResolver.Resolve(SelectedChatFontPreset);

    public System.Windows.Media.FontFamily SelectedChatFontPreviewFamily =>
        SelectedChatTypography.Message.FontFamily;

    public System.Windows.FontWeight SelectedChatFontPreviewWeight =>
        SelectedChatTypography.Message.FontWeight;

    public string SelectedChatFontNotice => BuildResolutionStatus(SelectedChatTypography);

    public double ChatFontSizePoints
    {
        get => _chatFontSizePoints;
        set
        {
            var normalized = ChatSettings.NormalizeFontSize(value);
            SetAndSave(ref _chatFontSizePoints, normalized, settings => settings with { ChatFontSizePoints = normalized });
        }
    }

    public bool ChatNicknameOutlineEnabled
    {
        get => _chatNicknameOutlineEnabled;
        set => SetAndSave(ref _chatNicknameOutlineEnabled, value, settings => settings with { ChatNicknameOutlineEnabled = value });
    }

    public bool ChatMessageOutlineEnabled
    {
        get => _chatMessageOutlineEnabled;
        set => SetAndSave(ref _chatMessageOutlineEnabled, value, settings => settings with { ChatMessageOutlineEnabled = value });
    }

    public double ChatNicknameOutlineThickness
    {
        get => _chatNicknameOutlineThickness;
        set
        {
            var normalized = ChatSettings.NormalizeOutlineThickness(value);
            SetAndSave(
                ref _chatNicknameOutlineThickness,
                normalized,
                settings => settings with { ChatNicknameOutlineThickness = normalized });
        }
    }

    public double ChatMessageOutlineThickness
    {
        get => _chatMessageOutlineThickness;
        set
        {
            var normalized = ChatSettings.NormalizeOutlineThickness(value);
            SetAndSave(
                ref _chatMessageOutlineThickness,
                normalized,
                settings => settings with { ChatMessageOutlineThickness = normalized });
        }
    }

    public double ChatOutlineThickness
    {
        get => ChatMessageOutlineThickness;
        set
        {
            ChatNicknameOutlineThickness = value;
            ChatMessageOutlineThickness = value;
        }
    }

    public int ChatMaxLines
    {
        get => _chatMaxLines;
        set
        {
            var normalized = ChatSettings.NormalizeMaxLines(value);
            SetAndSave(ref _chatMaxLines, normalized, settings => settings with { ChatMaxLines = normalized });
        }
    }

    public double ChatLineHeightMultiplier
    {
        get => _chatLineHeightMultiplier;
        set
        {
            var normalized = ChatSettings.NormalizeLineHeightMultiplier(value);
            SetAndSave(
                ref _chatLineHeightMultiplier,
                normalized,
                settings => settings with { ChatLineHeightMultiplier = normalized });
        }
    }

    public double ChatMessageSpacing
    {
        get => _chatMessageSpacing;
        set
        {
            var normalized = ChatSettings.NormalizeMessageSpacing(value);
            SetAndSave(
                ref _chatMessageSpacing,
                normalized,
                settings => settings with { ChatMessageSpacing = normalized });
        }
    }

    public RoleIconPosition SelectedRoleIconPosition
    {
        get => _selectedRoleIconPosition;
        set
        {
            var normalized = Enum.IsDefined(value) ? value : RoleIconPosition.Left;
            SetAndSave(
                ref _selectedRoleIconPosition,
                normalized,
                settings => settings with { ChatRoleIconPosition = normalized });
        }
    }

    public double ChatReactionSize
    {
        get => _chatReactionSize;
        set
        {
            var normalized = ChatSettings.NormalizeReactionSize(value);
            SetAndSave(
                ref _chatReactionSize,
                normalized,
                settings => settings with { ChatReactionSize = normalized });
        }
    }

    public bool ChatShowImages
    {
        get => _chatShowImages;
        set => SetAndSave(ref _chatShowImages, value, settings => settings with { ChatShowImages = value });
    }

    public ChatImageMode SelectedChatImageMode
    {
        get => _selectedChatImageMode;
        set => SetAndSave(ref _selectedChatImageMode, value, settings => settings with { ChatImageMode = value });
    }

    public IReadOnlyList<ChatImageSizeModeOption> ChatImageSizeModes
    {
        get => _chatImageSizeModes;
        private set { _chatImageSizeModes = value; OnPropertyChanged(); }
    }

    public ChatImageSizeMode SelectedChatImageSizeMode
    {
        get => _selectedChatImageSizeMode;
        set => SetAndSave(
            ref _selectedChatImageSizeMode,
            value,
            settings => settings with { ChatImageSizeMode = value });
    }

    public bool ChatCustomEmojiEnabled
    {
        get => _chatCustomEmojiEnabled;
        set => SetAndSave(ref _chatCustomEmojiEnabled, value, settings => settings with
        {
            ChatCustomEmojiEnabled = value,
        });
    }

    public bool ChatStickerEnabled
    {
        get => _chatStickerEnabled;
        set => SetAndSave(ref _chatStickerEnabled, value, settings => settings with
        {
            ChatStickerEnabled = value,
        });
    }

    public bool HidePreviewSourceUrl
    {
        get => _hidePreviewSourceUrl;
        set => SetAndSave(ref _hidePreviewSourceUrl, value, settings => settings with
        {
            HidePreviewSourceUrl = value,
        });
    }

    public bool SalesTrackingEnabled
    {
        get => _salesTrackingEnabled;
        set
        {
            var previous = _salesTrackingEnabled;
            SetAndSave(
                ref _salesTrackingEnabled,
                value,
                settings => settings with { SalesTrackingEnabled = value });
            if (previous != _salesTrackingEnabled)
            {
                OnPropertyChanged(nameof(SalesDisplayOptionsEnabled));
                ManualSalesResyncCommand.RaiseCanExecuteChanged();
                ManualSalesResyncStatusMessage = value
                    ? string.Empty
                    : Localization["SettingsManualResyncTrackingDisabled"];
                RefreshDiagnostics();
            }
        }
    }

    public bool SalesDisplayOptionsEnabled => SalesTrackingEnabled;

    public bool SalesShowCurrentSeller
    {
        get => _salesShowCurrentSeller;
        set => SetAndSave(
            ref _salesShowCurrentSeller,
            value,
            settings => settings with { SalesShowCurrentSeller = value });
    }

    public bool SalesShowWaitingCount
    {
        get => _salesShowWaitingCount;
        set => SetAndSave(
            ref _salesShowWaitingCount,
            value,
            settings => settings with { SalesShowWaitingCount = value });
    }

    public bool SalesShowProduct
    {
        get => _salesShowProduct;
        set => SetAndSave(
            ref _salesShowProduct,
            value,
            settings => settings with { SalesShowProduct = value });
    }

    public bool SalesShowNextWaitingUser
    {
        get => _salesShowNextWaitingUser;
        set => SetAndSave(
            ref _salesShowNextWaitingUser,
            value,
            settings => settings with { SalesShowNextWaitingUser = value });
    }

    public double SalesQueueDetailMaxHeight
    {
        get => _salesQueueDetailMaxHeight;
        set
        {
            var normalized = ChatSettings.NormalizeQueueDetailMaxHeight(value);
            SetAndSave(
                ref _salesQueueDetailMaxHeight,
                normalized,
                settings => settings with { SalesQueueDetailMaxHeight = normalized });
        }
    }

    public bool SalesTurnSoundEnabled
    {
        get => _salesTurnSoundEnabled;
        set
        {
            var previous = _salesTurnSoundEnabled;
            SetAndSave(ref _salesTurnSoundEnabled, value, settings => settings with
            {
                SalesTurnSoundEnabled = value,
            });
            if (previous != _salesTurnSoundEnabled)
            {
                TestSalesTurnSoundCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SalesTurnSoundOptionsEnabled));
            }
        }
    }

    public bool SalesTurnSoundOptionsEnabled => SalesTurnSoundEnabled;

    public double SalesTurnSoundVolume
    {
        get => _salesTurnSoundVolume;
        set
        {
            var normalized = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 50;
            var previous = _salesTurnSoundVolume;
            SetAndSave(ref _salesTurnSoundVolume, normalized, settings => settings with
            {
                SalesTurnSoundVolume = normalized,
            });
            if (Math.Abs(previous - _salesTurnSoundVolume) >= 0.001)
            {
                TestSalesTurnSoundCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool NotifySalesNext
    {
        get => _notifySalesNext;
        set => SetAndSave(ref _notifySalesNext, value, settings => settings with
        {
            NotifySalesNext = value,
        });
    }

    public bool NotifySalesCurrent
    {
        get => _notifySalesCurrent;
        set => SetAndSave(ref _notifySalesCurrent, value, settings => settings with
        {
            NotifySalesCurrent = value,
        });
    }

    public string SalesHealthText => _salesHealthSnapshot.State switch
    {
        SalesFeatureHealthState.Disabled => Localization["SalesHealthDisabled"],
        SalesFeatureHealthState.Connecting => Localization["SalesHealthConnecting"],
        SalesFeatureHealthState.Resyncing => Localization["SalesHealthResyncing"],
        SalesFeatureHealthState.Live => Localization["SalesHealthLiveAccessible"],
        SalesFeatureHealthState.Paused => Localization["SettingsSalesHealthPaused"],
        SalesFeatureHealthState.Degraded => Localization["SalesHealthDegraded"],
        SalesFeatureHealthState.Disconnected => Localization["SalesHealthDisconnected"],
        _ => Localization[_salesHealthSnapshot.Reason == SalesFeatureHealthReason.RemoteSalesAccessRevoked
            ? "SalesHealthRemoteAccessRevoked"
            : "SalesHealthRemoteError"],
    };

    public string SalesCoverageText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsSalesCoverageFormat"],
        _salesHealthSnapshot.ObservedMessageCount,
        _salesHealthSnapshot.TargetMessageCount,
        Localization[_salesHealthSnapshot.Coverage switch
        {
            SalesCoverageState.Complete => "SettingsSalesCoverageComplete",
            SalesCoverageState.Partial => "SettingsSalesCoveragePartial",
            _ => "SettingsSalesCoverageNone",
        }]);

    public string SalesLastFullSyncText => _salesHealthSnapshot.LastCompleteResyncAt.HasValue
        ? string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            Localization["SettingsSalesLastSyncFormat"],
            _salesHealthSnapshot.LastCompleteResyncAt.Value.ToLocalTime().ToString("g"))
        : Localization["SettingsSalesLastSyncNever"];

    public string SalesEffectiveSourceText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsSalesEffectiveSourceFormat"],
        _salesHealthSnapshot.EffectiveSource);

    public string SalesRemoteStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsSalesRemoteStateFormat"],
        _salesHealthSnapshot.RemotePhase);

    public string SalesMergedStatusText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        Localization["SettingsSalesMergedStateFormat"],
        _salesHealthSnapshot.State);

    public string ManualSalesResyncStatusMessage
    {
        get => _manualSalesResyncStatusMessage;
        private set
        {
            _manualSalesResyncStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public string MediaCacheStatusMessage
    {
        get => _mediaCacheStatusMessage;
        private set
        {
            _mediaCacheStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public string DiagnosticExportStatusMessage
    {
        get => _diagnosticExportStatusMessage;
        private set
        {
            _diagnosticExportStatusMessage = value;
            OnPropertyChanged();
        }
    }

    public void RefreshDiagnostics()
    {
        _salesHealthSnapshot = _getSalesHealthSnapshot();
        _productCatalogSnapshot = _getProductCatalogSnapshot();
        OnPropertyChanged(nameof(SalesHealthText));
        OnPropertyChanged(nameof(SalesCoverageText));
        OnPropertyChanged(nameof(SalesLastFullSyncText));
        OnPropertyChanged(nameof(SalesEffectiveSourceText));
        OnPropertyChanged(nameof(SalesRemoteStatusText));
        OnPropertyChanged(nameof(SalesMergedStatusText));
        OnPropertyChanged(nameof(ProductCatalogSummaryText));
        OnPropertyChanged(nameof(ProductOverrideCountText));
        OnPropertyChanged(nameof(ProductCatalogLoadStatusText));
        OnPropertyChanged(nameof(BunkerTimerPresets));
        OnPropertyChanged(nameof(GeneralTimerPresets));
        OnPropertyChanged(nameof(LsdTimerPresets));
        OnPropertyChanged(nameof(HasProductOverrides));
    }

    private async Task CreateDiagnosticBundleAsync()
    {
        DiagnosticExportStatusMessage = Localization["SettingsDiagnosticCreating"];
        DiagnosticExportStatusMessage = await _createDiagnosticBundle();
    }

    private void ResetProductOverrides()
    {
        if (_resetProductOverrides())
        {
            RefreshDiagnostics();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RemoteChatSettings?.Dispose();
        Localization.LanguageChanged -= OnLanguageChanged;
        SalesHistory?.Dispose();
    }

    private void ApplyHotkeys()
    {
        if (!HotkeyGesture.TryParseDisplayText(LockHotkeyText, out var lockGesture) ||
            !HotkeyGesture.TryParseDisplayText(VisibilityHotkeyText, out var visibilityGesture) ||
            !TryOptionalHotkey(PreviousChannelHotkeyText, out var previousChannel) ||
            !TryOptionalHotkey(NextChannelHotkeyText, out var nextChannel) ||
            !TryOptionalHotkey(GeneralTimerHotkeyText, out var generalTimer) ||
            !TryOptionalHotkey(BunkerTimerHotkeyText, out var bunkerTimer) ||
            !TryOptionalHotkey(LsdTimerHotkeyText, out var lsdTimer))
        { HotkeyValidationMessage = Localization["SettingsHotkeyInvalid"]; return; }

        var assigned = new HotkeyGesture?[]
            { lockGesture, visibilityGesture, previousChannel, nextChannel, generalTimer, bunkerTimer, lsdTimer }
            .Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        if (assigned.Distinct().Count() != assigned.Length)
        { HotkeyValidationMessage = Localization["SettingsHotkeyDuplicate"]; return; }

        ApplyAndPersistHotkeys(_settingsStore.Current with
        {
            HudLockHotkey = lockGesture.ToSetting(),
            HudVisibilityHotkey = visibilityGesture.ToSetting(),
            PreviousMainChannelHotkey = previousChannel?.ToSetting() ?? new HotkeySetting { Key = "" },
            NextMainChannelHotkey = nextChannel?.ToSetting() ?? new HotkeySetting { Key = "" },
            GeneralTimerHotkey = generalTimer?.ToSetting() ?? new HotkeySetting { Key = "" },
            BunkerTimerHotkey = bunkerTimer?.ToSetting() ?? new HotkeySetting { Key = "" },
            LsdTimerHotkey = lsdTimer?.ToSetting() ?? new HotkeySetting { Key = "" },
            HotkeySettingsVersion = AppSettings.CurrentHotkeySettingsVersion,
            HotkeysCustomized = true,
        });
    }

    private static bool TryOptionalHotkey(string text, out HotkeyGesture? gesture)
    {
        gesture = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!HotkeyGesture.TryParseDisplayText(text, out var parsed)) return false;
        gesture = parsed;
        return true;
    }

    private void ResetHotkeys() => ApplyAndPersistHotkeys(_settingsStore.Current with
    {
        HudLockHotkey = HotkeySetting.DefaultLockToggle,
        HudVisibilityHotkey = HotkeySetting.DefaultVisibilityToggle,
        PreviousMainChannelHotkey = new HotkeySetting { Key = "" },
        NextMainChannelHotkey = new HotkeySetting { Key = "" },
        GeneralTimerHotkey = new HotkeySetting { Key = "" },
        BunkerTimerHotkey = new HotkeySetting { Key = "" },
        LsdTimerHotkey = new HotkeySetting { Key = "" },
        HotkeySettingsVersion = AppSettings.CurrentHotkeySettingsVersion,
        HotkeysCustomized = false,
    });

    private void ApplyAndPersistHotkeys(AppSettings desired)
    {
        var previous = _settingsStore.Current;
        if (!_applyAllHotkeys(desired))
        { RefreshHotkeyText(previous); HotkeyValidationMessage = Localization["SettingsHotkeyRegistrationFailed"]; return; }
        if (!_settingsStore.Update(settings => settings with
        {
            HudLockHotkey = desired.HudLockHotkey,
            HudVisibilityHotkey = desired.HudVisibilityHotkey,
            PreviousMainChannelHotkey = desired.PreviousMainChannelHotkey,
            NextMainChannelHotkey = desired.NextMainChannelHotkey,
            GeneralTimerHotkey = desired.GeneralTimerHotkey,
            BunkerTimerHotkey = desired.BunkerTimerHotkey,
            LsdTimerHotkey = desired.LsdTimerHotkey,
            HotkeySettingsVersion = desired.HotkeySettingsVersion,
            HotkeysCustomized = desired.HotkeysCustomized,
        }))
        {
            _applyAllHotkeys(previous);
            RefreshHotkeyText(previous);
            HotkeyValidationMessage = Localization["SettingsHotkeySaveFailed"];
            return;
        }
        RefreshHotkeyText(_settingsStore.Current);
        _applyHudSettings(_settingsStore.Current);
        HotkeyValidationMessage = Localization[desired.HotkeysCustomized ? "SettingsHotkeyApplied" : "SettingsHotkeyDefaultsRestored"];
    }

    private void RefreshHotkeyText(AppSettings settings)
    {
        LockHotkeyText = FormatHotkey(settings.HudLockHotkey, HotkeySetting.DefaultLockToggle);
        VisibilityHotkeyText = FormatHotkey(settings.HudVisibilityHotkey, HotkeySetting.DefaultVisibilityToggle);
        PreviousChannelHotkeyText = FormatOptionalHotkey(settings.PreviousMainChannelHotkey);
        NextChannelHotkeyText = FormatOptionalHotkey(settings.NextMainChannelHotkey);
        GeneralTimerHotkeyText = FormatOptionalHotkey(settings.GeneralTimerHotkey);
        BunkerTimerHotkeyText = FormatOptionalHotkey(settings.BunkerTimerHotkey);
        LsdTimerHotkeyText = FormatOptionalHotkey(settings.LsdTimerHotkey);
    }

    private void RequestManualSalesResync()
    {
        var result = _manualSalesResync();
        ManualSalesResyncStatusMessage = Localization[result switch
        {
            ManualSalesResyncResult.Requested => "SettingsManualResyncRequested",
            ManualSalesResyncResult.Coalesced => "SettingsManualResyncCoalesced",
            ManualSalesResyncResult.RemoteUnavailable => "SettingsManualResyncDisconnected",
            _ => "SettingsManualResyncTrackingDisabled",
        }];
        RefreshDiagnostics();
    }

    private void ClearMediaCache()
    {
        _clearMediaCache();
        MediaCacheStatusMessage = Localization["SettingsMediaCacheCleared"];
    }

    private void ApplyChatPreset() => ApplyChatPreset(SelectedChatStylePreset);

    private void ApplyChatPreset(ChatStylePreset preset)
    {
        _selectedChatStylePreset = preset;
        OnPropertyChanged(nameof(SelectedChatStylePreset));
        var applied = GachaOverlay.Core.Chat.ChatStylePresets.Apply(
            _settingsStore.Current,
            preset);
        if (!_settingsStore.Update(_ => applied))
        {
            _logger.Warning(
                "SETTINGS",
                "The chat style preset changed for this session but could not be persisted.");
        }

        LoadChatSettings(_settingsStore.Current);
        _applyHudSettings(_settingsStore.Current);
        RefreshChatPresetState();
    }

    private void SaveAndApply(Func<AppSettings, AppSettings> update)
    {
        if (!_settingsStore.Update(update))
        {
            _logger.Warning(
                "SETTINGS",
                "A HUD setting changed for this session but could not be persisted.");
        }

        _applyHudSettings(_settingsStore.Current);
        RefreshChatPresetState();
    }

    private IReadOnlyList<HudVisibilityModeOption> CreateVisibilityModes() =>
        new[]
        {
            new HudVisibilityModeOption(HudVisibilityMode.Always, Localization["SettingsVisibilityAlways"]),
            new HudVisibilityModeOption(
                HudVisibilityMode.GameForegroundOnly,
                Localization["SettingsVisibilityGameOnly"]),
        };

    private IReadOnlyList<SessionHostOption> CreateSessionHostOptions() => new[]
    {
        new SessionHostOption(
            SessionHostSelection.Host1,
            Localization["SettingsSessionHost1"]),
        new SessionHostOption(
            SessionHostSelection.Host2,
            Localization["SettingsSessionHost2"]),
    };

    private IReadOnlyList<RoleIconPositionOption> CreateRoleIconPositions() => new[]
    {
        new RoleIconPositionOption(
            RoleIconPosition.Left,
            Localization["SettingsRoleIconLeft"]),
        new RoleIconPositionOption(
            RoleIconPosition.AdjacentRight,
            Localization["SettingsRoleIconRight"]),
        new RoleIconPositionOption(
            RoleIconPosition.FarRight,
            Localization["SettingsRoleIconFarRight"]),
    };

    private IReadOnlyList<SettingsCategoryOption> CreateSettingsCategories() => new[]
    {
        CreateCategory(SettingsCategory.General, "SettingsCategoryGeneral", "M12,2A10,10 0 1 0 12,22A10,10 0 1 0 12,2M12,7A5,5 0 1 1 12,17A5,5 0 1 1 12,7"),
        CreateCategory(SettingsCategory.Discord, "SettingsCategoryDiscord", "M8.6,12.8L5.8,15.6A3,3 0 0 0 10,19.8L13.1,16.7M15.4,11.2L18.2,8.4A3,3 0 0 0 14,4.2L10.9,7.3M8.5,15.5L15.5,8.5"),
        CreateCategory(SettingsCategory.Hud, "SettingsCategoryHud", "M3,4H21V17H3ZM8,21H16M12,17V21"),
        CreateCategory(SettingsCategory.Chat, "SettingsCategoryChat", "M4,4H20V16H9L4,20ZM8,8H16M8,12H14"),
        CreateCategory(SettingsCategory.Media, "SettingsCategoryMedia", "M3,5H21V19H3ZM6,16L10,12L13,15L16,10L20,16M8,9A1.5,1.5 0 1 1 8,6A1.5,1.5 0 1 1 8,9"),
        CreateCategory(SettingsCategory.Sales, "SettingsCategorySales", "M5,3H19V21H5ZM8,7H16M8,11H16M8,15H13M8,19H11"),
        CreateCategory(SettingsCategory.SalesHistory, "SettingsCategorySalesHistory", "M4,4H20V20H4ZM8,8H16M8,12H16M8,16H13M17,15V19M15,17H19"),
        CreateCategory(SettingsCategory.Timers, "SettingsTimerTitle", "M12,3A9,9 0 1 0 12,21A9,9 0 1 0 12,3M12,7V12L15,14M9,2H15"),
        CreateCategory(SettingsCategory.Hotkeys, "SettingsCategoryHotkeys", "M3,6H21V18H3ZM6,10H8M10,10H12M14,10H16M18,10H19M6,14H8M10,14H17"),
        CreateCategory(SettingsCategory.Diagnostics, "SettingsCategoryDiagnostics", "M14.7,6.3A5,5 0 0 0 8.3,12.7L3.5,17.5L6.5,20.5L11.3,15.7A5,5 0 0 0 17.7,9.3L14,13L11,10Z"),
        CreateCategory(SettingsCategory.Developer, "SettingsCategoryDeveloper", "M9,3H15L16,7L20,9V15L16,17L15,21H9L8,17L4,15V9L8,7ZM12,9A3,3 0 1 0 12,15A3,3 0 1 0 12,9"),
    };

    private SettingsCategoryOption CreateCategory(
        SettingsCategory category,
        string localizationKey,
        string geometry) => new(
            category,
            Localization[localizationKey],
            System.Windows.Media.Geometry.Parse(geometry));

    private IReadOnlyList<ChatLayoutModeOption> CreateChatLayoutModes() => new[]
    {
        new ChatLayoutModeOption(ChatLayoutMode.Compact, Localization["SettingsChatCompact"]),
        new ChatLayoutModeOption(ChatLayoutMode.Balanced, Localization["SettingsChatBalanced"]),
    };

    private IReadOnlyList<ChatFontPresetOption> CreateChatFontPresets() => new[]
    {
        CreateFontOption(ChatFontPreset.Pretendard, Localization["SettingsFontPretendard"]),
        CreateFontOption(ChatFontPreset.Kimm, Localization["SettingsFontKimm"]),
        CreateFontOption(ChatFontPreset.WantedSans, Localization["SettingsFontWantedSans"]),
        CreateFontOption(ChatFontPreset.Cafe24ProSlim, Localization["SettingsFontCafe24ProSlim"]),
        CreateFontOption(ChatFontPreset.ChosunGulim, Localization["SettingsFontChosunGulim"]),
    };

    private IReadOnlyList<ChatStylePresetOption> CreateChatStylePresets() => new[]
    {
        CreateStylePresetOption(
            ChatStylePreset.Clean,
            "SettingsPresetClean",
            "SettingsFontPretendard",
            "SettingsPresetCleanDescription",
            isRecommended: true),
        CreateStylePresetOption(
            ChatStylePreset.Modern,
            "SettingsPresetModern",
            "SettingsFontKimm",
            "SettingsPresetModernDescription",
            isRecommended: false),
        CreateStylePresetOption(
            ChatStylePreset.HighReadability,
            "SettingsPresetHighReadability",
            "SettingsFontWantedSans",
            "SettingsPresetHighReadabilityDescription",
            isRecommended: false),
        CreateStylePresetOption(
            ChatStylePreset.GtaLegacy,
            "SettingsPresetGtaLegacy",
            "SettingsFontCafe24ProSlim",
            "SettingsPresetGtaLegacyDescription",
            isRecommended: false),
    };

    private ChatFontPresetOption CreateFontOption(ChatFontPreset value, string displayName)
    {
        var typography = _typographyResolver.Resolve(value);
        return new ChatFontPresetOption(
            value,
            displayName,
            typography.Message.FontFamily,
            typography.Message.FontWeight,
            Localization["SettingsFontPreviewSample"],
            BuildResolutionStatus(typography));
    }

    private ChatStylePresetOption CreateStylePresetOption(
        ChatStylePreset value,
        string nameKey,
        string fontKey,
        string descriptionKey,
        bool isRecommended)
    {
        var settings = GachaOverlay.Core.Chat.ChatStylePresets.Apply(
            AppSettings.CreateDefault(),
            value);
        var typography = _typographyResolver.Resolve(settings.ChatFontPreset);
        return new ChatStylePresetOption(
            value,
            Localization[nameKey],
            Localization[fontKey],
            Localization[descriptionKey],
            Localization["SettingsPresetPreviewNickname"],
            Localization["SettingsPresetPreviewMessage"],
            isRecommended,
            Localization["SettingsRecommended"],
            typography,
            BuildResolutionStatus(typography),
            ApplyChatPreset);
    }

    private IReadOnlyList<ChatImageModeOption> CreateChatImageModes() => new[]
    {
        new ChatImageModeOption(ChatImageMode.ThumbnailOnly, Localization["SettingsImageThumbnail"]),
        new ChatImageModeOption(ChatImageMode.ThumbnailAndEnlarge, Localization["SettingsImageEnlarge"]),
    };

    private IReadOnlyList<ChatImageSizeModeOption> CreateChatImageSizeModes() => new[]
    {
        new ChatImageSizeModeOption(ChatImageSizeMode.Compact, Localization["SettingsImageSizeCompact"]),
        new ChatImageSizeModeOption(ChatImageSizeMode.Large, Localization["SettingsImageSizeLarge"]),
    };

    private static IReadOnlyList<ChatLineLimitOption> CreateChatMaxLineOptions() => new[]
    {
        new ChatLineLimitOption(1, "1"),
        new ChatLineLimitOption(2, "2"),
        new ChatLineLimitOption(3, "3"),
    };

    private IReadOnlyList<ColorThemeOption> CreateColorThemes() =>
        ColorThemeCatalog.All
            .Select(definition => new ColorThemeOption(
                definition,
                Localization[definition.DescriptionResourceKey],
                theme => SelectedColorTheme = theme))
            .ToArray();

    private string BuildResolutionStatus(ResolvedChatTypography typography)
    {
        var key = typography.IsFallback
            ? "SettingsFontResolvedFallback"
            : typography.Nickname.Source == ChatFontResolutionSource.Bundled
                ? "SettingsFontResolvedBundled"
                : "SettingsFontResolvedSystem";
        return string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            Localization[key],
            typography.ResolvedSummary,
            typography.RequestedDisplayName);
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        SettingsCategories = CreateSettingsCategories();
        VisibilityModes = CreateVisibilityModes();
        SessionHostOptions = CreateSessionHostOptions();
        ChatLayoutModes = CreateChatLayoutModes();
        ChatFontPresets = CreateChatFontPresets();
        ChatImageModes = CreateChatImageModes();
        ChatImageSizeModes = CreateChatImageSizeModes();
        ChatStylePresets = CreateChatStylePresets();
        ChatMaxLineOptions = CreateChatMaxLineOptions();
        RoleIconPositions = CreateRoleIconPositions();
        ColorThemes = CreateColorThemes();
        RefreshThemeSelection();
        HotkeyValidationMessage = string.Empty;
        ManualSalesResyncStatusMessage = string.Empty;
        MediaCacheStatusMessage = string.Empty;
        RefreshDiagnostics();
        RefreshChatPresetState();
        OnPropertyChanged(nameof(SelectedChatFontPreviewFamily));
        OnPropertyChanged(nameof(SelectedChatFontPreviewWeight));
        OnPropertyChanged(nameof(SelectedChatTypography));
        OnPropertyChanged(nameof(SelectedChatFontNotice));
        OnPropertyChanged(nameof(ProductCatalogSummaryText));
        OnPropertyChanged(nameof(ProductOverrideCountText));
        OnPropertyChanged(nameof(ProductCatalogLoadStatusText));
    }

    private void LoadChatSettings(AppSettings settings)
    {
        _hudSurfaceOpacity = settings.HudSurfaceOpacity;
        _hudChromeOpacity = settings.HudChromeOpacity;
        _chatSurfaceOpacity = settings.ChatSurfaceOpacity;
        _salesSurfaceOpacity = settings.SalesSurfaceOpacity;
        _queueDetailSurfaceOpacity = settings.QueueDetailSurfaceOpacity;
        _minimalHudMode = settings.MinimalHudMode;
        _selectedChatLayoutMode = settings.ChatLayoutMode;
        _chatShowTime = settings.ChatShowTime;
        _selectedChatFontPreset = settings.ChatFontPreset;
        _chatFontSizePoints = settings.ChatFontSizePoints;
        _chatNicknameOutlineEnabled = settings.ChatNicknameOutlineEnabled;
        _chatMessageOutlineEnabled = settings.ChatMessageOutlineEnabled;
        _chatNicknameOutlineThickness = settings.ChatNicknameOutlineThickness;
        _chatMessageOutlineThickness = settings.ChatMessageOutlineThickness;
        _chatLineHeightMultiplier = settings.ChatLineHeightMultiplier;
        _chatMessageSpacing = settings.ChatMessageSpacing;
        _selectedRoleIconPosition = settings.ChatRoleIconPosition;
        _chatReactionSize = settings.ChatReactionSize;
        _chatMaxLines = settings.ChatMaxLines;
        _chatShowImages = settings.ChatShowImages;
        _selectedChatImageMode = settings.ChatImageMode;
        _selectedChatImageSizeMode = settings.ChatImageSizeMode;
        _chatCustomEmojiEnabled = settings.ChatCustomEmojiEnabled;
        _chatStickerEnabled = settings.ChatStickerEnabled;
        _hidePreviewSourceUrl = settings.HidePreviewSourceUrl;

        foreach (var name in new[]
        {
            nameof(HudSurfaceOpacity),
            nameof(HudChromeOpacity),
            nameof(ChatSurfaceOpacity),
            nameof(SalesSurfaceOpacity),
            nameof(QueueDetailSurfaceOpacity),
            nameof(MinimalHudMode),
            nameof(SelectedChatLayoutMode),
            nameof(ChatShowTime),
            nameof(SelectedChatFontPreset),
            nameof(SelectedChatFontPreviewFamily),
            nameof(SelectedChatFontPreviewWeight),
            nameof(SelectedChatTypography),
            nameof(SelectedChatFontNotice),
            nameof(ChatFontSizePoints),
            nameof(ChatNicknameOutlineEnabled),
            nameof(ChatMessageOutlineEnabled),
            nameof(ChatNicknameOutlineThickness),
            nameof(ChatMessageOutlineThickness),
            nameof(ChatOutlineThickness),
            nameof(ChatLineHeightMultiplier),
            nameof(ChatMessageSpacing),
            nameof(SelectedRoleIconPosition),
            nameof(ChatReactionSize),
            nameof(ChatMaxLines),
            nameof(ChatShowImages),
            nameof(SelectedChatImageMode),
            nameof(SelectedChatImageSizeMode),
            nameof(ChatCustomEmojiEnabled),
            nameof(ChatStickerEnabled),
            nameof(HidePreviewSourceUrl),
        })
        {
            OnPropertyChanged(name);
        }

        RefreshChatPresetState();
    }

    private void RefreshChatPresetState()
    {
        var matched = GachaOverlay.Core.Chat.ChatStylePresets.Match(_settingsStore.Current);
        if (matched.HasValue)
        {
            _selectedChatStylePreset = matched.Value;
            OnPropertyChanged(nameof(SelectedChatStylePreset));
        }

        foreach (var option in ChatStylePresets)
        {
            option.SetSelected(matched.HasValue && option.Value == matched.Value);
        }

        ChatPresetStatusMessage = matched.HasValue
            ? string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                Localization["SettingsPresetActive"],
                ChatStylePresets.First(option => option.Value == matched.Value).DisplayName)
            : Localization["SettingsPresetCustom"];
    }

    private void RefreshThemeSelection()
    {
        foreach (var option in ColorThemes)
        {
            option.SetSelected(option.Value == _selectedColorTheme);
        }
    }

    private void SetAndSave<T>(ref T field, T value, Func<AppSettings, AppSettings> update, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        SaveAndApply(update);
        OnPropertyChanged(propertyName);
    }

    private static string FormatHotkey(HotkeySetting setting, HotkeySetting fallback)
    {
        if (!HotkeyGesture.TryParse(setting, out var gesture))
        {
            HotkeyGesture.TryParse(fallback, out gesture);
        }

        return gesture.ToString();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record ProductCatalogUiSnapshot(
    bool BuiltInLoaded,
    int BuiltInMappingCount,
    int BuiltInGroupCount,
    int OverrideCount)
{
    public static ProductCatalogUiSnapshot Empty { get; } = new(false, 0, 0, 0);
}

internal sealed record SessionHostOption(
    SessionHostSelection Value,
    string DisplayText);
