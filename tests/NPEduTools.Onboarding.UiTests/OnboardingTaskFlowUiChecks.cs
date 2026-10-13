using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static IEnumerable<T> FlowElements<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FlowElements<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void AssertFlowFooter(OnboardingWindow window)
    {
        foreach (string name in new[] { "OobeLater", "NextButton", "BackButton", "SkipButton" })
        {
            var button = name == "OobeLater" ? (Button)Find(window, name) : (Button)window.FindName(name);
            if (button.Visibility != Visibility.Visible) continue;
            var rect = button.TransformToAncestor(window).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Assert(rect.Left >= 0 && rect.Right <= window.ActualWidth + 1 && rect.Top >= 0 && rect.Bottom <= window.ActualHeight + 1,
                "OOBE navigation is outside the available window: " + name);
        }
        Assert(((ScrollViewer)window.FindName("PageScroll")).ViewportHeight > 30, "OOBE page has no usable viewport");
    }
    private static void RunOnboardingTaskFlowChecks()
    {
        const OnboardingFeatures all = (OnboardingFeatures)255;
        var saves = new List<OnboardingState>();
        string pipe = "NPEduTools.Test.oobe-flow." + Guid.NewGuid().ToString("N");
        var welcome = PreparationWindow(pipe, new(Features: OnboardingFeatures.None), saves.Add);
        Exercise(welcome, window =>
        {
            ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(1, 1);
            window.Width = 900; window.Height = 700; window.UpdateLayout();
            var groups = (Grid)window.FindName("PurposeGroups");
            Assert(Grid.GetColumn((Border)window.FindName("RecordingPurpose")) == 1, "wide purpose groups did not use two columns");
            Snapshot(window, "oobe-welcome-wide.png");
            window.Width = 560; window.Height = 500; window.UpdateLayout();
            Assert(Grid.GetColumn((Border)window.FindName("RecordingPurpose")) == 0 && Grid.GetRow((Border)window.FindName("ToolsPurpose")) == 3,
                "narrow purpose groups did not become a single column");
            AssertFlowFooter((OnboardingWindow)window); Snapshot(window, "oobe-welcome-narrow.png");
            foreach (string choice in new[] { "UseNoise", "UseSchool", "UseRecording", "UseAutomatic", "UseClassroom", "UseShortcuts", "UseTouch", "UseSecRandom" })
                Check(window, choice).IsChecked = true;
            Assert(saves.Count == 0, "choosing purposes wrote configuration before navigation");
            Assert(((ItemsControl)window.FindName("StepList")).Items.Count == 4, "full selection did not use compact stage navigation");
            Click(window, "NextButton");
            Assert(saves.Last().Features == all && saves.Last().Step == "preferences", "grouping changed existing feature selection semantics");
            Click(window, "BackButton");
            Assert(Check(window, "UseSchool").IsChecked == true && Check(window, "UseAutomatic").IsChecked == true, "back navigation lost selected purposes");
            foreach (var checkbox in new[] { "UseNoise", "UseSchool", "UseRecording", "UseAutomatic", "UseClassroom", "UseShortcuts", "UseTouch", "UseSecRandom" }) Check(window, checkbox).IsChecked = false;
            Assert(((ItemsControl)window.FindName("StepList")).Items.Count == 3, "no-purpose setup contains a phantom preparation stage");
            Click(window, "NextButton");
            Assert(saves.Last().Features == OnboardingFeatures.None, "all purposes cannot be deselected");
            window.Close();
        });
        Checks.Add("OOBE grouped choices preserve every feature, retain back navigation and allow none; wide/narrow layouts keep navigation visible");

        saves.Clear();
        var review = PreparationWindow(pipe + ".review", new(Features: OnboardingFeatures.School | OnboardingFeatures.Noise, Step: "review",
            Reviewed: ["preferences"], Skipped: ["school", "noise"]), saves.Add);
        Exercise(review, window =>
        {
            window.UpdateLayout();
            var items = (ItemsControl)window.FindName("ReviewItems");
            Button Action(string step) => FlowElements<Button>(items).Single(b => b.Tag as string == step);
            PumpUntil(() => FlowElements<Button>(items).Any(b => b.Tag as string == "preferences"), "review actions were not rendered");
            var rows = items.ItemsSource;
            var refresh = typeof(OnboardingWindow).GetMethod("RefreshReviewItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
            refresh.Invoke(window, null); Assert(ReferenceEquals(rows, items.ItemsSource), "unchanged status rebuilds review controls and loses focus");
            Action("preferences").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(saves.Last().Step == "preferences", "review shortcut did not open the selected step");
            Click(window, "NextButton");
            Assert(saves.Last().Step == "review" && !saves.Last().Completed, "editing from review started the whole walkthrough again or completed prematurely");
            window.UpdateLayout(); Action("noise").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(((TextBlock)window.FindName("StepTitle")).Text == "准备自习设备", "study preparation label is inconsistent");
            Snapshot(window, "oobe-study-devices.png");
            Click(window, "SkipButton"); Assert(saves.Last().Step == "review", "skip from review did not return to the summary");
            window.UpdateLayout(); Action("school").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            var connection = (NpepConnectionControl)window.FindName("SchoolConnection");
            Assert(!((Expander)connection.FindName("WebPairingOptions")).IsExpanded && !((Expander)connection.FindName("ServerIdentityDetails")).IsExpanded,
                "guided pairing exposes optional codes and technical IDs by default");
            Snapshot(window, "oobe-school-connection.png");
            Click(window, "BackButton"); Assert(saves.Last().Step == "review", "back from review edit did not preserve summary navigation");
            // Match the existing bounds suite's 2560x1440 / 200% simulation without changing Windows DPI.
            var density = typeof(MainWindow).Assembly.GetType("NPEduTools.App.DesktopDensity")!.GetMethod("Resolve")!;
            double scale = (double)density.Invoke(null, [0d, 1280d, 680d, 900d, 720d])!;
            ((FrameworkElement)window.Content).LayoutTransform = new ScaleTransform(scale, scale);
            window.Width = 900 * scale; window.Height = 720 * scale; window.UpdateLayout();
            Assert(window.ActualWidth <= 1264 && window.ActualHeight <= 664, "OOBE cannot fit the simulated 200% work area");
            AssertFlowFooter((OnboardingWindow)window); Snapshot(window, "oobe-review-simulated-200.png");
            Assert(((TextBlock)window.FindName("ReviewText")).Text.Contains("稍后配置"), "skipped preparation is misreported as complete");
            Snapshot(window, "oobe-review.png");
            window.Close();
        });
        Checks.Add("OOBE review actions return directly to summary, preserve skips, do not replace unchanged rows and fit simulated 200% work area");

        saves.Clear();
        bool failSave = true;
        var requests = new List<HostRequest>();
        NpepState school = new(1, "PENDING", "WAITING", "隔离申请等待管理员批准。", Origin: "https://school.example");
        var session = new NpepConnectionSession((request, _) => {
            requests.Add(request);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离状态", Npep: school));
        });
        var waiting = PreparationWindow(pipe + ".waiting", new(Features: OnboardingFeatures.School, Step: "review", Reviewed: ["preferences"], Skipped: ["school", "noise"]),
            state => { if (failSave) throw new System.IO.IOException("isolated save failure"); saves.Add(state); }, session);
        Exercise(waiting, window => {
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            var items = (ItemsControl)window.FindName("ReviewItems");
            Assert(FlowElements<TextBlock>(items).Any(t => t.Text.Contains("等待管理员批准")), "pending school status missing from review");
            Snapshot(window, "oobe-review-pending.png");
            var action = FlowElements<Button>(items).Single(b => b.Tag as string == "preferences");
            action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(((TextBlock)window.FindName("StepTitle")).Text == "核对准备情况" && saves.Count == 0,
                "failed progress write changed the current page");
            Assert(((TextBlock)window.FindName("ErrorText")).Text.Contains("未保存"), "failed progress write did not tell the user");
            failSave = false;
            action.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            failSave = true; Click(window, "NextButton");
            Assert(((TextBlock)window.FindName("StepTitle")).Text == "使用偏好", "failed edit save returned to review prematurely");
            failSave = false; Click(window, "NextButton"); Assert(saves.Last().Step == "review", "retry lost the return-to-review path");
            int savedCount = saves.Count;
            school = school with { State = "ACTIVE", Connection = "ONLINE" };
            session.RefreshAsync().GetAwaiter().GetResult(); window.UpdateLayout();
            Assert(FlowElements<TextBlock>(items).Any(t => t.Text.Contains("当前在线")), "live school status did not refresh in review");
            Assert(saves.Count == savedCount && requests.All(r => r.Capability == "npep.status" && r.Npep is null),
                "review status refresh completed onboarding or mutated school settings");
            window.Close();
        });
        Checks.Add("OOBE pending/live school review updates are read-only; failed progress writes preserve page and retry returns to review");
    }
}
