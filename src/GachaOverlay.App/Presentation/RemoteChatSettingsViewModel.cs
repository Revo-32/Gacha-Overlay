using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Localization;

namespace GachaOverlay.App.Presentation;

internal sealed class RemoteChatSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService _localization;
    private readonly Func<string, Task<bool>> _applyConfiguration;
    private readonly Func<Task> _beginPairing;
    private readonly Action _cancelPairing;
    private readonly Func<Task<bool>> _forgetPairing;
    private readonly Func<Task> _refresh;
    private readonly Func<string, Task<bool>> _switchChannel;
    private RemoteChatSnapshot _snapshot;
    private string _backendBaseUrl;
    private RemoteChannelOption? _selectedChannel;
    private string _statusMessage = string.Empty;

    public RemoteChatSettingsViewModel(
        ILocalizationService localization,
        RemoteChatSnapshot initialSnapshot,
        Func<string, Task<bool>> applyConfiguration,
        Func<Task> beginPairing,
        Action cancelPairing,
        Func<Task<bool>> forgetPairing,
        Func<Task> refresh,
        Func<string, Task<bool>> switchChannel)
    {
        _localization = localization;
        _snapshot = initialSnapshot;
        _backendBaseUrl = initialSnapshot.BackendBaseUrl;
        _applyConfiguration = applyConfiguration;
        _beginPairing = beginPairing;
        _cancelPairing = cancelPairing;
        _forgetPairing = forgetPairing;
        _refresh = refresh;
        _switchChannel = switchChannel;
        _selectedChannel = FindSelectedChannel(initialSnapshot);
        ApplyConfigurationCommand = new AsyncRelayCommand(ApplyConfigurationAsync);
        BeginPairingCommand = new AsyncRelayCommand(beginPairing, () => !IsPairing && NeedsLogin);
        CancelPairingCommand = new RelayCommand(cancelPairing, () => IsPairing);
        ForgetPairingCommand = new AsyncRelayCommand(ForgetPairingAsync, () => HasCredential);
        RefreshCommand = new AsyncRelayCommand(refresh);
        SwitchChannelCommand = new AsyncRelayCommand(
            SwitchChannelAsync,
            () => SelectedChannel is not null);
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string BackendBaseUrl
    {
        get => _backendBaseUrl;
        set
        {
            if (SetField(ref _backendBaseUrl, value))
            {
                StatusMessage = string.Empty;
            }
        }
    }

    public IReadOnlyList<RemoteChannelOption> Channels => _snapshot.Channels;

    public RemoteChannelOption? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (SetField(ref _selectedChannel, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool HasCredential => _snapshot.HasProtectedCredential;

    public bool NeedsLogin => !HasCredential || _snapshot.Health is
        RemoteChatHealthState.PairingRequired or RemoteChatHealthState.AccessRevoked;

    public bool IsReady =>
        _snapshot.Health == RemoteChatHealthState.Live &&
        !string.IsNullOrWhiteSpace(_snapshot.SelectedChannelId);

    public bool IsPairing => _snapshot.Health == RemoteChatHealthState.PairingInProgress;

    public bool HasPairingCode => !string.IsNullOrWhiteSpace(_snapshot.PairingCode);

    public string PairingCode => _snapshot.PairingCode ?? string.Empty;

    public string PairingInstruction => HasPairingCode
        ? string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _localization["SettingsRemotePairingInstruction"],
            PairingCode)
        : string.Empty;

    public string HealthText => _localization[_snapshot.Detail == "WebAuthWaiting" ? "WebAuthWaiting" : $"RemoteHealth{_snapshot.Health}"];

    public string HealthDetailText => _localization[ResolveDetailKey()];

    public string CredentialStatusText => _localization[
        HasCredential ? "SettingsRemoteCredentialSaved" : "SettingsRemoteCredentialMissing"];

    public string RemoteSalesHealthText => $"Remote Sales: {_snapshot.RemoteSalesStatus}";

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public ICommand ApplyConfigurationCommand { get; }

    public AsyncRelayCommand BeginPairingCommand { get; }

    public RelayCommand CancelPairingCommand { get; }

    public AsyncRelayCommand ForgetPairingCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand SwitchChannelCommand { get; }

    public void UpdateSnapshot(RemoteChatSnapshot snapshot)
    {
        _snapshot = snapshot;
        _backendBaseUrl = snapshot.BackendBaseUrl;
        _selectedChannel = FindSelectedChannel(snapshot);
        OnPropertyChanged(string.Empty);
        RaiseCommandStates();
    }

    public void Dispose()
    {
        _cancelPairing();
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private async Task ApplyConfigurationAsync()
    {
        var succeeded = await _applyConfiguration(BackendBaseUrl);
        StatusMessage = _localization[succeeded
            ? "SettingsRemoteConfigurationApplied"
            : "SettingsRemoteConfigurationFailed"];
    }

    private async Task ForgetPairingAsync()
    {
        var succeeded = await _forgetPairing();
        StatusMessage = _localization[succeeded
            ? "SettingsRemotePairingForgotten"
            : "SettingsRemotePairingForgetFailed"];
    }

    private async Task SwitchChannelAsync()
    {
        if (SelectedChannel is null)
        {
            return;
        }

        var succeeded = await _switchChannel(SelectedChannel.ChannelId);
        StatusMessage = _localization[succeeded
            ? "SettingsRemoteChannelSwitchRequested"
            : "SettingsRemoteChannelSwitchFailed"];
    }

    private void OnLanguageChanged(object? sender, EventArgs eventArgs)
    {
        OnPropertyChanged(nameof(PairingInstruction));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(HealthDetailText));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(RemoteSalesHealthText));
    }

    private static RemoteChannelOption? FindSelectedChannel(RemoteChatSnapshot snapshot) =>
        snapshot.Channels.FirstOrDefault(channel =>
            channel.ChannelId == snapshot.SelectedChannelId);

    private string ResolveDetailKey()
    {
        if (_snapshot.Detail.StartsWith("WebAuth", StringComparison.Ordinal))
            return _snapshot.Detail;
        return _snapshot.Health switch
        {
            RemoteChatHealthState.PairingRequired => "RemoteDetailPairingRequired",
            RemoteChatHealthState.PairingInProgress => "RemoteDetailPairing",
            RemoteChatHealthState.Authenticating or
                RemoteChatHealthState.Connecting or
                RemoteChatHealthState.Bootstrapping => "RemoteDetailConnecting",
            RemoteChatHealthState.ChannelSelectionRequired => "RemoteDetailChannel",
            RemoteChatHealthState.Live => "RemoteDetailLive",
            RemoteChatHealthState.Reconnecting => "RemoteDetailNetwork",
            RemoteChatHealthState.AuthorizationUnavailable or
                RemoteChatHealthState.AccessRevoked => "RemoteDetailAuthentication",
            RemoteChatHealthState.Error when _snapshot.Detail.Contains(
                "Credential",
                StringComparison.OrdinalIgnoreCase) ||
                _snapshot.Detail.Contains("Protected", StringComparison.OrdinalIgnoreCase) =>
                    "RemoteDetailStorage",
            RemoteChatHealthState.Error => "RemoteDetailError",
            _ => "RemoteDetailDisconnected",
        };
    }

    private void RaiseCommandStates()
    {
        BeginPairingCommand.RaiseCanExecuteChanged();
        CancelPairingCommand.RaiseCanExecuteChanged();
        ForgetPairingCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        SwitchChannelCommand.RaiseCanExecuteChanged();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
