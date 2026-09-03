using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace GachaOverlay.App.Services;

internal interface IDiscordForegroundService
{
    bool IsGtaEnhancedForeground();
    bool TryActivateDiscord();
}

/// <summary>Top-level HWND/process identity only. No Discord UI inspection or input automation.</summary>
internal sealed class DiscordForegroundService : IDiscordForegroundService
{
    public bool IsGtaEnhancedForeground() => string.Equals(ProcessName(GetForegroundWindow()), "GTA5_Enhanced", StringComparison.OrdinalIgnoreCase);

    public bool TryActivateDiscord()
    {
        nint selected = 0;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindow(window, 4) != 0 || GetWindowTextLength(window) == 0) return true;
            if (!string.Equals(ProcessName(window), "Discord", StringComparison.OrdinalIgnoreCase)) return true;
            // Electron's main browser HWND; excludes utility/tool and owned windows.
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (!className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal)) return true;
            selected = window;
            return false;
        }, 0);
        if (selected == 0) return false;
        if (IsIconic(selected)) ShowWindowAsync(selected, 9);
        return SetForegroundWindow(selected);
    }

    private static string? ProcessName(nint window)
    {
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out var pid);
        var process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            var length = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref length)
                ? Path.GetFileNameWithoutExtension(buffer.ToString()) : null;
        }
        finally { CloseHandle(process); }
    }

    private delegate bool EnumWindowsCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder buffer, int length);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder buffer, ref int length);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
