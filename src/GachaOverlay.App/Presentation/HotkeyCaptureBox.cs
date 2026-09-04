using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GachaOverlay.Core.Hud.Hotkeys;

namespace GachaOverlay.App.Presentation;

internal static class GlobalHotkeyDispatchGate
{
    private static int _captureCount;

    public static bool IsSuppressed => Volatile.Read(ref _captureCount) > 0;

    public static IDisposable Enter()
    {
        Interlocked.Increment(ref _captureCount);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref _captureCount);
        }
    }
}

internal enum HotkeyCaptureResultKind
{
    None,
    Commit,
    Cancel,
    Clear,
}

internal readonly record struct HotkeyCaptureResult(
    HotkeyCaptureResultKind Kind,
    string Value);

internal sealed class HotkeyCaptureModel
{
    private string _previous = string.Empty;
    private Key? _pendingKey;
    private HotkeyGesture _pendingGesture;

    public bool IsCapturing { get; private set; }
    public string DisplayText { get; private set; } = HotkeyCaptureBox.UnassignedText;

    public void Begin(string? current)
    {
        _previous = current?.Trim() ?? string.Empty;
        _pendingKey = null;
        IsCapturing = true;
        DisplayText = HotkeyCaptureBox.CapturePrompt;
    }

    public HotkeyCaptureResult Press(Key key, ModifierKeys modifiers)
    {
        if (!IsCapturing) return default;
        if (key == Key.Escape)
        {
            IsCapturing = false;
            DisplayText = HotkeyCaptureBox.FormatDisplay(_previous);
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Cancel, _previous);
        }

        if (key is Key.Delete or Key.Back)
        {
            IsCapturing = false;
            DisplayText = HotkeyCaptureBox.UnassignedText;
            return new HotkeyCaptureResult(HotkeyCaptureResultKind.Clear, string.Empty);
        }

        if (IsModifier(key))
        {
            DisplayText = FormatPendingModifiers(modifiers);
            return default;
        }

        if (!TryCreateGesture(key, modifiers, out var gesture)) return default;
        _pendingKey = key;
        _pendingGesture = gesture;
        DisplayText = HotkeyCaptureBox.FormatDisplay(gesture.ToString());
        return default;
    }

    public HotkeyCaptureResult Release(Key key)
    {
        if (!IsCapturing || _pendingKey != key) return default;
        IsCapturing = false;
        _pendingKey = null;
        var value = _pendingGesture.ToString();
        DisplayText = HotkeyCaptureBox.FormatDisplay(value);
        return new HotkeyCaptureResult(HotkeyCaptureResultKind.Commit, value);
    }

    public HotkeyCaptureResult Cancel()
    {
        if (!IsCapturing) return default;
        IsCapturing = false;
        _pendingKey = null;
        DisplayText = HotkeyCaptureBox.FormatDisplay(_previous);
        return new HotkeyCaptureResult(HotkeyCaptureResultKind.Cancel, _previous);
    }

    internal static bool TryCreateGesture(
        Key key,
        ModifierKeys modifiers,
        out HotkeyGesture gesture)
    {
        var normalized = modifiers & (ModifierKeys.Control | ModifierKeys.Shift |
                                      ModifierKeys.Alt | ModifierKeys.Windows);
        var hotkeyModifiers = HotkeyModifiers.None;
        if (normalized.HasFlag(ModifierKeys.Control)) hotkeyModifiers |= HotkeyModifiers.Control;
        if (normalized.HasFlag(ModifierKeys.Shift)) hotkeyModifiers |= HotkeyModifiers.Shift;
        if (normalized.HasFlag(ModifierKeys.Alt)) hotkeyModifiers |= HotkeyModifiers.Alt;
        if (normalized.HasFlag(ModifierKeys.Windows)) hotkeyModifiers |= HotkeyModifiers.Windows;
        gesture = new HotkeyGesture(hotkeyModifiers, KeyInterop.VirtualKeyFromKey(key));
        return gesture.IsValid;
    }

    internal static bool IsModifier(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string FormatPendingModifiers(ModifierKeys modifiers)
    {
        var values = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) values.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) values.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) values.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) values.Add("Win");
        values.Add(HotkeyCaptureBox.CapturePrompt);
        return string.Join(" + ", values);
    }
}

public sealed class HotkeyCaptureBox : System.Windows.Controls.Button
{
    internal const string CapturePrompt = "키를 입력하세요...";
    internal const string UnassignedText = "미지정";
    private readonly HotkeyCaptureModel _capture = new();
    private IDisposable? _dispatchSuppression;

    public static readonly DependencyProperty HotkeyTextProperty = DependencyProperty.Register(
        nameof(HotkeyText),
        typeof(string),
        typeof(HotkeyCaptureBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnHotkeyTextChanged));

    public HotkeyCaptureBox()
    {
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        LostKeyboardFocus += OnLostKeyboardFocus;
        Unloaded += (_, _) => CancelCapture();
        RefreshContent();
    }

    public string HotkeyText
    {
        get => (string)GetValue(HotkeyTextProperty);
        set => SetValue(HotkeyTextProperty, value);
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (!_capture.IsCapturing)
        {
            _capture.Begin(HotkeyText);
            _dispatchSuppression = GlobalHotkeyDispatchGate.Enter();
            Content = _capture.DisplayText;
        }
        Focus();
    }

    internal static string FormatDisplay(string? value)
    {
        if (!HotkeyGesture.TryParseDisplayText(value, out var gesture)) return UnassignedText;
        var parts = gesture.ToString().Split('+');
        for (var index = 0; index < parts.Length - 1; index++)
        {
            parts[index] = parts[index] switch
            {
                "Control" => "Ctrl",
                "Windows" => "Win",
                _ => parts[index],
            };
        }
        return string.Join(" + ", parts);
    }

    private static void OnHotkeyTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var control = (HotkeyCaptureBox)sender;
        if (!control._capture.IsCapturing) control.RefreshContent();
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (!_capture.IsCapturing) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        ApplyResult(_capture.Press(key, Keyboard.Modifiers));
        Content = _capture.DisplayText;
        args.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs args)
    {
        if (!_capture.IsCapturing) return;
        var key = args.Key == Key.System ? args.SystemKey : args.Key;
        ApplyResult(_capture.Release(key));
        Content = _capture.DisplayText;
        args.Handled = true;
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs args) => CancelCapture();

    private void CancelCapture() => ApplyResult(_capture.Cancel());

    private void ApplyResult(HotkeyCaptureResult result)
    {
        if (result.Kind == HotkeyCaptureResultKind.None) return;
        if (result.Kind is HotkeyCaptureResultKind.Commit or HotkeyCaptureResultKind.Clear)
            HotkeyText = result.Value;
        EndSuppression();
        RefreshContent();
    }

    private void EndSuppression()
    {
        _dispatchSuppression?.Dispose();
        _dispatchSuppression = null;
    }

    private void RefreshContent() => Content = FormatDisplay(HotkeyText);
}
