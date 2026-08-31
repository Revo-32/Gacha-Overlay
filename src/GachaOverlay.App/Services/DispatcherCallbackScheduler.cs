using System.Windows.Threading;
using GachaOverlay.Core.Hud.Presentation;

namespace GachaOverlay.App.Services;

internal sealed class DispatcherCallbackScheduler : IUiCallbackScheduler
{
    private readonly Dispatcher _dispatcher;

    public DispatcherCallbackScheduler(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Schedule(Action callback) =>
        _dispatcher.BeginInvoke(DispatcherPriority.Render, callback);
}
