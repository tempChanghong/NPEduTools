using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NPEduTools.App;

internal static partial class Program
{
    private static Point QuickHandleCenter(QuickAccessWindow quick)
    {
        var edge = Button(quick, "EdgeHandle");
        return edge.TranslatePoint(new Point(edge.ActualWidth / 2, edge.ActualHeight / 2), quick);
    }

    private static void EnterQuickMove(QuickAccessWindow quick)
    {
        var panel = (FrameworkElement)quick.FindName("PanelSurface");
        if (!panel.IsVisible) quick.OpenPanel();
        if (!Button(quick, "LayoutMove").IsVisible) Click(quick, "LayoutButton");
        Assert(Button(quick, "LayoutMove").IsVisible, "the move action is not available");
        Click(quick, "LayoutMove");
        quick.UpdateLayout();
        Assert(!panel.IsVisible && ((FrameworkElement)quick.FindName("MoveHint")).IsVisible,
            "the explicit move action did not close the panel and show its instructions");
    }

    private sealed class FixtureTouch : TouchDevice
    {
        private Point _point;
        public FixtureTouch(int id, Window window, Point point) : base(id)
        { _point = point; SetActiveSource(PresentationSource.FromVisual(window)); Activate(); }
        public override TouchPoint GetTouchPoint(IInputElement? relativeTo) => new(this, _point, new Rect(_point, new Size(1, 1)), TouchAction.Move);
        public override TouchPointCollection GetIntermediateTouchPoints(IInputElement? relativeTo) => [GetTouchPoint(relativeTo)];
        public void MoveTo(Point point) => _point = point;
        public void Stop() { if (IsActive) Deactivate(); }
    }

