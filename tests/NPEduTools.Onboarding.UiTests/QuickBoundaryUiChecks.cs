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
            var hint = (FrameworkElement)quick.FindName("MoveHint");
            var preference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", flags)!.GetValue(quick)!;
            touch = new(301, quick, QuickHandleCenter(quick));
            void Touch(RoutedEvent routed) => edge.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = routed });
            void Start()
            {
                touch.MoveTo(QuickHandleCenter(quick)); Touch(UIElement.PreviewTouchDownEvent);
                Assert(edge.TouchesCaptured.Any(t => t.Id == touch.Id), "handle did not capture the starting finger");
            }
            Point HandleTop() => quick.PointToScreen(edge.TranslatePoint(new Point(), quick));
            void MoveTo(Point screenPoint, RoutedEvent routed)
            {
                touch.MoveTo(quick.PointFromScreen(screenPoint));
                Touch(routed);
            }
            void KeyEscape()
            {
                var source = PresentationSource.FromVisual(quick)!;
                quick.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = UIElement.PreviewKeyDownEvent });
            }
            Case("One touch on the compact handle opens the panel and returns to the same shape", () =>
            {
                quick.SetCompact(true); quick.UpdateLayout();
                var top = HandleTop();
                Start(); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.IsVisible && ((Button)quick.FindName("RailTools")).Visibility == Visibility.Collapsed,
                    "one touch on the compact handle did not directly open its panel");
                Assert((HandleTop() - top).Length <= 2, "opening the panel moved the compact handle");
                Snapshot(quick, "compact-handle-open-panel.png");
                Click(quick, "CollapseButton");
                Assert(!panel.IsVisible && ((Button)quick.FindName("RailTools")).Visibility == Visibility.Collapsed,
                    "closing the panel unexpectedly turned the compact handle into a full rail");
                quick.OpenPanel(); Click(quick, "LayoutButton"); Click(quick, "LayoutShape");
                Assert(!panel.IsVisible && Button(quick, "RailSecRandom").IsVisible,
                    "switching back to the full rail lost direct access to flash draw");
            });
            Case("Escape during a pressed handle cannot reopen on release", () =>
            {
                Start();
                KeyEscape(); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "Escape was undone by the old finger release");
            });
            Case("Closing a panel during a pressed handle aborts the gesture", () =>
            {
                quick.OpenPanel(); quick.UpdateLayout();
                Start();
                quick.Collapse(false); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "dismissed panel reopened after release");
            });
            Case("Locked handle cannot move, and a coalesced outside touch-up cannot open it", () =>
            {
                var top = HandleTop();
                string before = File.ReadAllText(preference);
                Start();
                touch.MoveTo(edge.TranslatePoint(new Point(edge.ActualWidth / 2, edge.ActualHeight + 6), quick));
                Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(!panel.IsVisible && !edge.TouchesCaptured.Any() && (HandleTop() - top).Length <= 2 &&
                    File.ReadAllText(preference) == before, "an outside release opened or moved the locked handle");
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
            Case("Explicit touch move keeps the original shape and only saves on release", () =>
            {
                foreach (bool compact in new[] { false, true })
                {
                    quick.SetSide(compact); quick.SetCompact(compact); EnterQuickMove(quick);
                    var hintLeft = hint.TranslatePoint(new Point(), quick).X;
                    var handleLeft = edge.TranslatePoint(new Point(), quick).X;
                    Assert((hintLeft > handleLeft) == quick.LeftSide, "position tips appeared beyond the screen edge");
                    if (compact) Snapshot(quick, "compact-handle-position-mode.png");
                    var top = HandleTop();
                    string before = File.ReadAllText(preference);
                    Start();
                    var start = quick.PointToScreen(QuickHandleCenter(quick));
                    double dpi = VisualTreeHelper.GetDpi(quick).DpiScaleY;
                    var end = start - new Vector(0, 30 * dpi);
                    MoveTo(start - new Vector(0, 9 * dpi), UIElement.PreviewTouchMoveEvent);
                    Assert((HandleTop() - top).Length <= 2, "small jitter moved the handle in position mode");
                    MoveTo(end, UIElement.PreviewTouchMoveEvent);
                    quick.UpdateLayout();
                    Assert(edge.TouchesCaptured.Any(t => t.Id == touch.Id) && Math.Abs(HandleTop().Y - (top.Y - 30 * dpi)) <= 2 &&
                        File.ReadAllText(preference) == before, "the captured handle jumped, lost capture or saved before release");
                    MoveTo(end, UIElement.PreviewTouchUpEvent); Settle();
                    Assert(!panel.IsVisible && !hint.IsVisible && !edge.TouchesCaptured.Any() && File.ReadAllText(preference) != before &&
                        Button(quick, "RailTools").IsVisible != compact, "release did not save and restore the original sidebar shape");
                }
            });
            Case("Lost capture rolls back visible position and preserves the saved position", () =>
            {
                EnterQuickMove(quick);
                var top = HandleTop();
                string before = File.ReadAllText(preference);
                Start();
                var end = quick.PointToScreen(QuickHandleCenter(quick)) - new Vector(0, 65);
                MoveTo(end, UIElement.PreviewTouchMoveEvent);
                Assert((HandleTop() - top).Length > 10, "test did not first move the handle");
                edge.ReleaseTouchCapture(touch);
                MoveTo(end, UIElement.PreviewTouchUpEvent); Settle();
                Assert(!hint.IsVisible && !panel.IsVisible && (HandleTop() - top).Length <= 2 &&
                    File.ReadAllText(preference) == before, "lost capture saved a partial move or a late up reopened the panel");
            });
            Case("Touch can cancel position mode without a keyboard", () =>
            {
                EnterQuickMove(quick);
                string before = File.ReadAllText(preference);
                Start(); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(!hint.IsVisible && !panel.IsVisible && File.ReadAllText(preference) == before,
                    "tapping did not cancel positioning without changing preferences");
            });
            Case("Fullscreen suppression while touching releases capture and ignores late up", () =>
            {
                EnterQuickMove(quick);
                string before = File.ReadAllText(preference);
                Start();
                MoveTo(quick.PointToScreen(QuickHandleCenter(quick)) - new Vector(0, 55), UIElement.PreviewTouchMoveEvent);
                quick.SetAutoHidden(true);
                Assert(!quick.IsVisible && !edge.TouchesCaptured.Any(), "suppression retained touch capture");
                quick.SetAutoHidden(false); Touch(UIElement.PreviewTouchUpEvent); Settle();
                Assert(!hint.IsVisible && !panel.IsVisible && !edge.TouchesCaptured.Any() && File.ReadAllText(preference) == before,
                    "suppression kept position mode, saved a move or let a late release revive the panel");
            });
            Case("An explicit move returns keyboard focus only after touch release", () =>
            {
                other = new Window { Title = "NPEduTools.Test.drag-focus", Width = 240, Height = 160 };
                ActivateFocusFixture(quick, other);
                quick.OpenPanel(); quick.UpdateLayout(); PumpUntil(() => HasForeground(quick), "expanded drag could not become foreground");
                EnterQuickMove(quick);
                var start = QuickHandleCenter(quick);
                Start();
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
