using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Services;

internal sealed class RemoteOnlinePlaytimeStatusSource : IOnlinePlaytimeStatusSource
{
    private readonly Dictionary<int, HostPresenceSnapshot> _hosts = new();
    private SessionHostSelection _selection;
    private bool _connected;

    public RemoteOnlinePlaytimeStatusSource(AppSettings settings) => _selection = settings.SelectedSessionHost;

    public OnlinePlaytimeAvailability Current
    {
        get
        {
            if (!_connected || !_hosts.TryGetValue((int)_selection, out var host))
                return OnlinePlaytimeAvailability.Unknown;
            return host.State switch
            {
                HostPresenceState.GtaOnline => OnlinePlaytimeAvailability.Online,
                HostPresenceState.Offline or HostPresenceState.OnlineButNotGtaOnline =>
                    OnlinePlaytimeAvailability.Offline,
                _ => OnlinePlaytimeAvailability.Unknown,
            };
        }
    }

    public void ApplySettings(AppSettings settings) => _selection = settings.SelectedSessionHost;

    public void ApplyBootstrap(BootstrapResponse bootstrap)
    {
        _hosts.Clear();
        foreach (var host in bootstrap.TrackedHosts) _hosts[host.HostSlot] = host;
        _connected = true;
    }

    public void ApplyPresence(HostPresenceSnapshot presence)
    {
        _hosts[presence.HostSlot] = presence;
        _connected = true;
    }

    public void ApplyConnection(RemoteChatSnapshot snapshot)
    {
        _connected = snapshot.Health == RemoteChatHealthState.Live;
    }
}
