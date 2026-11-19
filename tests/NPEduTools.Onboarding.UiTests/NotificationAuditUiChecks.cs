using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationAuditChecks()
    {
        var failures = new List<Exception>();
        foreach (Action check in new Action[] { RunNotificationKeyboardPaging, RunSmallNotificationPreview })
        {
            try { check(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void RunNotificationKeyboardPaging()
    {
        string pipe = "NPEduTools.Test.keyboard-notice-paging." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(16));
        var main = IsolatedMainWindow(pipe);
        Task? polling = null;
        NoticeExchange Listen() => new(pipe, timeout.Token);
        NpepInboxState Page(int offset) => new(NoticeScope, "ONLINE", "隔离列表已同步。", 30,
            Enumerable.Range(offset + 1, 10).Select(i => new NpepNoticeSummary(
                $"page-item-{i}", 1, $"第 {i} 条通知", "MINOR", false, false)).ToArray(),
            NextOffset: offset < 20 ? offset + 10 : null);
        try
        {
            Exercise(main, _ =>
            {
                try
                {
                    ShowNotificationSettings(main);
                    var next = Button(main, "NotificationNext");
                    var previous = Button(main, "NotificationPrevious");
                    var selector = (ComboBox)main.FindName("NotificationPreviewPriority");
                    var range = (TextBlock)main.FindName("NotificationPageRange");
                    using var firstPage = Listen();
                    polling = (Task)typeof(MainWindow).GetMethod("NotificationPollAsync", NoticeMembers)!.Invoke(main, null)!;
                    firstPage.Expect(new("poll", Offset: 0));
                    firstPage.Reply(Page(0));
                    PumpUntil(() => next.IsEnabled, "initial notice page did not enable Next");
                    Assert(next.Focus(), "Next could not take focus");

                    using var secondPage = Listen();
                    SendNoticeKey(next, Key.Enter);
                    secondPage.Expect(new("poll", Offset: 10));
                    secondPage.Reply(Page(10));
                    PumpUntil(() => range.Text == "第 11–20 条，共 30 条", "second page did not render");
                    Assert(next.IsKeyboardFocused,
                        $"keyboard paging moved focus away from Next to {Keyboard.FocusedElement}");

                    using var thirdPage = Listen();
                    SendNoticeKey(next, Key.Enter);
                    thirdPage.Expect(new("poll", Offset: 20));
                    Assert(selector.Focus(), "fixture could not leave the pager while waiting");
                    thirdPage.Reply(Page(20));
                    PumpUntil(() => range.Text == "第 21–30 条，共 30 条", "third page did not render");
                    Assert(selector.IsKeyboardFocused, "arriving page stole focus after the user left the pager");

                    using var backPage = Listen();
                    Assert(previous.Focus(), "Previous could not take focus on the last page");
                    SendNoticeKey(previous, Key.Enter);
                    backPage.Expect(new("poll", Offset: 10));
                    backPage.Reply(Page(10));
                    PumpUntil(() => range.Text == "第 11–20 条，共 30 条", "previous page did not render");
                    Assert(previous.IsKeyboardFocused, "reverse keyboard paging did not retain Previous");
                    Checks.Add("Real inbox over the fake management pipe: Enter keeps the current paging direction, while a late page leaves focus on the preview selector if the user moved away");
                }
                finally { main.Close(); }
            });
        }
        finally
        {
            main.Close();
            if (polling is not null)
            {
                PumpUntil(() => polling.IsCompleted, "isolated keyboard paging did not stop");
                polling.GetAwaiter().GetResult();
            }
        }
    }

    private static void RunSmallNotificationPreview()
    {
        var main = IsolatedMainWindow("NPEduTools.Test.small-notification-preview." + Guid.NewGuid().ToString("N"));
        try
        {
            Exercise(main, _ =>
            {
                try
                {
                    ShowNotificationSettings(main);
                    ((ComboBox)main.FindName("NotificationPreviewPriority")).SelectedIndex = (int)SchoolNotificationPriority.Important;
                    Click(main, "NotificationPreviewButton");
                    var window = NoticeField<SchoolNotificationWindow>(main, "_notificationPreview")!;
                    window.Width = 320;
                    window.Height = 250;
                    window.UpdateLayout();
                    var grade = (TextBlock)window.FindName("NotificationHeader");
                    var brand = ((Grid)grade.Parent).Children.OfType<TextBlock>().First();
                    var scroll = (ScrollViewer)window.FindName("MessageScroll");
                    var close = Button(window, "DismissButton");
                    PumpUntil(() => Math.Abs(((System.Windows.Media.TranslateTransform)window.FindName("EntranceOffset")).Y) < .1,
                        "small preview entrance did not settle");
                    Snapshot(window, "detail-320x250.png");
                    Assert(brand.ActualHeight <= 2 * brand.FontSize + 2 && grade.Text.Contains("重要通知") &&
                        scroll.ViewportHeight >= 36 && close.ActualHeight >= 64,
                        $"320×250 preview left {scroll.ViewportHeight:F1} DIP for the body; brand height {brand.ActualHeight:F1} DIP");
                    var target = close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), window);
                    Assert(window.InputHitTest(target) is DependencyObject hit && (close == hit || close.IsAncestorOf(hit)),
                        "small preview close action was outside the hit-testable viewport");
                    Click(window, "DismissButton");
                    PumpUntil(() => !window.IsVisible && window.Backdrops.Count == 0, "small preview did not close cleanly");
                    Checks.Add("Real Important preview at 320×250 DIP: readable school and grade header, scrollable body, reachable 64-DIP close action");
                }
                finally { main.Close(); }
            });
        }
        finally { main.Close(); }
    }
}
