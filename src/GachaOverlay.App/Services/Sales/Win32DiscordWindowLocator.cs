using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GachaOverlay.App.Services.Sales;

internal interface IDiscordWindowLocator
{
    DiscordWindowCandidate? Locate();
}

internal sealed class Win32DiscordWindowLocator : IDiscordWindowLocator
{
    public DiscordWindowCandidate? Locate()
    {
        var candidates = new List<DiscordWindowCandidate>();
        _ = EnumWindows((windowHandle, parameter) =>
        {
            if (!IsWindowVisible(windowHandle))
            {
                return true;
            }

            GetWindowThreadProcessId(windowHandle, out var processIdValue);
            if (processIdValue == 0 || processIdValue > int.MaxValue)
            {
                return true;
            }

            try
            {
                using var process = Process.GetProcessById((int)processIdValue);
                var processName = process.ProcessName;
                if (!processName.Equals("Discord", StringComparison.OrdinalIgnoreCase) &&
                    !processName.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase) &&
                    !processName.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                candidates.Add(new DiscordWindowCandidate(
                    process.Id,
                    windowHandle.ToInt64(),
                    processName,
                    ReadWindowText(windowHandle),
                    ReadWindowClass(windowHandle),
                    true));
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            return true;
        }, IntPtr.Zero);

        return DiscordWindowSelectionPolicy.Select(candidates);
    }

    private static string ReadWindowText(IntPtr windowHandle)
    {
        var length = GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ReadWindowClass(IntPtr windowHandle)
    {
        var buffer = new StringBuilder(256);
        _ = GetClassName(windowHandle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsCallback(IntPtr windowHandle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr windowHandle,
        out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(
        IntPtr windowHandle,
        StringBuilder text,
        int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr windowHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        IntPtr windowHandle,
        StringBuilder className,
        int maximumCount);
}
