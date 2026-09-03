using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using GachaOverlay.Core.Localization;
using GachaOverlay.Core.Settings;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Presentation;

internal enum SessionRemoteState
{
    Awaiting,
    Live,
    Reconnecting,
    Unavailable,
}

internal sealed class SessionHudViewModel : INotifyPropertyChanged
{
    private const int MaximumHostSlot = 2;
    private readonly ILocalizationService _localization;
    private readonly Dictionary<int, HostPresenceSnapshot> _hosts = new();
    private AppSettings _settings;
    private SessionRemoteState _remoteState = SessionRemoteState.Awaiting;
    private bool _remoteConfigured;
    private bool _hasCanonicalBootstrap;
    private bool _isUltraCompact;
    private bool _isVisible;

    public SessionHudViewModel(
        ILocalizationService localization,
        AppSettings initialSettings)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _settings = initialSettings ?? throw new ArgumentNullException(nameof(initialSettings));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SessionHudItemViewModel> Items { get; } = new();

    public bool IsVisible
    {
        get => _isVisible;
        private set => SetField(ref _isVisible, value);
    }

    public bool IsCompactDisplay => _settings.MinimalHudMode || _isUltraCompact;

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var compactChanged = IsCompactDisplay != (settings.MinimalHudMode || _isUltraCompact);
        if (_settings.ShowGtaSession == settings.ShowGtaSession &&
            _settings.MinimalHudMode == settings.MinimalHudMode &&
            _settings.SelectedSessionHost == settings.SelectedSessionHost)
        {
            _settings = settings;
            return;
        }

        _settings = settings;
        if (compactChanged)
        {
            OnPropertyChanged(nameof(IsCompactDisplay));
        }

        Refresh();
    }

    public void UpdateLayout(bool isUltraCompact)
    {
        if (_isUltraCompact == isUltraCompact)
        {
            return;
        }

        var compactChanged = IsCompactDisplay != (_settings.MinimalHudMode || isUltraCompact);
        _isUltraCompact = isUltraCompact;
        if (compactChanged)
        {
            OnPropertyChanged(nameof(IsCompactDisplay));
        }

        Refresh();
    }

    public void UpdateRemoteState(bool configured, SessionRemoteState state)
    {
        if (_remoteConfigured == configured && _remoteState == state)
        {
            return;
        }

        if (_remoteConfigured != configured)
        {
            _hosts.Clear();
            _hasCanonicalBootstrap = false;
        }

        _remoteConfigured = configured;
        _remoteState = state;
        Refresh();
    }

    public void ApplyBootstrap(BootstrapResponse bootstrap)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        _hosts.Clear();
        foreach (var host in bootstrap.TrackedHosts
                     .Where(host => host.HostSlot is >= 1 and <= MaximumHostSlot)
                     .OrderBy(host => host.HostSlot)
                     .Take(MaximumHostSlot))
        {
            _hosts[host.HostSlot] = host;
        }

        _hasCanonicalBootstrap = true;
        Refresh();
    }

    public void ApplyPresence(HostPresenceSnapshot presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        if (presence.HostSlot is < 1 or > MaximumHostSlot)
        {
            return;
        }

        if (_hosts.TryGetValue(presence.HostSlot, out var existing) && existing == presence)
        {
            return;
        }

        _hosts[presence.HostSlot] = presence;
        Refresh();
    }

    public void RefreshLocalization() => Refresh();

    private void Refresh()
    {
        if (!_settings.ShowGtaSession || !_remoteConfigured)
        {
            ReplaceItems(Array.Empty<SessionHudItemViewModel>());
            return;
        }

        var selectedSlot = _settings.SelectedSessionHost == SessionHostSelection.Host2 ? 2 : 1;
        _hosts.TryGetValue(selectedSlot, out var selectedHost);
        var item = _remoteState == SessionRemoteState.Live && _hasCanonicalBootstrap && selectedHost is not null
            ? BuildHostItem(selectedHost, true)
            : null;
        IReadOnlyList<SessionHudItemViewModel> next = item is { IsAvailable: true }
            ? new[] { item }
            : Array.Empty<SessionHudItemViewModel>();
        ReplaceItems(next);
    }

    private IReadOnlyList<SessionHudItemViewModel> BuildTransientItems(
        int selectedSlot,
        string statusKey,
        bool compact)
    {
        if (compact)
        {
            return Array.Empty<SessionHudItemViewModel>();
        }

        return new[] { BuildStatusItem(selectedSlot, statusKey) };
    }

    private SessionHudItemViewModel BuildHostItem(
        HostPresenceSnapshot host,
        bool compact)
    {
        var current = host.CurrentPlayers ?? -1;
        var maximum = host.MaximumPlayers ?? 0;
        var validParty = current >= 0 && maximum > 0 && current <= maximum;
        if (host.State == HostPresenceState.GtaOnline && validParty)
        {
            var occupancy = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                _localization["SessionOccupancyFormat"],
                Math.Min(current, 30),
                30);
            var label = compact
                ? string.Empty
                : FormatHostLabel(host.HostSlot);
            return new SessionHudItemViewModel(
                host.HostSlot,
                label,
                current >= 30 ? _localization["SessionFull"] : occupancy,
                $"{_localization["SessionGtaOnline"]} {occupancy}",
                true,
                false,
                current >= 30);
        }

        var statusKey = host.State switch
        {
            HostPresenceState.AwaitingPresence => "SessionAwaiting",
            HostPresenceState.Offline => "SessionOffline",
            _ => "SessionUnavailable",
        };
        return BuildStatusItem(host.HostSlot, statusKey);
    }

    private SessionHudItemViewModel BuildStatusItem(
        int hostSlot,
        string statusKey)
    {
        var label = FormatHostLabel(hostSlot);
        var value = _localization[statusKey];
        return new SessionHudItemViewModel(
            hostSlot,
            label,
            value,
            $"{label} {value}",
            false,
            true);
    }

    private string FormatHostLabel(int hostSlot) =>
        _localization[hostSlot == 2 ? "SessionHost2" : "SessionHost1"];

    private void ReplaceItems(IReadOnlyList<SessionHudItemViewModel> next)
    {
        if (Items.SequenceEqual(next))
        {
            IsVisible = next.Count > 0;
            return;
        }

        if (Items.Count == next.Count)
        {
            for (var index = 0; index < next.Count; index++)
            {
                if (Items[index] != next[index])
                {
                    Items[index] = next[index];
                }
            }
        }
        else
        {
            Items.Clear();
            foreach (var item in next)
            {
                Items.Add(item);
            }
        }

        IsVisible = next.Count > 0;
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

internal sealed record SessionHudItemViewModel(
    int HostSlot,
    string Label,
    string Value,
    string AccessibleText,
    bool IsAvailable,
    bool IsLabelVisible,
    bool IsFull = false);
