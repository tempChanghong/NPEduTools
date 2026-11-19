using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint QuickForeground();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int QuickWindowStyle(nint window, int index);

    private static void TraceQuickFocus(QuickAccessWindow quick, Window other, string action, bool? activated = null)
    {
        var edge = (Button)quick.FindName("EdgeHandle");
        var handle = new WindowInteropHelper(quick).Handle;
        Checks.Add($"{action}: activation={activated?.ToString() ?? "unrequested"}, foreground={QuickForeground():X}, " +
            $"rail={handle:X}/{quick.IsActive}, fixture={new WindowInteropHelper(other).Handle:X}/{other.IsActive}, " +
            $"railExStyle={QuickWindowStyle(handle, -20):X}, mouseCapture={edge.IsMouseCaptured}, touches={edge.TouchesCaptured.Count()}");
    }

    private static bool HasForeground(Window window) => window.IsActive && QuickForeground() == new WindowInteropHelper(window).Handle;

    private static void ActivateFocusFixture(QuickAccessWindow quick, Window other)
    {
        other.Show();
        TraceQuickFocus(quick, other, "focus fixture", other.Activate());
        if (HasForeground(other)) return;
        // An OS-denied Activate may still mark a window active on its own WPF thread.
        // Establish actual foreground using a click on the fixture's own visible HWND.
        other.Topmost = true;
        try
        {
            other.UpdateLayout();
            ClickFixturePoint(new WindowInteropHelper(other).Handle, other.PointToScreen(new Point(36, 40)), QuickForeground(),
                () => PumpUntil(() => HasForeground(other), "the fixture did not acquire actual keyboard foreground"));
        }
        finally { other.Topmost = false; }
    }

    private static void RunQuickBoundaryChecks()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        string pipe = "NPEduTools.Test.quick-boundaries." + Guid.NewGuid().ToString("N");
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        var failures = new List<string>();
        FixtureTouch? touch = null;
        Window? other = null;
        void Case(string name, Action test)
        {
            try { test(); Checks.Add(name + ": PASSED"); }
            catch (Exception e) { failures.Add(name + ": " + e.Message); Checks.Add(name + ": FAILED: " + e.Message); }
            finally { quick.SetCompact(false); quick.Collapse(false); }
        }
        void Settle()
        {
            var operation = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            PumpUntil(() => operation.Task.IsCompleted, "input queue did not settle");
        }
        try
        {
            quick.Show(); quick.UpdateLayout();
            var edge = (Button)quick.FindName("EdgeHandle"); var panel = (FrameworkElement)quick.FindName("PanelSurface");
            touch = new(301, quick, new Point(30, 30));
            void Touch(RoutedEvent routed) => edge.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = routed });
            void KeyEscape()
            {
                var source = PresentationSource.FromVisual(quick)!;
                quick.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            }
            Case("Escape during a pressed handle cannot reopen on release", () =>
            {
                touch.MoveTo(new Point(30, 30)); Touch(UIElement.PreviewTouchDownEvent);
                KeyEscape(); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "Escape was undone by the old finger release");
            });
            Case("Closing a panel during a pressed handle aborts the gesture", () =>
            {
                quick.OpenPanel(); quick.UpdateLayout();
                touch.MoveTo(new Point(30, 30)); Touch(UIElement.PreviewTouchDownEvent);
                quick.Collapse(false); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "dismissed panel reopened after release");
            });
            Case("Touch release uses the final position when the last move event is missing", () =>
            {
                touch.MoveTo(new Point(30, 30)); Touch(UIElement.PreviewTouchDownEvent);
                double scale = (double)typeof(QuickAccessWindow).GetField("_gestureScale", flags)!.GetValue(quick)!;
                touch.MoveTo(new Point(30, 30 - 30 * scale / VisualTreeHelper.GetDpi(quick).DpiScaleY));
                Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "a long release displacement was treated as a click");
            });
            Case("Folding the active panel returns focus to the previous application", () =>
            {
                other = new Window { Title = "NPEduTools.Test.quick-focus", Width = 240, Height = 160 };
                ActivateFocusFixture(quick, other);
                quick.OpenPanel(); TraceQuickFocus(quick, other, "panel opened");
                PumpUntil(() => HasForeground(quick), "quick panel could not become foreground");
                Click(quick, "RailFold"); Settle();
                Assert(HasForeground(other) && !quick.IsActive, "folded rail retained keyboard focus");
                other.Close(); other = null;
            });
            Case("Expanded touch drag keeps capture until release in full and compact layouts", () =>
            {
                foreach (bool compact in new[] { false, true })
                {
                    quick.SetCompact(compact); quick.OpenPanel(); quick.UpdateLayout();
                    var start = edge.TranslatePoint(new Point(20, 20), quick);
                    var oldTop = quick.PointToScreen(edge.TranslatePoint(new Point(), quick));
                    touch.MoveTo(start); Touch(UIElement.PreviewTouchDownEvent);
                    double scale = (double)typeof(QuickAccessWindow).GetField("_gestureScale", flags)!.GetValue(quick)!;
                    var end = quick.PointToScreen(start) - new Vector(0, 30 * scale);
                    touch.MoveTo(quick.PointFromScreen(end)); Touch(UIElement.PreviewTouchMoveEvent);
                    Assert(edge.TouchesCaptured.Any(t => t.Id == touch.Id), "layout collapse aborted its own drag");
                    quick.UpdateLayout();
                    var newTop = quick.PointToScreen(edge.TranslatePoint(new Point(), quick));
                    Assert(Math.Abs(newTop.Y - (oldTop.Y - 30 * scale)) <= 2, "captured handle jumped after collapse");
                    touch.MoveTo(quick.PointFromScreen(end)); Touch(UIElement.PreviewTouchUpEvent); Settle();
                    Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "expanded drag reopens or retains capture");
                }
            });
            Case("Fullscreen suppression while touching releases capture and ignores late up", () =>
            {
                touch.MoveTo(new Point(30, 30)); Touch(UIElement.PreviewTouchDownEvent);
                quick.SetAutoHidden(true);
                Assert(!quick.IsVisible && !edge.TouchesCaptured.Any(), "suppression retained touch capture");
                quick.SetAutoHidden(false); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "late touch revives suppressed panel");
            });
            Case("Dragging an expanded panel returns keyboard focus only after touch release", () =>
            {
                other = new Window { Title = "NPEduTools.Test.drag-focus", Width = 240, Height = 160 };
                ActivateFocusFixture(quick, other);
                quick.OpenPanel(); quick.UpdateLayout(); PumpUntil(() => HasForeground(quick), "expanded drag could not become foreground");
                var start = edge.TranslatePoint(new Point(20, 20), quick);
                touch.MoveTo(start); Touch(UIElement.PreviewTouchDownEvent);
                double scale = (double)typeof(QuickAccessWindow).GetField("_gestureScale", flags)!.GetValue(quick)!;
                var end = quick.PointToScreen(start) - new Vector(0, 30 * scale);
                touch.MoveTo(quick.PointFromScreen(end)); Touch(UIElement.PreviewTouchMoveEvent);
                Assert(quick.IsActive && edge.TouchesCaptured.Any(), "drag restores focus before releasing capture");
                touch.MoveTo(quick.PointFromScreen(end)); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(HasForeground(other) && !quick.IsActive && !edge.TouchesCaptured.Any(), "completed expanded drag retained foreground focus");
                other.Close(); other = null;
            });
        }
        finally
        {
            touch?.Stop(); other?.Close(); quick.Shutdown();
            string preference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
            File.Delete(preference); File.Delete(preference + ".tmp");
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join("\n", failures));
    }
}
