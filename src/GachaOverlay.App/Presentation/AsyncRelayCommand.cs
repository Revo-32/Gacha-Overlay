using System.Windows.Input;

namespace GachaOverlay.App.Presentation;

internal sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private int _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        Volatile.Read(ref _running) == 0 && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter) || Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