    private static void RunQuickPointerChecks()
    {
        string pipe = "NPEduTools.Test.quick-pointer." + Guid.NewGuid().ToString("N");
        var quick = new QuickAccessWindow(pipe, () => { }, () => { }, () => { }, () => { }, _ => { }, () => { }, () => { });
        FixtureTouch? first = null, second = null;
        Window? other = null;
        try
        {
            quick.Show(); quick.UpdateLayout();
            var edge = (Button)quick.FindName("EdgeHandle"); var panel = (FrameworkElement)quick.FindName("PanelSurface");
            bool openedWhileCaptured = false;
            panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) openedWhileCaptured |= ReferenceEquals(Mouse.Captured, edge) || edge.TouchesCaptured.Any(); };
            void MouseEvent(RoutedEvent routed) =>
                edge.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed });
            void TouchEvent(FixtureTouch touch, RoutedEvent routed) => edge.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = routed });
            ClickFixturePoint(new System.Windows.Interop.WindowInteropHelper(quick).Handle,
                quick.PointToScreen(QuickHandleCenter(quick)), QuickForeground(),
                () => PumpUntil(() => panel.IsVisible, "mouse tap did not expand quick panel"));
            Assert(!openedWhileCaptured, "quick panel resized/activated while its handle still owned pointer capture");
            Assert(Mouse.Captured is null, "completed mouse tap retained capture");
            quick.Collapse(false); quick.UpdateLayout();
            first = new(101, quick, QuickHandleCenter(quick)); second = new(102, quick, QuickHandleCenter(quick));
            TouchEvent(first, UIElement.PreviewTouchDownEvent);
            TouchEvent(second, UIElement.PreviewTouchDownEvent);
            TouchEvent(second, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed, "another finger completed the first finger's handle gesture");
            Assert(edge.TouchesCaptured.Any(t => t.Id == first.Id), "another finger stole first finger's capture");
            var secondDownOnDraw = new TouchEventArgs(second, Environment.TickCount) { RoutedEvent = UIElement.PreviewTouchDownEvent };
            Button(quick, "RailSecRandom").RaiseEvent(secondDownOnDraw);
            Assert(secondDownOnDraw.Handled, "the second finger reached a neighboring action during handle capture");
            // A compatibility mouse event during the same touch must not steal/cancel its gesture.
            MouseEvent(UIElement.PreviewMouseLeftButtonDownEvent); MouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed, "compatibility mouse event toggled during a touch");
            TouchEvent(first, UIElement.PreviewTouchUpEvent);
            PumpUntil(() => panel.IsVisible, "single touch failed to expand panel after capture release");
            Assert(!edge.TouchesCaptured.Any() && !openedWhileCaptured, "completed touch retained capture while expanding");
            quick.Collapse(false); quick.UpdateLayout();
            var center = QuickHandleCenter(quick);
            first.MoveTo(center); TouchEvent(first, UIElement.PreviewTouchDownEvent);
            first.MoveTo(center + new Vector(0, 14)); TouchEvent(first, UIElement.PreviewTouchMoveEvent); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            PumpUntil(() => panel.IsVisible, "routed touch jitter did not click");
            quick.Collapse(false); quick.UpdateLayout();
            center = QuickHandleCenter(quick);
            first.MoveTo(center); TouchEvent(first, UIElement.PreviewTouchDownEvent);
            first.MoveTo(edge.TranslatePoint(new Point(edge.ActualWidth / 2, edge.ActualHeight + 2), quick));
            TouchEvent(first, UIElement.PreviewTouchMoveEvent); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "routed touch drag clicks or retains capture");
            first.MoveTo(center); TouchEvent(first, UIElement.PreviewTouchDownEvent); edge.ReleaseTouchCapture(first); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "lost touch capture still clicks");
            quick.OpenPanel(); quick.UpdateLayout();
            Snapshot(quick, "quick-pointer-expanded.png");
            other = new Window { Title = "NPEduTools.Test.focus", Width = 240, Height = 160 };
            ActivateFocusFixture(quick, other);
            quick.OpenPanel(); PumpUntil(() => HasForeground(quick), "could not prepare transient foreground change");
            TraceQuickFocus(quick, other, "transient focus", other.Activate());
            TraceQuickFocus(quick, other, "reactivate open panel", quick.Activate());
            PumpUntil(() => HasForeground(quick), "fixture could not reactivate the quick panel");
            Assert(panel.Visibility == Visibility.Visible, "transient activation change collapsed the panel after focus returned");
            other.Activate();
            PumpUntil(() => panel.Visibility == Visibility.Collapsed, "real focus loss did not dismiss quick panel");
            other.Close(); other = null;
            quick.Collapse(false);
            // An unfinished pointer gesture must not reopen the rail after full-screen suppression.
            first.MoveTo(QuickHandleCenter(quick));
            TouchEvent(first, UIElement.PreviewTouchDownEvent); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            quick.SetAutoHidden(true);
            PumpUntil(() => !quick.IsVisible, "full-screen suppression failed");
            quick.SetAutoHidden(false); quick.UpdateLayout();
            Assert(panel.Visibility == Visibility.Collapsed, "queued tap reopened a suppressed panel");
            // Actual physical work-area bounds with each supported density, including drag to the bottom.
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (double density in new[] { .8, .9, 1d })
            {
                quick.SetDensity(density); quick.SetCompact(true); EnterQuickMove(quick);
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(quick).Handle);
                var area = screen.WorkingArea; var start = quick.PointToScreen(QuickHandleCenter(quick));
                void Invoke(string name, params object[] args) => typeof(QuickAccessWindow).GetMethod(name, flags)!.Invoke(quick, args);
                Invoke("BeginHandlePress", start); Invoke("TrackHandlePress", new Point(start.X, area.Bottom - (start.Y - quick.PointToScreen(new Point(0, 0)).Y) - 10));
                // Move far past the lower edge to exercise clamping consistently across DPI/density.
                Invoke("TrackHandlePress", new Point(start.X, area.Bottom + 100)); quick.UpdateLayout();
                Assert(Math.Abs(quick.PointToScreen(new Point(0, quick.ActualHeight)).Y - area.Bottom) <= 2, "density-scaled drag did not reach work-area bottom");
                Invoke("CancelHandlePress");
            }
            Checks.Add("Actual HWND mouse and simulated WPF touch: release capture before expansion, own one finger, ignore concurrent mouse input, cancel outside/late up, and clamp explicit movement at each density");
        }
        finally
        {
            first?.Stop(); second?.Stop(); other?.Close(); quick.Shutdown();
            string ownPreference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(quick)!;
            File.Delete(ownPreference); File.Delete(ownPreference + ".tmp");
        }
    }
}
