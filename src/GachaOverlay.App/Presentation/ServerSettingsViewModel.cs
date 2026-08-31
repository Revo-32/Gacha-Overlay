using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;

namespace GachaOverlay.App.Presentation;

internal sealed class ServerSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly Func<bool, CancellationToken, Task<DiscordServerDiscoverySnapshot>> _discover;
    private readonly Func<DiscordMainChannelOption, CancellationToken, Task<MainChannelSwitchResult>> _switchMain;
    private IReadOnlyList<DiscordMainChannelOption> _mainChannels =
        Array.Empty<DiscordMainChannelOption>();
    private DiscordMainChannelOption? _selectedMainChannel;
    private string _guildName = string.Empty;
    private string _salesChannelName = string.Empty;
    private string _statusText = string.Empty;
    private string _statusKey = "SettingsServerConnectToInspect";
    private bool _isBusy;
    private bool _suppressSelection;
    private bool _loaded;
    private bool _disposed;
    private long _selectionRevision;

    public ServerSettingsViewModel(
        ISettingsStore settingsStore,
        ILocalizationService localization,
        Func<bool, CancellationToken, Task<DiscordServerDiscoverySnapshot>> discover,
        Func<DiscordMainChannelOption, CancellationToken, Task<MainChannelSwitchResult>> switchMain)
    {
        _settingsStore = settingsStore;
        _localization = localization;
        _discover = discover;
        _switchMain = switchMain;
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(forceRefresh: true));
        _localization.LanguageChanged += OnLanguageChanged;
        SetStatus(_statusKey);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RefreshCommand { get; }

    public IReadOnlyList<DiscordMainChannelOption> MainChannels
    {
        get => _mainChannels;
        private set => Set(ref _mainChannels, value);
    }

    public DiscordMainChannelOption? SelectedMainChannel
    {
        get => _selectedMainChannel;
        set
        {
            if (!Set(ref _selectedMainChannel, value) || _suppressSelection || value is null)
            {
                return;
            }

            var revision = Interlocked.Increment(ref _selectionRevision);
            _ = SwitchAsync(value, revision);
        }
    }

    public string GuildName
    {
        get => _guildName;
        private set => Set(ref _guildName, value);
    }

    public string SalesChannelName
    {
        get => _salesChannelName;
        private set => Set(ref _salesChannelName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool IsReady => _loaded && MainChannels.Count > 0;

    public void EnsureLoaded()
    {
        if (!_loaded && !IsBusy)
        {
            _ = LoadAsync(forceRefresh: false);
        }
    }

    public async Task LoadAsync(bool forceRefresh)
    {
        if (_disposed)
        {
            return;
        }

        IsBusy = true;
        SetStatus("SettingsServerLoading");
        try
        {
            var result = await _discover(forceRefresh, CancellationToken.None);
            if (result.IsStale)
            {
                return;
            }

            _loaded = result.State == DiscordServerDiscoveryState.Ready;
            GuildName = result.GuildName ?? _localization["SettingsServerUnavailableName"];
            SalesChannelName = string.IsNullOrWhiteSpace(result.SalesChannelName)
                ? _localization["SettingsServerSalesMissing"]
                : $"#{result.SalesChannelName.TrimStart('#')}";
            MainChannels = result.MainChannels;
            _suppressSelection = true;
            try
            {
                SelectedMainChannel = MainChannels.SingleOrDefault(channel => string.Equals(
                    channel.ChannelId,
                    _settingsStore.Current.DiscordMainChannelId,
                    StringComparison.Ordinal));
            }
            finally
            {
                _suppressSelection = false;
            }

            SetStatus(result.State switch
            {
                DiscordServerDiscoveryState.Ready when SelectedMainChannel is null &&
                    !string.IsNullOrWhiteSpace(_settingsStore.Current.DiscordMainChannelId) =>
                    "SettingsServerCurrentMainMissing",
                DiscordServerDiscoveryState.Ready => "SettingsServerReady",
                DiscordServerDiscoveryState.TargetGuildMissing =>
                    "SettingsServerTargetMissing",
                DiscordServerDiscoveryState.DiscordNotRunning =>
                    "SettingsServerDiscordNotRunning",
                DiscordServerDiscoveryState.CredentialsMissing =>
                    "SettingsServerConnectToInspect",
                _ => "SettingsServerLoadFailed",
            });
            OnPropertyChanged(nameof(IsReady));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SwitchAsync(DiscordMainChannelOption channel, long revision)
    {
        IsBusy = true;
        SetStatus("SettingsServerSwitching");
        try
        {
            var result = await _switchMain(channel, CancellationToken.None);
            if (revision != Volatile.Read(ref _selectionRevision))
            {
                return;
            }

            SetStatus(result.Status switch
            {
                MainChannelSwitchStatus.Succeeded => "SettingsServerSwitchSucceeded",
                MainChannelSwitchStatus.NoChange => "SettingsServerSwitchNoChange",
                MainChannelSwitchStatus.InvalidChannel => "SettingsServerCurrentMainMissing",
                MainChannelSwitchStatus.Superseded => "SettingsServerSwitchSuperseded",
                MainChannelSwitchStatus.PersistenceFailed => "SettingsServerSwitchSaveFailed",
                _ => "SettingsServerSwitchFailed",
            });
            if (!result.IsSuccess)
            {
                _suppressSelection = true;
                try
                {
                    SelectedMainChannel = MainChannels.SingleOrDefault(candidate => string.Equals(
                        candidate.ChannelId,
                        _settingsStore.Current.DiscordMainChannelId,
                        StringComparison.Ordinal));
                }
                finally
                {
                    _suppressSelection = false;
                }
            }
        }
        finally
        {
            if (revision == Volatile.Read(ref _selectionRevision))
            {
                IsBusy = false;
            }
        }
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

    private void OnLanguageChanged(object? sender, EventArgs eventArgs) => SetStatus(_statusKey);

    private void SetStatus(string localizationKey)
    {
        _statusKey = localizationKey;
        StatusText = _localization[localizationKey];
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
