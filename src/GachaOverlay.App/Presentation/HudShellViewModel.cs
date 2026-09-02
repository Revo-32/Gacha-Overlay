using System.ComponentModel;
using System.Runtime.CompilerServices;
using GachaOverlay.Core.Hud;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class HudShellViewModel : INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private string _title = string.Empty;
    private string _hint = string.Empty;
    private string _lockStatus = string.Empty;
    private string _connectionStatus = string.Empty;
    private string _visibilityStatus = string.Empty;
    private string _gameStatus = string.Empty;
    private bool _isLocked = true;
    private bool _minimalHudMode;

    public HudShellViewModel(
        ILocalizationService localization,
        ChatViewModel chat,
        SalesQueueViewModel sales,
        SessionHudViewModel session)
    {
        _localization = localization;
        Chat = chat;
        Sales = sales;
        Session = session;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ChatViewModel Chat { get; }

    public ILocalizationService Localization => _localization;

    public SalesQueueViewModel Sales { get; }

    public SessionHudViewModel Session { get; }

    public string Title
    {
        get => _title;
        private set => SetField(ref _title, value);
    }

    public string Hint
    {
        get => _hint;
        private set => SetField(ref _hint, value);
    }

    public string LockStatus
    {
        get => _lockStatus;
        private set => SetField(ref _lockStatus, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    public string VisibilityStatus
    {
        get => _visibilityStatus;
        private set => SetField(ref _visibilityStatus, value);
    }

    public string GameStatus
    {
        get => _gameStatus;
        private set => SetField(ref _gameStatus, value);
    }

    public bool IsHudChromeVisible => !_minimalHudMode;

    public bool IsFloatingEditStripVisible => _minimalHudMode && !_isLocked;

    public bool IsUnlocked => !_isLocked;

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Session.ApplySettings(settings);
        if (_minimalHudMode == settings.MinimalHudMode)
        {
            return;
        }

        _minimalHudMode = settings.MinimalHudMode;
        OnPropertyChanged(nameof(IsHudChromeVisible));
        OnPropertyChanged(nameof(IsFloatingEditStripVisible));
    }

    public void Update(
        HudSessionState state,
        string connectionState,
        string connectionDetail,
        string? foregroundProcess)
    {
        if (_isLocked != state.IsLocked)
        {
            _isLocked = state.IsLocked;
            OnPropertyChanged(nameof(IsUnlocked));
            OnPropertyChanged(nameof(IsFloatingEditStripVisible));
        }

        Title = _localization["HudShellTitle"];
        Hint = _localization[state.IsLocked ? "HudLockedHint" : "HudUnlockedHint"];
        LockStatus = _localization[state.IsLocked ? "HudLocked" : "HudUnlocked"];
        ConnectionStatus = string.Format(
            _localization["HudConnectionStatusFormat"],
            connectionState,
            connectionDetail);
        VisibilityStatus = _localization[
            state.VisibilityMode == HudVisibilityMode.Always
                ? "HudVisibilityAlways"
                : "HudVisibilityGameOnly"];
        GameStatus = string.Format(
            _localization["HudGameStatusFormat"],
            foregroundProcess ?? _localization["HudUnknownProcess"],
            state.IsTargetGameForeground
                ? _localization["HudTargetYes"]
                : _localization["HudTargetNo"]);
        Session.RefreshLocalization();
    }

    private void SetField(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
