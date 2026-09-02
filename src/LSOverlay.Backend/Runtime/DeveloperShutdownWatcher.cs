using Microsoft.Extensions.Hosting;

namespace LSOverlay.Backend.Runtime;

internal sealed class DeveloperShutdownWatcher : IHostedService, IDisposable
{
    public const string EnvironmentVariable = "LSO_DEV_SHUTDOWN_FILE";

    private readonly IHostApplicationLifetime _lifetime;
    private FileSystemWatcher? _watcher;

    public DeveloperShutdownWatcher(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(fullPath))
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        _watcher.Created += OnCreated;
        if (File.Exists(fullPath))
        {
            _lifetime.StopApplication();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.Created -= OnCreated;
        _watcher.Dispose();
        _watcher = null;
    }

    private void OnCreated(object sender, FileSystemEventArgs eventArgs) =>
        _lifetime.StopApplication();
}
