using System.Diagnostics;

namespace GachaOverlay.Infrastructure.Discord.Process;

public sealed class DiscordProcessService : IDiscordProcessService
{
    private static readonly string[] ProcessNames =
    {
        "Discord",
        "DiscordCanary",
        "DiscordPTB",
        "DiscordDevelopment",
    };

    private readonly TimeSpan _pollInterval;

    public DiscordProcessService(TimeSpan? pollInterval = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public bool IsDiscordRunning()
    {
        foreach (var processName in ProcessNames)
        {
            System.Diagnostics.Process[] processes;
            try
            {
                processes = System.Diagnostics.Process.GetProcessesByName(processName);
            }
            catch
            {
                continue;
            }

            try
            {
                if (processes.Any(process => !process.HasExited))
                {
                    return true;
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }

    public async Task WaitUntilDiscordIsRunningAsync(CancellationToken cancellationToken)
    {
        while (!IsDiscordRunning())
        {
            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public bool TryLaunchDiscord(bool accessibilityMode = false)
    {
        if (IsDiscordRunning())
        {
            return true;
        }

        try
        {
            if (accessibilityMode)
            {
                var updater = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Discord",
                    "Update.exe");
                if (!File.Exists(updater))
                {
                    return false;
                }

                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = updater,
                    Arguments = "--processStart Discord.exe --process-start-args \"--force-renderer-accessibility\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return true;
            }

            System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "discord://-/",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
