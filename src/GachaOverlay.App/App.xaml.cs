using System.Windows;
using System.Windows.Threading;
using GachaOverlay.App.Lifecycle;
using GachaOverlay.App.Services;
using GachaOverlay.Core.Logging;
using GachaOverlay.Infrastructure.Lifecycle;

namespace GachaOverlay.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName =
        @"Local\GachaOverlay.Foundation.74B75E39-1972-4FA1-B718-5546F7D85E30";

    private SingleInstanceGuard? _singleInstanceGuard;
    private ApplicationHost? _host;
    private ApplicationLifetimeService? _applicationLifetime;
    private int _fatalExceptionObserved;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(eventArgs);

        _applicationLifetime = new ApplicationLifetimeService(
            new UiDispatcherAdapter(Dispatcher),
            () => _host?.Logger ?? NullAppLogger.Instance,
            CompleteOrderedShutdown);

        if (eventArgs.Args.Length == 2 && eventArgs.Args[0] == "--verify-client-export")
        {
            _ = VerifyClientExportAsync(eventArgs.Args[1]);
            return;
        }

        if (!SingleInstanceGuard.TryAcquire(
                SingleInstanceMutexName,
                out _singleInstanceGuard,
                out var acquisitionError))
        {
            if (acquisitionError is not null)
            {
                System.Windows.MessageBox.Show(
                    "The application could not establish its single-instance lock.",
                    "Startup failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                _applicationLifetime.RequestExit(
                    ApplicationExitSource.StartupFailure,
                    -1);
                return;
            }

            _applicationLifetime.RequestExit(
                ApplicationExitSource.SecondaryInstance,
                0);
            return;
        }

        SubscribeExceptionBoundaries();

        try
        {
            _host = new ApplicationHost(
                this,
                () => _applicationLifetime.RequestExit(ApplicationExitSource.TrayExit, 0));
            _host.Start();
        }
        catch (Exception exception)
        {
            _host?.RecordCrash(exception, "Startup");
            _host?.Logger.Error("APP", "Fatal startup failure.", exception);
            ShowFatalError("StartupFatalTitle", "StartupFatalMessage");
            _applicationLifetime.RequestExit(ApplicationExitSource.StartupFailure, -1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        UnsubscribeExceptionBoundaries();
        _host?.PrepareForShutdown();
        _host?.Dispose();
        _host = null;
        _applicationLifetime = null;
        _singleInstanceGuard?.Dispose();
        _singleInstanceGuard = null;
        base.OnExit(eventArgs);
    }

    private async Task VerifyClientExportAsync(string directory)
    {
        var exitCode = await ClientExportVerification.RunAsync(this, directory);
        _applicationLifetime?.RequestExit(ApplicationExitSource.ClientVerification, exitCode);
    }

    private void CompleteOrderedShutdown(int exitCode)
    {
        _host?.PrepareForShutdown();
        _host?.Dispose();
        _host = null;
        Shutdown(exitCode);
    }

    private void SubscribeExceptionBoundaries()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void UnsubscribeExceptionBoundaries()
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (Interlocked.Exchange(ref _fatalExceptionObserved, 1) != 0)
        {
            return;
        }

        _host?.Logger.Error("APP", "Unhandled UI exception; orderly shutdown will begin.", eventArgs.Exception);
        _host?.RecordCrash(eventArgs.Exception, "WPF Dispatcher");
        ShowFatalError("UnexpectedErrorTitle", "UnexpectedErrorMessage");
        _applicationLifetime?.RequestExit(
            ApplicationExitSource.FatalUnhandledException,
            -1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        if (eventArgs.ExceptionObject is Exception exception)
        {
            _host?.RecordCrash(exception, "AppDomain");
            _host?.Logger.Error("APP", "Unhandled process exception.", exception);
        }
    }

    private void OnUnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs eventArgs)
    {
        _host?.RecordCrash(eventArgs.Exception, "TaskScheduler");
        _host?.Logger.Error("APP", "Unobserved task exception.", eventArgs.Exception);
        eventArgs.SetObserved();
    }

    private void ShowFatalError(string titleKey, string messageKey)
    {
        var title = _host?.GetLocalizedString(titleKey, "Application error")
            ?? "Application error";
        var message = _host?.GetLocalizedString(
                messageKey,
                "The application encountered a fatal error. See the log for details.")
            ?? "The application encountered a fatal error. See the log for details.";

        System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
