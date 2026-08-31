namespace GachaOverlay.Infrastructure.Discord.Process;

public interface IDiscordProcessService
{
    bool IsDiscordRunning();

    Task WaitUntilDiscordIsRunningAsync(CancellationToken cancellationToken);

    bool TryLaunchDiscord(bool accessibilityMode = false) => false;
}
