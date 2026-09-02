using GachaOverlay.Core.Discord.Connection;
using GachaOverlay.Core.Discord.Messages;

namespace GachaOverlay.Core.Providers;

/// <summary>
/// Receives transport-neutral, normalized message mutations from an active provider.
/// Raw Discord Gateway, REST, or WebSocket payload types must be converted before
/// crossing this boundary.
/// </summary>
public interface IOverlayMessageIngress
{
    event Action<DiscordMessageState>? StateChanged;

    DiscordMessageState Current { get; }

    DiscordTargetChannels? Targets { get; }

    void SetAuthenticatedUser(string userId);

    bool StartBootstrap(long generation, DiscordTargetChannels targets);

    bool ReceiveLive(long generation, DiscordMessageMutation mutation);

    bool CompleteBootstrap(
        long generation,
        IEnumerable<DiscordMessagePatch> mainSnapshot,
        IEnumerable<DiscordMessagePatch> salesSnapshot);

    bool AbortBootstrap(long generation);

    void ClearForAccessRevocation();

    bool ReplaceMain(
        long generation,
        DiscordTargetChannels targets,
        IEnumerable<DiscordMessagePatch> mainSnapshot);
}
