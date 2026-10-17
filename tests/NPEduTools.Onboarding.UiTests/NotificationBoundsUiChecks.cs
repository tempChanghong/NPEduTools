using System.Reflection;
using System.Windows.Interop;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunNotificationBoundsChecks()
    {
        var assembly = typeof(MainWindow).Assembly;
        var boundsType = assembly.GetType("NPEduTools.App.DesktopBounds")!;
        var placement = assembly.GetType("NPEduTools.App.DesktopWindowBounds")!;
        var centered = placement.GetMethod("Centered", BindingFlags.Static | BindingFlags.NonPublic)!;
        double Value(object bounds, string name) => (double)boundsType.GetProperty(name)!.GetValue(bounds)!;
        foreach (var area in new[] { new System.Drawing.Rectangle(0, 0, 2560, 1360),
            new System.Drawing.Rectangle(-3840, 40, 3840, 2080), new System.Drawing.Rectangle(2560, -1440, 3840, 1380) })
        foreach (double scale in new[] { 1d, 1.5, 2 })
        {
            object workArea = Activator.CreateInstance(boundsType, [(double)area.Left, (double)area.Top, (double)area.Width, (double)area.Height])!;
            var output = centered.Invoke(null, [workArea, 1120 * scale, 720 * scale])!;
            double x = Value(output, "Left"), y = Value(output, "Top"), w = Value(output, "Width"), h = Value(output, "Height");
            Assert(x >= area.Left && y >= area.Top && x + w <= area.Right && y + h <= area.Bottom,
                "notification bounds escape a negative-origin or mixed-DPI work area");
            Assert(Math.Abs(x + w / 2 - (area.Left + area.Width / 2d)) < .001 &&
                Math.Abs(y + h / 2 - (area.Top + area.Height / 2d)) < .001,
                "notification centering scaled the monitor's absolute pixel origin");
        }
        Checks.Add("Notification geometry: native-pixel centering at 100/150/200%, negative and offset monitor origins; oversized content stays in the target work area (simulated topology)");

        var window = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Minor));
        try
        {
            // Notifications are modeless; their attention lifecycle may change Owner.
            window.Show();
            window.UpdateLayout();
            var handle = new WindowInteropHelper(window).Handle;
            var screenType = Type.GetType("System.Windows.Forms.Screen, System.Windows.Forms")!;
            var screen = screenType.GetMethod("FromHandle")!.Invoke(null, [handle])!;
            var area = (System.Drawing.Rectangle)screenType.GetProperty("WorkingArea")!.GetValue(screen)!;
            void CheckCentered()
            {
                Assert(GetWindowRect(handle, out var rect), "notification native bounds unavailable");
                Assert(rect.Left >= area.Left && rect.Top >= area.Top && rect.Right <= area.Right && rect.Bottom <= area.Bottom,
                    "notification does not fit the actual display work area");
                Assert(Math.Abs((rect.Left + rect.Right) / 2d - (area.Left + area.Right) / 2d) <= 1 &&
                    Math.Abs((rect.Top + rect.Bottom) / 2d - (area.Top + area.Bottom) / 2d) <= 1,
                    "notification native bounds are not centered in the actual work area");
            }
            window.UpdateLayout(); CheckCentered();
            SetWindowPos(handle, 0, area.Left + 12, area.Top + 12, 600, 400, 0x0214);
            typeof(SchoolNotificationWindow).GetMethod("FitWorkArea", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
            window.UpdateLayout(); CheckCentered();
            window.Shutdown();
            Checks.Add("Minor notification preview: actual HWND fits and centers within the current work area after first show and refit; no school service, sound or modal backdrop");
        }
        finally { window.Shutdown(); }
    }
}
