using System.Windows.Threading;

namespace GachaOverlay.App.Services;

internal interface IUiDispatcher
{
    bool CheckAccess();

    bool HasShutdownStarted { get; }

    void Invoke(Action action);

    void BeginInvoke(Action action);
}

internal sealed class UiDispatcherAdapter : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public UiDispatcherAdapter(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public bool CheckAccess() => _dispatcher.CheckAccess();

    public bool HasShutdownStarted =>
        _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished;

    public void Invoke(Action action) => _dispatcher.Invoke(action);

    public void BeginInvoke(Action action) => _dispatcher.BeginInvoke(action);
}
