using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NPEduTools.App;

internal static partial class Program
{
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
            void MouseEvent(RoutedEvent routed)
            {
                edge.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routed });
                // RaiseEvent has no native mouse position report. Capture refreshes the primary
                // device's source, so seed the synthetic down at that same position as the up.
                if (routed == UIElement.PreviewMouseLeftButtonDownEvent && ReferenceEquals(Mouse.Captured, edge))
                    typeof(QuickAccessWindow).GetMethod("BeginDrag", BindingFlags.NonPublic | BindingFlags.Instance)!
                        .Invoke(quick, [quick.PointToScreen(Mouse.GetPosition(quick))]);
            }
            void TouchEvent(FixtureTouch touch, RoutedEvent routed) => edge.RaiseEvent(new TouchEventArgs(touch, Environment.TickCount) { RoutedEvent = routed });
            MouseEvent(UIElement.PreviewMouseLeftButtonDownEvent);
            Assert(ReferenceEquals(Mouse.Captured, edge), "fixture failed to capture the handle");
            MouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            PumpUntil(() => panel.IsVisible, "mouse tap did not expand quick panel");
            Assert(!openedWhileCaptured, "quick panel resized/activated while its handle still owned pointer capture");
            Assert(Mouse.Captured is null, "completed mouse tap retained capture");
            quick.Collapse(false); quick.UpdateLayout();
            first = new(101, quick, new Point(30, 30)); second = new(102, quick, new Point(35, 35));
            TouchEvent(first, UIElement.PreviewTouchDownEvent);
            TouchEvent(second, UIElement.PreviewTouchDownEvent);
            TouchEvent(second, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed, "another finger completed the first finger's handle gesture");
            Assert(edge.TouchesCaptured.Any(t => t.Id == first.Id), "another finger stole first finger's capture");
            // A compatibility mouse event during the same touch must not steal/cancel its gesture.
            MouseEvent(UIElement.PreviewMouseLeftButtonDownEvent); MouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed, "compatibility mouse event toggled during a touch");
            TouchEvent(first, UIElement.PreviewTouchUpEvent);
            PumpUntil(() => panel.IsVisible, "single touch failed to expand panel after capture release");
            Assert(!edge.TouchesCaptured.Any() && !openedWhileCaptured, "completed touch retained capture while expanding");
            quick.Collapse(false); quick.UpdateLayout();
            first.MoveTo(new Point(30, 30)); TouchEvent(first, UIElement.PreviewTouchDownEvent);
            double gestureScale = (double)typeof(QuickAccessWindow).GetField("_gestureScale", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(quick)!;
            double windowScale = VisualTreeHelper.GetDpi(quick).DpiScaleY;
            first.MoveTo(new Point(30, 30 + 10 * gestureScale / windowScale)); TouchEvent(first, UIElement.PreviewTouchMoveEvent); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            PumpUntil(() => panel.IsVisible, "routed touch jitter did not click");
            quick.Collapse(false); quick.UpdateLayout();
            first.MoveTo(new Point(30, 30)); TouchEvent(first, UIElement.PreviewTouchDownEvent);
            first.MoveTo(new Point(30, 30 - 20 * gestureScale / windowScale)); TouchEvent(first, UIElement.PreviewTouchMoveEvent); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "routed touch drag clicks or retains capture");
            first.MoveTo(new Point(30, 30)); TouchEvent(first, UIElement.PreviewTouchDownEvent); edge.ReleaseTouchCapture(first); TouchEvent(first, UIElement.PreviewTouchUpEvent);
            Assert(panel.Visibility == Visibility.Collapsed && !edge.TouchesCaptured.Any(), "lost touch capture still clicks");
            quick.OpenPanel(); quick.UpdateLayout();
            Snapshot(quick, "quick-pointer-expanded.png");
            other = new Window { Title = "NPEduTools.Test.focus", Width = 240, Height = 160 };
            other.Show(); other.Activate(); quick.Activate();
            PumpUntil(() => quick.IsActive, "fixture could not reactivate the quick panel");
            Assert(panel.Visibility == Visibility.Visible, "transient activation change collapsed the panel after focus returned");
            other.Activate();
            PumpUntil(() => panel.Visibility == Visibility.Collapsed, "real focus loss did not dismiss quick panel");
            other.Close(); other = null;
            quick.Collapse(false);
            // An unfinished pointer gesture must not reopen the rail after full-screen suppression.
            MouseEvent(UIElement.PreviewMouseLeftButtonDownEvent); MouseEvent(UIElement.PreviewMouseLeftButtonUpEvent);
            quick.SetAutoHidden(true);
            PumpUntil(() => !quick.IsVisible, "full-screen suppression failed");
            quick.SetAutoHidden(false); quick.UpdateLayout();
            Assert(panel.Visibility == Visibility.Collapsed, "queued tap reopened a suppressed panel");
            // Actual physical work-area bounds with each supported density, including drag to the bottom.
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (double density in new[] { .8, .9, 1d })
            {
                quick.SetDensity(density); quick.SetCompact(true); quick.UpdateLayout();
                var screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(quick).Handle);
                var area = screen.WorkingArea; var start = quick.PointToScreen(new Point(30, 20));
                void Invoke(string name, params object[] args) => typeof(QuickAccessWindow).GetMethod(name, flags)!.Invoke(quick, args);
                Invoke("BeginDrag", start); Invoke("MoveHandle", new Point(start.X, area.Bottom - (start.Y - quick.PointToScreen(new Point(0, 0)).Y) - 10));
                // Move far past the lower edge to exercise clamping consistently across DPI/density.
                Invoke("MoveHandle", new Point(start.X, area.Bottom + 100)); quick.UpdateLayout();
                Assert(Math.Abs(quick.PointToScreen(new Point(0, quick.ActualHeight)).Y - area.Bottom) <= 2, "density-scaled drag did not reach work-area bottom");
                Invoke("CancelDrag");
            }
            Checks.Add("Real WPF routed mouse/touch: release capture before expansion, own one finger, ignore concurrent promoted mouse, cancel queued tap on full-hide, density drag bounds; synthetic events, no physical touchscreen");
        }
        finally
        {
            first?.Stop(); second?.Stop(); other?.Close(); quick.Shutdown();
            string ownPreference = (string)typeof(QuickAccessWindow).GetField("_settingsPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(quick)!;
            File.Delete(ownPreference); File.Delete(ownPreference + ".tmp");
        }
    }
}
