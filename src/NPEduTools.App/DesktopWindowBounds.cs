using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

internal readonly record struct DesktopBounds(double Left, double Top, double Width, double Height);

internal static class DesktopWindowBounds
{
    // Native desktop coordinates remain in pixels. Dividing a monitor's absolute origin
    // by its DPI is incorrect on mixed-DPI desktops (including monitors left of primary).
    internal static DesktopBounds Constrain(DesktopBounds bounds, DesktopBounds area)
    {
        if (new[] { bounds.Left, bounds.Top, bounds.Width, bounds.Height, area.Left, area.Top, area.Width, area.Height }.Any(v => !double.IsFinite(v)) ||
            bounds.Width <= 0 || bounds.Height <= 0 || area.Width <= 0 || area.Height <= 0) throw new ArgumentException("Invalid window bounds.");
        double width = Math.Min(bounds.Width, area.Width), height = Math.Min(bounds.Height, area.Height);
        return new(Math.Clamp(bounds.Left, area.Left, area.Left + area.Width - width),
            Math.Clamp(bounds.Top, area.Top, area.Top + area.Height - height), width, height);
    }

    internal static DesktopBounds Centered(DesktopBounds area, double width, double height)
    {
        var fitted = Constrain(new(area.Left, area.Top, width, height), area);
        return fitted with { Left = area.Left + (area.Width - fitted.Width) / 2,
            Top = area.Top + (area.Height - fitted.Height) / 2 };
    }

    internal static void PlaceCentered(Window window, DesktopBounds area, double width, double height)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var bounds = Centered(area, width, height);
        SetWindowPos(handle, 0, (int)Math.Round(bounds.Left), (int)Math.Round(bounds.Top),
            (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x0214);
    }

    internal static void KeepVisible(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        nint handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !GetWindowRect(handle, out var rect)) return;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var before = new DesktopBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var after = Constrain(before, new(area.Left, area.Top, area.Width, area.Height));
        if (after == before) return;
        SetWindowPos(handle, 0, (int)Math.Round(after.Left), (int)Math.Round(after.Top),
            (int)Math.Round(after.Width), (int)Math.Round(after.Height), 0x0214); // no activation/Z-order/owner changes
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint after, int x, int y, int width, int height, uint flags);
}
