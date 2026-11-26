using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNotificationReaderChecks()
    {
        string pipe = "NPEduTools.Test.notice-reader." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var main = IsolatedMainWindow(pipe);
        var study = new StudyWindow(pipe, () => { }, muted: true, time: new StudyUiClock(), manual: true) { ShowActivated = false, Topmost = false };
        typeof(MainWindow).GetField("_manualStudyWindow", NoticeMembers)!.SetValue(main, study);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string paragraph = "请在图书馆服务台办理借阅与归还。各班可安排同学按小组依次前往，归还前核对书籍及随书材料。\n";
        var notice = new NpepNotice("library-notice", 4, "本周图书借阅与归还安排",
            string.Concat(Enumerable.Repeat(paragraph, 55)) + "以上为本周安排。", "MINOR", now, now.AddHours(2), "学校图书馆", false);
        var rows = Enumerable.Range(0, 10).Select(i => new NpepNoticeSummary("notice-" + i, 1, $"校内第 {i + 11} 条通知", "MINOR", false, false)).ToArray();
        rows[7] = new(notice.PublicationId, notice.Revision, notice.Title, notice.Priority, false, false);
        var closed = new NpepNotice("closed-notice", 2, "已处理的教务提醒", "课前请到教务处领取签到表。", "NORMAL",
            now, now.AddHours(2), "学校教务处", true);
        rows[0] = new(closed.PublicationId, closed.Revision, closed.Title, closed.Priority, true, true);
        var inbox = new NpepInboxState(NoticeScope, "ONLINE", "通知已同步。", 23, rows, NextOffset: 20, CanPresent: true);
        NpepNotificationCommand ForNotice(NpepNotice item) => new("get", NoticeScope, item.PublicationId, item.Revision);
        try
        {
            Exercise(main, _ =>
            {
                study.Apply(StudyFixture(), null);
                study.ShowDisplay();
                Assert(study.IsVisible, "isolated active study fixture was not displaying");
                main.Activate();
                main.Width = 1040; main.Height = 760;
                ShowNotificationSettings(main, expandPreview: false);
                Assert(!((Expander)main.FindName("NotificationPreviewExpander")).IsExpanded, "style preview replaces the main task by default");
                typeof(MainWindow).GetField("_inboxOffset", NoticeMembers)!.SetValue(main, 10);
                ApplyNoticePage(main, inbox);
                var listPane = (FrameworkElement)main.FindName("NotificationListPane");
                var listScroll = (ScrollViewer)main.FindName("NotificationListScroll");
                var readerPane = (FrameworkElement)main.FindName("NotificationReaderPane");
                var readerScroll = (ScrollViewer)main.FindName("NotificationReaderScroll");
                var body = (TextBlock)main.FindName("NotificationReaderBody");
                var status = (TextBlock)main.FindName("NotificationReaderStatus");
                var title = (TextBlock)main.FindName("NotificationReaderTitle");
                var meta = (TextBlock)main.FindName("NotificationReaderMeta");
                var back = Button(main, "NotificationReaderBack");
                var row = NoticeRow(main, 7);
                row.BringIntoView(); main.UpdateLayout();
                Assert(row.Focus(), "notice row could not be focused before opening");
                using (var first = new NoticeExchange(pipe, deadline.Token))
                {
                    row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    first.Expect(ForNotice(notice));
                    Assert(study.IsVisible && VisibleNotices() == 0 && !NoticeField<bool>(main, "_notificationOpening"),
                        "reading a listed notice interrupts the classroom or takes the classroom presentation gate");
                    first.Reply(inbox with { Current = notice });
                    PumpUntil(() => body.Text.EndsWith("以上为本周安排。"), "verified note was not rendered in the reader");
                }
                Assert(main.ActualWidth == 1040 && main.ActualHeight == 760 && listPane.IsVisible && readerPane.IsVisible && Grid.GetColumn(readerPane) == 1 &&
                    Window.GetWindow(body) == main && meta.Text.Contains("学校图书馆") && meta.Text.Contains("仅在列表显示") && title.Text == notice.Title,
                    "standard width did not show the verified details beside their list in the same window");
                Snapshot(main, "notification-reader-1040-760.png");

                main.Width = 840; main.Height = 600; main.UpdateLayout();
                PumpUntil(() => main.ActualWidth == 840 && main.ActualHeight == 600, "compact main window did not resize");
                Snapshot(main, "notification-reader-840-600.png");
                var workspace = (FrameworkElement)main.FindName("NotificationWorkspace");
                Assert(main.ActualWidth == 840 && main.ActualHeight == 600 && !listPane.IsVisible && back.IsVisible &&
                    Grid.GetColumn(readerPane) == 0 && title.Text == notice.Title && readerScroll.ViewportHeight > 120,
                    $"compact layout body width {workspace.ActualWidth}, list {listPane.IsVisible}, return {back.IsVisible}, column {Grid.GetColumn(readerPane)}, scroll viewport {readerScroll.ViewportHeight}, title '{title.Text}'");
                var backPosition = back.TranslatePoint(new(back.ActualWidth / 2, back.ActualHeight / 2), main);
                Assert(main.InputHitTest(backPosition) is DependencyObject hit && (hit == back || back.IsAncestorOf(hit)),
                    "the narrow return action is clipped or covered");
                readerScroll.ScrollToEnd(); main.UpdateLayout();
                Assert(readerScroll.VerticalOffset > 0 && back.TranslatePoint(new(back.ActualWidth / 2, back.ActualHeight / 2), main) == backPosition,
                    "long notice scrolled away the fixed return action");
                Click(main, "NotificationReaderBack");
                PumpUntil(() => NoticeRow(main, 7).IsKeyboardFocused, "return did not focus the original notice row");
                Assert(listPane.IsVisible && !readerPane.IsVisible && listScroll.VerticalOffset > 0 &&
                    ((TextBlock)main.FindName("NotificationPageRange")).Text == "第 11–20 条，共 23 条" && study.IsVisible,
                    "return lost page, list position or the active classroom display");
                Snapshot(main, "notification-return-840-600.png");
                Checks.Add("Real main-window inbox at 1040x760 and 840x600: verified long body, unchanged study presentation and no receipt commands; narrow return restores the page and original row");

                using (var history = new NoticeExchange(pipe, deadline.Token))
                {
                    NoticeRow(main, 0).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    history.Expect(ForNotice(closed)); history.Reply(inbox with { Current = closed });
                    PumpUntil(() => body.Text == closed.Content, "already closed notice did not use inline reading");
                    Assert(meta.Text.Contains("已手动关闭") && study.IsVisible && VisibleNotices() == 0,
                        "reopening history changed its delivery state or reopened a classroom popup");
                    Click(main, "NotificationReaderBack"); main.UpdateLayout();
                }

                using (var again = new NoticeExchange(pipe, deadline.Token))
                {
                    NoticeRow(main, 7).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    again.Expect(ForNotice(notice)); again.Reply(inbox with { Current = notice });
                    PumpUntil(() => body.Text.Length > 0, "notification did not reopen");
                }
                using (var recheck = new NoticeExchange(pipe, deadline.Token))
                {
                    ApplyNoticePage(main, inbox);
                    recheck.Expect(ForNotice(notice));
                    recheck.Reply(inbox with { Current = null });
                    PumpUntil(() => body.Text.Length == 0 && status.Text.Contains("已变化"), "withdrawn body stayed visible after exact recheck");
                }
                Assert(back.IsVisible && title.Text.Length == 0 && meta.Text.Length == 0 && study.IsVisible &&
                    !NoticeField<bool>(main, "_notificationOpening") && VisibleNotices() == 0,
                    "withdrawal leaked old fields, obscured back, or promoted manual reading to classroom delivery");
                Snapshot(main, "notification-withdrawn-840-600.png");
                Click(main, "NotificationReaderBack"); main.UpdateLayout();
                using (var old = new NoticeExchange(pipe, deadline.Token))
                {
                    NoticeRow(main, 7).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    old.Expect(ForNotice(notice));
                    var other = new NpepNoticeSummary(notice.PublicationId, notice.Revision, "另一学校的同号通知", "MINOR", false, false);
                    ApplyNoticePage(main, inbox with { Scope = new string('b', 64), Items = [other], Total = 11, NextOffset = null });
                    try { old.Reply(inbox with { Current = notice }); }
                    catch (IOException) { /* old read is cancelled on a school change */ }
                    PumpUntil(() => NoticeField<CancellationTokenSource>(main, "_notificationReaderRead") is null, "old school read did not clear");
                    Assert(body.Text.Length == 0 && status.Text.Contains("范围已变化"), "late call exposed another school's notice");
                    Click(main, "NotificationReaderBack");
                    Assert((NoticeRow(main, 0).Tag as NpepNoticeSummary) == other, "return did not show the new scoped list");
                }
                Checks.Add("Reader invalidation cancels obsolete reads, clears title, metadata and body after withdrawal or school change, and leaves the classroom display visible");
                main.Close();
            });
        }
        finally { main.Close(); }
    }
}
