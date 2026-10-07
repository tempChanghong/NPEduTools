using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    // Real WPF templates and bindings, isolated from MainWindow, Host and any actual switch.
    private static void RunRemoteExamChecks()
    {
        var view = new RemoteExamHistoryView();
        Guid id = Guid.NewGuid();
        var history = new[]
        {
            new RemoteExamHistory(id, "PARTIAL", "SetStartup", "STARTUP_NOT_READY", DateTimeOffset.Now, null),
            new RemoteExamHistory(Guid.NewGuid(), "SUCCEEDED", "Verify", null, DateTimeOffset.Now, null, "Daily")
        };
        view.ShowHistory(history);
        var fixture = new Window
        {
            Title = "考试记录展示 · 隔离界面测试", Width = 680, Height = 720,
            Background = Brushes.White,
            Content = new ScrollViewer { Content = view, Padding = new Thickness(20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
        Exercise(fixture, window =>
        {
            window.UpdateLayout();
            var items = (ItemsControl)view.FindName("HistoryItems");
            Assert(items.Items.Count == 2, "history rows missing");
            var blocks = Descendants<TextBlock>(view).Where(x => x.IsVisible).Select(x => x.Text).ToArray();
            Assert(blocks.Any(x => x.Contains("切换未完成")), "partial title not rendered");
            Assert(blocks.Any(x => x.Contains("登录自启动设置尚未得到确认")), "reason not rendered");
            Assert(blocks.Any(x => x.Contains("返回日常") && x.Contains("历史回执")), "daily receipt not rendered");
            var expanders = Descendants<Expander>(view).ToArray();
            Assert(expanders.Length == 2 && expanders.All(x => !x.IsExpanded), "technical details open by default");
            Assert(!Descendants<TextBox>(view).Any(x => x.IsVisible), "raw detail visible without expanding");
            Snapshot(window, "exam-history.png");
            expanders[0].IsExpanded = true; window.UpdateLayout();
            var details = Descendants<TextBox>(expanders[0]).Single();
            Assert(details.IsReadOnly && details.IsVisible, "technical details not visible/read-only");
            Assert(details.Text.Contains(id.ToString()) && details.Text.Contains("STARTUP_NOT_READY"), "copyable detail binding missing");
            Snapshot(window, "exam-history-details.png");
            Checks.Add("Real history template renders partial cause and historical daily success; technical details are collapsed and copyable");

            view.ShowHistory([history[0] with { LocallyEndedAt = DateTimeOffset.Now }]); window.UpdateLayout();
            Assert(Descendants<TextBlock>(view).Any(x => x.IsVisible && x.Text.Contains("本机已解除该次远程录课暂停")), "locally ended evidence not rendered");
            Assert(Descendants<TextBlock>(view).Any(x => x.IsVisible && x.Text.Contains("切换未完成")), "local end overwrote original failure");
            view.ShowHistory([]); window.UpdateLayout();
            Assert(items.Items.Count == 0, "old rows survived empty refresh");
            Assert(((TextBlock)view.FindName("EmptyHistory")).IsVisible, "empty state missing");
            view.ShowHistory(null); window.UpdateLayout();
            Assert(((TextBlock)view.FindName("EmptyHistory")).Text.Contains("无法确认"), "unavailable history presented as no history");
            Checks.Add("Local pause clearance retains failure; empty/unavailable refresh removes stale rows and distinguishes unknown history");
            window.Close();
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
