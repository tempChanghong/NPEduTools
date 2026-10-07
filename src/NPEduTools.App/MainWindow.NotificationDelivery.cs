using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private SchoolNotificationWindow? _schoolNotification;
    private NpepNotice? _shownNotice;
    private string? _shownScope, _inboxScope;
    private int _inboxOffset;
    private int? _inboxNext;
    private long _inboxNavigationVersion;
    private bool _notificationReading, _notificationOpening;
    private long _lastNotificationRead;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    private static bool NotificationDesktopAvailable()
    {
        if (!Environment.UserInteractive) return false;
        var desktop = OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero) return false;
        CloseDesktop(desktop); return true;
    }

    private Task<HostResponse> NotificationRequestAsync(NpepNotificationCommand command) =>
        ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "npep.notifications", Notification: command));

    private async Task NotificationPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            // Yield the window and its backdrop without sending a dismissed receipt.
            if (NPEduTools.Core.RuntimeOperationFile.PriorityRequested)
            {
                _schoolNotification?.Shutdown();
                _notificationPreview?.Shutdown();
            }
            if (!_notificationReading && !_notificationOpening)
            {
                _notificationReading = true;
                var navigationVersion = _inboxNavigationVersion;
                try
                {
                    var reply = await NotificationRequestAsync(new("poll", Offset: _inboxOffset));
                    if (reply.Inbox is not { } inbox)
                    {
                        if (navigationVersion == _inboxNavigationVersion)
                            NotificationInboxMessage.Text = "请更新并重启后台以接收学校通知。";
                        InvalidateUnverifiedNotification(navigationVersion == _inboxNavigationVersion);
                    }
                    else
                    {
                        // A page selected while this request was pending owns the list UI.
                        // Keep verifying any shown notice even when these page rows are obsolete.
                        if (navigationVersion == _inboxNavigationVersion)
                        {
                            _inboxScope = inbox.Scope;
                            NotificationInboxMessage.Text = inbox.Message + $"（当前有效通知 {inbox.Total} 条）";
                            NotificationInboxItems.ItemsSource = inbox.Items; _inboxNext = inbox.NextOffset;
                            if (_inboxOffset >= inbox.Total && _inboxOffset != 0) _inboxOffset = 0;
                            NotificationNext.IsEnabled = _inboxNext is not null; NotificationPrevious.IsEnabled = _inboxOffset > 0;
                        }
                        if (_schoolNotification is { Invalidated: false } && _shownNotice is { } shown)
                        {
                            var current = await NotificationRequestAsync(new("get", _shownScope, shown.PublicationId, shown.Revision));
                            if (current.Inbox is not { CanPresent: true, Current: not null } || current.Inbox.Scope != _shownScope)
                                _schoolNotification?.InvalidateNotice("通知已撤回、更新、过期，或学校互联已停用。请关闭此窗口。新通知会继续按顺序显示。");
                        }
                        else if (_schoolNotification is null && inbox.CanPresent && inbox.Current is { } candidate && NotificationDesktopAvailable())
                            await ShowSchoolNotificationAsync(inbox.Scope!, candidate);
                        // A readable list alone does not verify the body of an already displayed notice.
                        _lastNotificationRead = Stopwatch.GetTimestamp();
                    }
                }
                catch (Exception error) when (IsManagementError(error))
                {
                    if (navigationVersion == _inboxNavigationVersion)
                        NotificationInboxMessage.Text = "通知后台暂不可用，正在重试。";
                    InvalidateUnverifiedNotification(navigationVersion == _inboxNavigationVersion);
                }
                finally { _notificationReading = false; }
            }
            try { await Task.Delay(1000, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
        _schoolNotification?.InvalidateNotice("学校互联后台已停止。请关闭此窗口，重新启动 NPEduTools 后再核对通知。");
    }

    private void InvalidateUnverifiedNotification(bool clearInbox = true)
    {
        if (clearInbox) ClearNotificationInbox(0);
        if (Stopwatch.GetElapsedTime(_lastNotificationRead).TotalSeconds > 60)
            _schoolNotification?.InvalidateNotice("无法核对通知的有效性，正文已暂时隐藏。请关闭窗口，连接恢复后会重新核对通知。");
    }

    private void ClearNotificationInbox(int offset)
    {
        _inboxScope = null; _inboxNext = null; _inboxOffset = offset;
        NotificationInboxItems.ItemsSource = null;
        NotificationNext.IsEnabled = false; NotificationPrevious.IsEnabled = offset > 0;
    }

    private void SelectNotificationPage(int offset)
    {
        if (offset == _inboxOffset) return;
        _inboxNavigationVersion++;
        ClearNotificationInbox(offset);
        NotificationInboxMessage.Text = "正在读取通知列表…";
    }

    private async Task ShowSchoolNotificationAsync(string scope, NpepNotice notice)
    {
        if (_schoolNotification is not null || _notificationPreview is not null || _notificationOpening || _lifetime.IsCancellationRequested) return;
        _notificationOpening = true;
        IDisposable? reservation = null;
        try
        {
            // Hold through the visible window, not merely through Show(). A switch defers
            // presentation only; inbox polling and delivery continue for every priority.
            reservation = NPEduTools.Core.RuntimeOperationFile.TryAcquire(false);
            if (reservation is null) return;
            // Check this exact version just before opening, including manual opens from an older list.
            var check = await NotificationRequestAsync(new("get", scope, notice.PublicationId, notice.Revision));
            if (_lifetime.IsCancellationRequested || check.Inbox is not { CanPresent: true, Current: not null } || !NotificationDesktopAvailable()) return;
            notice = check.Inbox.Current;
            var priority = Enum.Parse<SchoolNotificationPriority>(notice.Priority, true);
            var window = new SchoolNotificationWindow(new(string.IsNullOrWhiteSpace(notice.Title) ? "学校通知" : notice.Title,
                string.IsNullOrWhiteSpace(notice.Content) ? "（此通知没有正文）" : notice.Content,
                string.IsNullOrWhiteSpace(notice.Source) ? "学校通知" : notice.Source, notice.PublishAt, priority));
            _scheduledNoiseWindow?.Hide();
            _schoolNotification = window; _shownNotice = notice; _shownScope = scope;
            async Task<bool> Mark(string action)
            {
                try
                {
                    var response = await NotificationRequestAsync(new(action, scope, notice.PublicationId, notice.Revision));
                    if (response.ErrorCode == "NoticeChanged") { window.InvalidateNotice("通知或连接已变化，请关闭此窗口。"); return true; }
                    return response.Outcome == "Succeeded";
                }
                catch (Exception error) when (IsManagementError(error)) { return false; }
            }
            window.BeforeDismissAsync = async () => await Mark("displayed") && await Mark("dismissed");
            window.ContentRendered += async (_, _) =>
            {
                if (!window.Invalidated && NotificationDesktopAvailable()) await Mark("displayed");
            };
            var visibleReservation = reservation;
            window.Closed += (_, _) =>
            {
                _schoolNotification = null; _shownNotice = null; _shownScope = null;
                visibleReservation.Dispose();
            };
            window.Show();
            reservation = null; // Ownership transferred to Closed.
        }
        finally { reservation?.Dispose(); _notificationOpening = false; }
    }

    private async void OpenSchoolNotificationClicked(object sender, RoutedEventArgs e)
    {
        if (_schoolNotification is not null) { _schoolNotification.Activate(); return; }
        if (sender is not Button { Tag: NpepNoticeSummary summary } || _inboxScope is null) return;
        try
        {
            var response = await NotificationRequestAsync(new("get", _inboxScope, summary.PublicationId, summary.Revision));
            if (response.Inbox is { CanPresent: true, Current: not null }) await ShowSchoolNotificationAsync(_inboxScope, response.Inbox.Current);
            else NotificationInboxMessage.Text = "通知已变化或尚未完成在线核对，请等待刷新。";
        }
        catch (Exception error) when (IsManagementError(error)) { NotificationInboxMessage.Text = "无法打开通知，请稍后重试。"; }
    }
    private void NotificationPreviousClicked(object sender, RoutedEventArgs e) => SelectNotificationPage(Math.Max(0, _inboxOffset - 10));
    private void NotificationNextClicked(object sender, RoutedEventArgs e) { if (_inboxNext is { } offset) SelectNotificationPage(offset); }
}
