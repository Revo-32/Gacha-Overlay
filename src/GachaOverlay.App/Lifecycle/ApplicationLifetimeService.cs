using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;

namespace GachaOverlay.App.Lifecycle;

internal enum ApplicationExitSource
{
    TrayExit,
    StartupFailure,
    SecondaryInstance,
    FatalUnhandledException,
    WindowsSessionEnding,
    ClientVerification,
}

internal sealed class ApplicationLifetimeService
{
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<IAppLogger> _getLogger;
    private readonly Action<int> _orderedShutdown;
    private int _exitRequested;

    public ApplicationLifetimeService(
        IUiDispatcher dispatcher,
        Func<IAppLogger> getLogger,
        Action<int> orderedShutdown)
    {
        _dispatcher = dispatcher;
        _getLogger = getLogger;
        _orderedShutdown = orderedShutdown;
    }

    public bool IsExitRequested => Volatile.Read(ref _exitRequested) != 0;

    public bool RequestExit(ApplicationExitSource source, int exitCode)
    {
        if (Interlocked.Exchange(ref _exitRequested, 1) != 0)
        {
            _getLogger().Information("APP", $"Duplicate shutdown request ignored source={source}.");
            return false;
        }

        void ExitOnUi()
        {
            _getLogger().Information("APP", $"Shutdown requested source={source}.");
            _orderedShutdown(exitCode);
        }

        if (_dispatcher.CheckAccess())
        {
            ExitOnUi();
        }
        else
        {
            _dispatcher.BeginInvoke(ExitOnUi);
        }

        return true;
    }
}
