using GachaOverlay.Core.Settings;
using GachaOverlay.Core.Timers;
using LSOverlay.Protocol;

namespace GachaOverlay.App.Services;

internal sealed class RemoteOnlinePlaytimeStatusSource : IOnlinePlaytimeStatusSource
{
    private OnlinePlaytimeAvailability _current = OnlinePlaytimeAvailability.Unknown;

    public RemoteOnlinePlaytimeStatusSource(AppSettings settings) { }

    public OnlinePlaytimeAvailability Current
    {
        get
        {
            return _current;
        }
    }

    public void ApplyProcess(GachaOverlay.Core.Hud.Game.GtaClientState state) =>
        _current = state.ProcessRunning && state.ValidTrackedProcess
            ? OnlinePlaytimeAvailability.Online : OnlinePlaytimeAvailability.Offline;

    // Compatibility adapters deliberately cannot override local GTA process authority.
    public void ApplySettings(AppSettings settings) { }

    public void ApplyBootstrap(BootstrapResponse bootstrap)
    {
    }

    public void ApplyPresence(HostPresenceSnapshot presence)
    {
    }

    public void ApplyConnection(RemoteChatSnapshot snapshot)
    {
    }
}
