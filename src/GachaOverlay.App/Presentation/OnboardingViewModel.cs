using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class OnboardingViewModel : INotifyPropertyChanged, IDisposable
{
    public const int StepCount = 6;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly Action _completed;
    private int _stepIndex;
    private string _validationMessage = string.Empty;
    private bool _disposed;

    public OnboardingViewModel(
        FoundationViewModel settings,
        ISettingsStore settingsStore,
        ILocalizationService localization,
        Action completed,
        bool restartFromBeginning)
    {
        Settings = settings;
        _settingsStore = settingsStore;
        _localization = localization;
        _completed = completed;
        _stepIndex = restartFromBeginning
            ? 0
            : FindFirstIncompleteStep(settingsStore.Current, settings);
        PreviousCommand = new RelayCommand(Previous, () => StepIndex > 0);
        NextCommand = new AsyncRelayCommand(NextAsync, () => StepIndex < StepCount - 1);
        FinishCommand = new RelayCommand(Finish, () => StepIndex == StepCount - 1);
        _localization.LanguageChanged += OnLanguageChanged;
        EnterStep();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FoundationViewModel Settings { get; }

    public ICommand PreviousCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand FinishCommand { get; }

    public int StepIndex
    {
        get => _stepIndex;
        private set
        {
            if (_stepIndex == value)
            {
                return;
            }

            _stepIndex = Math.Clamp(value, 0, StepCount - 1);
            ValidationMessage = string.Empty;
            RaiseStepProperties();
            EnterStep();
        }
    }

    public string StepProgressText => string.Format(
        System.Globalization.CultureInfo.CurrentUICulture,
        _localization["OnboardingProgress"],
        StepIndex + 1,
        StepCount);

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            _validationMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsLanguageStep => StepIndex == 0;

    public bool IsDiscordStep => StepIndex == 1;

    public bool IsServerStep => StepIndex == 2;

    public bool IsMainStep => StepIndex == 3;

    public bool IsAccessibilityStep => StepIndex == 4;

    public bool IsHudStep => StepIndex == 5;

    private void Previous() => StepIndex--;

    private async Task NextAsync()
    {
        switch (StepIndex)
        {
            case 1 when !Settings.DiscordCanReconnect:
                ValidationMessage = _localization["OnboardingDiscordRequired"];
                return;
            case 2:
                await Settings.ServerSettings.LoadAsync(forceRefresh: false);
                if (!Settings.ServerSettings.IsReady)
                {
                    ValidationMessage = _localization["OnboardingServerRequired"];
                    return;
                }

                break;
            case 3 when string.IsNullOrWhiteSpace(
                _settingsStore.Current.DiscordMainChannelId):
                ValidationMessage = _localization["OnboardingMainRequired"];
                return;
            case 4 when Settings.SalesTrackingEnabled &&
                Settings.SalesUiaStatusText == _localization["SettingsUiaUnavailable"]:
                ValidationMessage = _localization["OnboardingAccessibilityRequired"];
                return;
        }

        StepIndex++;
    }

    private void Finish()
    {
        if (string.IsNullOrWhiteSpace(_settingsStore.Current.DiscordMainChannelId))
        {
            ValidationMessage = _localization["OnboardingMainRequired"];
            StepIndex = 3;
            return;
        }

        if (!_settingsStore.Update(settings => settings with
        {
            OnboardingVersion = AppSettings.CurrentOnboardingVersion,
        }))
        {
            ValidationMessage = _localization["OnboardingSaveFailed"];
            return;
        }

        _completed();
    }

    private void EnterStep()
    {
        if (StepIndex is 2 or 3)
        {
            Settings.ServerSettings.EnsureLoaded();
        }
    }

    private static int FindFirstIncompleteStep(
        AppSettings persisted,
        FoundationViewModel settings)
    {
        if (string.IsNullOrWhiteSpace(persisted.DiscordClientId))
        {
            return 0;
        }

        if (!settings.DiscordCanReconnect)
        {
            return 1;
        }

        if (string.IsNullOrWhiteSpace(persisted.DiscordMainChannelId))
        {
            return 2;
        }

        return 4;
    }

    private void RaiseStepProperties()
    {
        foreach (var property in new[]
        {
            nameof(StepIndex),
            nameof(StepProgressText),
            nameof(IsLanguageStep),
            nameof(IsDiscordStep),
            nameof(IsServerStep),
            nameof(IsMainStep),
            nameof(IsAccessibilityStep),
            nameof(IsHudStep),
        })
        {
            OnPropertyChanged(property);
        }

        (PreviousCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NextCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (FinishCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        ValidationMessage = string.Empty;
        OnPropertyChanged(nameof(StepProgressText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
