using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class OnboardingViewModel : INotifyPropertyChanged, IDisposable
{
    public const int StepCount = 3;
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

    public bool IsHudStep => StepIndex == 2;

    private void Previous() => StepIndex--;

    private Task NextAsync()
    {
        switch (StepIndex)
        {
            case 1 when Settings.RemoteChatSettings?.IsReady != true:
                ValidationMessage = _localization["OnboardingDiscordRequired"];
                return Task.CompletedTask;
        }

        StepIndex++;
        return Task.CompletedTask;
    }

    private void Finish()
    {
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

    private void EnterStep() { }

    private static int FindFirstIncompleteStep(
        AppSettings persisted,
        FoundationViewModel settings)
    {
        if (settings.RemoteChatSettings?.IsReady != true)
        {
            return 1;
        }

        return 2;
    }

    private void RaiseStepProperties()
    {
        foreach (var property in new[]
        {
            nameof(StepIndex),
            nameof(StepProgressText),
            nameof(IsLanguageStep),
            nameof(IsDiscordStep),
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
