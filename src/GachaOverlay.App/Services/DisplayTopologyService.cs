using System.Runtime.InteropServices;
using GachaOverlay.Core.Hud.Geometry;

namespace GachaOverlay.App.Services;

internal sealed class DisplayTopologyService
{
    private const uint MonitorDefaultToNearest = 2;

    public IReadOnlyList<DisplayWorkingArea> GetWorkingAreas()
    {
        var displays = new List<DisplayWorkingArea>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            displays.Add(new DisplayWorkingArea(
                screen.DeviceName,
                new HudRectangle(area.Left, area.Top, area.Width, area.Height),
                TryGetDpi(area.Left, area.Top),
                screen.Primary));
        }

        return displays;
    }

    private static double TryGetDpi(int x, int y)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(x + 1, y + 1), MonitorDefaultToNearest);
            return monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
                    ? DisplayWorkingArea.NormalizeDpi(dpiX)
                    : 96;
        }
        catch (DllNotFoundException)
        {
            return 96;
        }
        catch (EntryPointNotFoundException)
        {
            return 96;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }
}
