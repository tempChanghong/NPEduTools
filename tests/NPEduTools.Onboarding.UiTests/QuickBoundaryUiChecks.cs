using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;

internal static partial class Program
{
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
                other.Show(); other.Activate(); PumpUntil(() => other.IsActive, "focus fixture could not become foreground");
                quick.OpenPanel(); PumpUntil(() => quick.IsActive, "quick panel could not become foreground");
                Click(quick, "RailFold"); Settle();
                Assert(other.IsActive && !quick.IsActive, "folded rail retained keyboard focus");
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
