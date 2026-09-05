using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;

namespace GachaOverlay.App.Presentation;

// Like WPF's own TextBlock formatter ownership: at most Ideal + Display per dispatcher.
// Lines remain owned/disposed by their control. No message, geometry or image cache here.
internal sealed class DispatcherTextFormatters
{
    private static readonly ConditionalWeakTable<Dispatcher, DispatcherTextFormatters> Owners = new();
    private TextFormatter? _ideal;
    private TextFormatter? _display;

    private DispatcherTextFormatters(Dispatcher dispatcher)
    {
        dispatcher.ShutdownFinished += (_, _) =>
        {
            _ideal?.Dispose();
            _display?.Dispose();
            _ideal = _display = null;
        };
    }

    internal static TextFormatter Get(Dispatcher dispatcher, TextFormattingMode mode)
    {
        dispatcher.VerifyAccess();
        var owner = Owners.GetValue(dispatcher, static key => new DispatcherTextFormatters(key));
        return mode == TextFormattingMode.Display
            ? owner._display ??= TextFormatter.Create(TextFormattingMode.Display)
            : owner._ideal ??= TextFormatter.Create(TextFormattingMode.Ideal);
    }
}
