using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private const BindingFlags NoticeMembers = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly string NoticeScope = new('a', 64);

    private static void RunNotificationWorkspaceChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            RunNotificationAuditChecks();
            RunNotificationPresentationChecks();
            RunLongNotificationChecks();
            RunNotificationEntryOrdering(previewFirst: false, stale: false);
            RunNotificationEntryOrdering(previewFirst: false, stale: true);
            RunNotificationEntryOrdering(previewFirst: false, stale: false, leaveInbox: true);
            RunNotificationEntryOrdering(previewFirst: true, stale: false);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void ShowNotificationSettings(MainWindow main)
    {
        // Isolate the notification cards from unrelated connection and remote-exam services.
        ((FrameworkElement)main.FindName("HomePage")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)main.FindName("SettingsPage")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("GeneralSettingsPanel")).Visibility = Visibility.Collapsed;
        var panel = (StackPanel)main.FindName("NpepSettingsPanel");
        panel.Visibility = Visibility.Visible;
        var inbox = (FrameworkElement)main.FindName("NotificationInboxCard");
        var preview = (FrameworkElement)main.FindName("NotificationPreviewActions");
        foreach (FrameworkElement child in panel.Children)
            child.Visibility = child == inbox || child.IsAncestorOf(preview) ? Visibility.Visible : Visibility.Collapsed;
        main.UpdateLayout();
    }

    private static void ApplyNoticePage(MainWindow main, NpepInboxState state) =>
        typeof(MainWindow).GetMethod("ApplyNotificationInbox", NoticeMembers)!.Invoke(main, [state]);

    private static Button NoticeRow(MainWindow main, int index)
    {
        var list = (ItemsControl)main.FindName("NotificationInboxItems");
        list.UpdateLayout();
        var presenter = (ContentPresenter)list.ItemContainerGenerator.ContainerFromIndex(index)!;
        return (Button)list.ItemTemplate.FindName("NotificationInboxRow", presenter);
    }

    private static T? NoticeField<T>(MainWindow main, string field) =>
        (T?)typeof(MainWindow).GetField(field, NoticeMembers)!.GetValue(main);

    private static int VisibleNotices() => Application.Current.Windows.OfType<SchoolNotificationWindow>().Count(window => window.IsVisible);

    private static int DesktopScreenCount()
    {
        var screen = Type.GetType("System.Windows.Forms.Screen, System.Windows.Forms")!;
        return ((Array)screen.GetProperty("AllScreens")!.GetValue(null)!).Length;
    }

    private static void SendNoticeKey(UIElement focus, Key key) =>
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(focus)!,
            Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });

    private static bool SameBrush(Brush actual, Brush expected) =>
        actual is SolidColorBrush left && expected is SolidColorBrush right && left.Color == right.Color;

    private static void RunNotificationPresentationChecks()
    {
        var main = IsolatedMainWindow("NPEduTools.Test.notification-presentation." + Guid.NewGuid().ToString("N"));
        try
        {
            Exercise(main, _ =>
            {
                Click(main, "HomeSchoolConnection", automation: true);
                ShowNotificationSettings(main);
                main.Width = 1040;
                main.Height = 760;
                var rows = new NpepNoticeSummary[]
                {
                    new("a", 1, "周五社团活动改到教学楼二层", "URGENT", true, false),
                    new("b", 1, "请在课前带齐实验记录和护目镜", "IMPORTANT", true, false),
                    new("c", 1, "下午第一节课在本班教室集合", "NORMAL", true, true),
                    new("d", 1, "本周图书借阅与归还时间", "MINOR", false, false)
                };
                var state = new NpepInboxState(NoticeScope, "ONLINE", "通知已同步。", rows.Length, rows);
                var list = (ItemsControl)main.FindName("NotificationInboxItems");
                var status = (TextBlock)main.FindName("NotificationInboxMessage");
                var range = (TextBlock)main.FindName("NotificationPageRange");
                var combo = (ComboBox)main.FindName("NotificationPreviewPriority");
                var previewButton = Button(main, "NotificationPreviewButton");
                var scroll = (ScrollViewer)main.FindName("SettingsScroll");
                ApplyNoticePage(main, state);
                main.UpdateLayout();
                Assert(range.Text == "第 1–4 条，共 4 条" && (combo.SelectedItem as ComboBoxItem)?.Tag as string == "Normal",
                    "verified range or default preview level is incorrect");
                Assert(NoticeRow(main, 0).Focus(), "inbox row could not take keyboard focus");
                var focusedRow = NoticeRow(main, 0);
                ApplyNoticePage(main, state with { Items = rows.ToArray() });
                Assert(ReferenceEquals(focusedRow, NoticeRow(main, 0)) && focusedRow.IsKeyboardFocused,
                    "unchanged poll replaced or unfocused a row");
                ApplyNoticePage(main, state with { Items = [rows[1], rows[0], rows[2], rows[3]] });
                Assert(NoticeRow(main, 1).IsKeyboardFocused &&
                    (NoticeRow(main, 1).Tag as NpepNoticeSummary)?.PublicationId == "a", "reordered page lost the focused publication");
                ApplyNoticePage(main, state);
                scroll.ScrollToTop();
                main.UpdateLayout();
                Assert((string?)list.Tag != "compact" && Grid.GetRow(previewButton) == 0, "standard window used compact notice layout");
                Snapshot(main, "inbox-standard.png");
                previewButton.BringIntoView(); main.UpdateLayout();
                Snapshot(main, "inbox-standard-preview.png");

                main.MinWidth = main.MinHeight = 0;
                main.Width = 760; main.Height = 520;
                previewButton.BringIntoView();
                main.UpdateLayout();
                Assert((string?)list.Tag == "compact" && Grid.GetRow(previewButton) == 1,
                    $"narrow notification card did not reflow (window {main.ActualWidth}, list {list.ActualWidth}, preview row {Grid.GetRow(previewButton)})");
                Assert(combo.ActualHeight >= 44 && previewButton.ActualHeight >= 44 && NoticeRow(main, 0).ActualHeight >= 44,
                    "narrow controls are smaller than the intended touch targets");
                Snapshot(main, "inbox-narrow-preview.png");
                scroll.ScrollToTop(); main.UpdateLayout();
                Snapshot(main, "inbox-narrow-list.png");

                ApplyNoticePage(main, state with { Connection = "STALE", Message = "正在重新核对学校连接。" });
                Assert(range.Text == "缓存列表第 1–4 条" && status.Text.Contains("在线核对"),
                    "cached rows were presented as a verified total");
                Snapshot(main, "inbox-cached.png");
                ApplyNoticePage(main, state with { Connection = "DISABLED" });
                Assert(list.Items.Count == 0 && range.Text.Length == 0 && status.Text.Contains("已停用") &&
                    !Button(main, "NotificationPrevious").IsEnabled && !Button(main, "NotificationNext").IsEnabled &&
                    status.IsKeyboardFocused, "disabling the inbox left actionable old rows, a range or no local focus");
                Snapshot(main, "inbox-disabled.png");
                ApplyNoticePage(main, state with { Scope = null, Connection = "OFFLINE", Message = "通知尚未同步。" });
                Assert(list.Items.Count == 0 && range.Text.Length == 0 && status.Text.Contains("尚未同步"), "unscoped result exposed old rows");
                ApplyNoticePage(main, state with { Total = 0, Items = [] });
                Assert(status.Text == "当前没有有效通知。" && range.Text.Length == 0, "verified empty state lost its own copy");
                Snapshot(main, "inbox-empty.png");
                Checks.Add("Real inbox controls: trusted ranges, unchanged and reordered focus, cache without trusted total, disabled/unscoped/verified empty states, two viewport layouts and 44-DIP controls");

                combo.BringIntoView(); main.UpdateLayout();
                Assert(combo.Focus(), "native level selector could not take focus");
                SendNoticeKey(combo, Key.Down);
                Assert(combo.SelectedIndex == 2 && VisibleNotices() == 0, "Down did not select Important independently of preview");
                var theme = typeof(SchoolNotificationWindow).GetMethod("ApplyNotificationVisualTheme", NoticeMembers)!;
                foreach (var priority in Enum.GetValues<SchoolNotificationPriority>())
                {
                    combo.SelectedIndex = (int)priority;
                    Click(main, "NotificationPreviewButton");
                    var notice = NoticeField<SchoolNotificationWindow>(main, "_notificationPreview")!;
                    Assert(notice.IsVisible && notice.Notification is { IsPreview: true } && notice.Notification.Priority == priority &&
                        notice.Backdrops.Count == (notice.RequiresResponse ? DesktopScreenCount() : 0) && VisibleNotices() == 1,
                        $"actual preview action did not show one {priority} window with the correct backdrops");
                    var body = (ScrollViewer)notice.FindName("MessageScroll");
                    var close = Button(notice, "DismissButton");
                    if (notice.RequiresResponse) PumpUntil(() => body.IsKeyboardFocused, $"{priority} preview did not focus the reading area");
                    Assert(close.ActualHeight >= 64 && close.ActualWidth >= 196 && !close.IsKeyboardFocused,
                        $"{priority} close target is too small or steals initial focus");
                    if (priority == SchoolNotificationPriority.Normal)
                    {
                        Assert(close.Focus(), "close button could not take focus before backdrop touch");
                        var backdrop = notice.Backdrops[0];
                        var finger = new FixtureTouch(401, backdrop, new Point(20, 20));
                        try
                        {
                            var touch = new TouchEventArgs(finger, Environment.TickCount) { RoutedEvent = UIElement.PreviewTouchDownEvent };
                            backdrop.RaiseEvent(touch);
                            Assert(touch.Handled && body.IsKeyboardFocused && notice.IsVisible && backdrop.IsVisible,
                                "synthetic WPF touch did not return focus from the backdrop to the message");
                        }
                        finally { finger.Stop(); }
                        Checks.Add("Normal preview: synthetic WPF PreviewTouchDown on the actual backdrop was consumed, refocused the body and left both windows visible");
                    }
                    var entrance = (TranslateTransform)notice.FindName("EntranceOffset");
                    PumpUntil(() => Math.Abs(entrance.Y) < .1 && notice.Opacity >= .999, "preview entrance animation did not settle");
                    Snapshot(notice, $"preview-{priority}.png");
                    theme.Invoke(notice, [true]);
                    Assert(SameBrush(notice.Background, SystemColors.WindowBrush) && SameBrush(notice.Foreground, SystemColors.WindowTextBrush) &&
                        SameBrush(close.Background, SystemColors.ControlBrush) && SameBrush(close.Foreground, SystemColors.ControlTextBrush) &&
                        ((TextBlock)notice.FindName("NotificationHeader")).Text.Contains(notice.Appearance.Label) &&
                        ((FrameworkElement)notice.FindName("PriorityMark")).IsVisible, $"system color mode lost {priority} colors or grade identification");
                    Snapshot(notice, $"preview-{priority}-system-colors.png");
                    theme.Invoke(notice, [false]);
                    Assert(SameBrush(notice.Background, notice.Surface), "normal priority surface did not return after switching themes");
                    Click(notice, "DismissButton");
                    PumpUntil(() => !notice.IsVisible && notice.Backdrops.Count == 0 && VisibleNotices() == 0, $"{priority} preview did not close cleanly");
                    if (main.IsActive)
                        PumpUntil(() => previewButton.IsKeyboardFocused, $"{priority} preview did not restore its trigger's focus");
                    Checks.Add($"Real selector and Preview button: {priority}, local preview only, native mask count, initial focus and forced system-color branch");
                }
                main.Close();
            });
        }
        finally { main.Close(); }
    }

    private static void RunLongNotificationChecks()
    {
        string title = "学校活动安排".PadRight(160, '字');
        const string end = "正文最后一行。";
        string body = "请在上课前核对教室、时间和应带的材料。\n" + string.Concat(Enumerable.Repeat("安排一：请按各班次序到场。\n", 180));
        body = body.PadRight(8000 - end.Length, '文') + end;
        string source = string.Concat(Enumerable.Repeat("学校教务处", 24));
        var window = new SchoolNotificationWindow(new(title, body, source, DateTimeOffset.Now, SchoolNotificationPriority.Normal));
        try
        {
            window.Show(); window.Width = 640; window.Height = 460;
            window.UpdateLayout();
            var scroll = (ScrollViewer)window.FindName("MessageScroll");
            var close = Button(window, "DismissButton");
            var fullSource = (TextBlock)window.FindName("FullNotificationSource");
            var footer = (TextBlock)window.FindName("NotificationSource");
            Assert(((TextBlock)window.FindName("NotificationTitle")).Text.Length == 160 &&
                ((TextBlock)window.FindName("NotificationBody")).Text == body && body.Length == 8000 && source.Length == 120 &&
                fullSource.IsVisible && fullSource.Text.EndsWith(source) && footer.Text == window.TimeLabel && scroll.ScrollableHeight > 0,
                "compact notification did not retain the full title, body, scrollable source and timestamp");
            PumpUntil(() => scroll.IsKeyboardFocused, "compact notification initial focus was not on the reading area");
            Snapshot(window, "detail-compact-long-start.png");
            SendNoticeKey(scroll, Key.PageDown);
            PumpUntil(() => scroll.VerticalOffset > 0, "Page Down did not scroll the long body");
            scroll.ScrollToEnd(); window.UpdateLayout();
            PumpUntil(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1, "complete source could not be reached");
            Assert(scroll.PanningMode == PanningMode.VerticalOnly, "long text does not allow vertical touch panning");
            Assert(scroll.IsKeyboardFocused && scroll.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)) && close.IsKeyboardFocused,
                $"Tab traversal did not reach close from the reading area ({Keyboard.FocusedElement})");
            var target = close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), window);
            Assert(window.InputHitTest(target) is DependencyObject hit && (close == hit || close.IsAncestorOf(hit)),
                "close button centre cannot be hit at the bottom of the long notification");
            Snapshot(window, "detail-compact-long-end.png");

            window.BeforeDismissAsync = () => Task.FromResult(false);
            Click(window, "DismissButton");
            var retry = (TextBlock)window.FindName("NotificationCloseStatus");
            Assert(window.IsVisible && close.IsEnabled && Equals(close.Content, "重试关闭") &&
                AutomationProperties.GetName(close) == "重试关闭" && retry.IsVisible && retry.Text.Contains("关闭回执"),
                "close failure did not keep a visible retry action with a matching accessible name");
            Snapshot(window, "detail-compact-retry.png");
            window.InvalidateNotice("学校已撤回这条通知。");
            window.UpdateLayout();
            Assert(!fullSource.IsVisible && fullSource.Text.Length == 0 && !footer.Text.Contains(window.TimeLabel) && !retry.IsVisible &&
                ((TextBlock)window.FindName("NotificationTitle")).Text == "此通知已失效" &&
                ((TextBlock)window.FindName("NotificationBody")).Text == "学校已撤回这条通知。" &&
                Equals(close.Content, "关闭窗口") && AutomationProperties.GetName(close) == "关闭窗口",
                "invalidated detail exposed replaced metadata or retained the old action");
            Snapshot(window, "detail-compact-invalidated.png");
            Click(window, "DismissButton");
            PumpUntil(() => !window.IsVisible && !window.DismissedByUser && window.Backdrops.Count == 0, "invalidated detail did not close and release its masks");
            Checks.Add("640×460 DIP detail: 160/8000/120-character content, Page Down and Tab traversal, close hit test, full source reachable, retry and withdrawal without a close receipt");
        }
        finally { if (window.IsVisible) window.Shutdown(); }
    }
}
