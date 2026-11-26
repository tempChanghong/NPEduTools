using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private sealed record NotificationReaderTarget(string Scope, long NavigationVersion, NpepNoticeSummary Summary, double ListOffset);
    private NotificationReaderTarget? _notificationReaderTarget;
    private NpepNotice? _notificationReaderNotice;
    private CancellationTokenSource? _notificationReaderRead;
    private double _notificationConnectionOffset;

    private void InitializeNotificationWorkspace()
    {
        NotificationWorkspace.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) return;
            _inboxNavigationVersion++;
            ResetNotificationReader();
        };
        NotificationWorkspace.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || _notificationReaderTarget is null) return;
            ReturnToNotificationList();
            e.Handled = true;
        };
        SizeChanged += (_, _) =>
        {
            if (NotificationWorkspace.IsVisible) LayoutNotificationWorkspace(NotificationWorkspace.ActualWidth);
        };
    }

    private void NotificationWorkspaceEntryClicked(object sender, RoutedEventArgs e)
    {
        if (!NotificationWorkspaceEntry.IsVisible || !NotificationWorkspaceEntry.IsEnabled) return;
        _notificationConnectionOffset = SettingsScroll.VerticalOffset;
        SettingsScroll.Visibility = Visibility.Collapsed;
        NotificationWorkspace.Visibility = Visibility.Visible;
        UpdateSettingsNavigation();
        LayoutNotificationWorkspace(NotificationWorkspace.ActualWidth);
        NotificationInboxMessage.Focus();
    }

    private void HideNotificationWorkspace()
    {
        NotificationWorkspace.Visibility = Visibility.Collapsed;
        SettingsScroll.Visibility = Visibility.Visible;
    }

    private void NotificationWorkspaceBackClicked(object sender, RoutedEventArgs e)
    {
        HideNotificationWorkspace();
        UpdateSettingsNavigation();
        SettingsScroll.ScrollToVerticalOffset(_notificationConnectionOffset);
        if (!NotificationWorkspaceEntry.Focus()) SettingsHeading.Focus();
    }

    private void NotificationReaderBackClicked(object sender, RoutedEventArgs e) => ReturnToNotificationList();

    private void NotificationWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        LayoutNotificationWorkspace(e.NewSize.Width);

    private void LayoutNotificationWorkspace(double width)
    {
        if (NotificationReaderLayout is null || NotificationReaderPane is null || NotificationListPane is null) return;
        bool wide = ActualWidth >= 900 && width >= 770;
        bool detailOpen = _notificationReaderTarget is not null;
        bool focusDetail = !wide && detailOpen && NotificationListPane.IsKeyboardFocusWithin;
        bool leavingNarrowBack = wide && NotificationReaderBack.IsKeyboardFocused;
        NotificationReaderLayout.ColumnDefinitions[0].Width = wide ? new(3, GridUnitType.Star) : new(1, GridUnitType.Star);
        NotificationReaderLayout.ColumnDefinitions[1].Width = wide ? new(5, GridUnitType.Star) : new(0);
        Grid.SetColumn(NotificationReaderPane, wide ? 1 : 0);
        NotificationReaderPane.Margin = wide ? new(14, 0, 0, 0) : new(0);
        NotificationListPane.Visibility = wide || !detailOpen ? Visibility.Visible : Visibility.Collapsed;
        NotificationReaderPane.Visibility = wide || detailOpen ? Visibility.Visible : Visibility.Collapsed;
        NotificationReaderBack.Visibility = !wide && detailOpen ? Visibility.Visible : Visibility.Collapsed;
        NotificationWorkspaceBack.Visibility = wide || !detailOpen ? Visibility.Visible : Visibility.Collapsed;
        if (focusDetail) NotificationReaderBack.Focus();
        if (leavingNarrowBack)
        {
            if (_notificationReaderNotice is null) NotificationReaderStatus.Focus();
            else NotificationReaderTitle.Focus();
        }
    }

    private async Task OpenNotificationReaderAsync(string scope, NpepNoticeSummary summary)
    {
        if (!NotificationWorkspace.IsVisible) return;
        CancelNotificationReaderRead();
        var target = new NotificationReaderTarget(scope, _inboxNavigationVersion, summary, NotificationListScroll.VerticalOffset);
        _notificationReaderTarget = target;
        _notificationReaderNotice = null;
        SetNotificationReaderText("正在在线核对通知…");
        LayoutNotificationWorkspace(NotificationWorkspace.ActualWidth);
        NotificationReaderScroll.ScrollToTop();
        await ReadNotificationBodyAsync(target, initial: true);
    }

    private async Task ReadNotificationBodyAsync(NotificationReaderTarget target, bool initial)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _notificationReaderRead = deadline;
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        bool StillCurrent() => ReferenceEquals(_notificationReaderRead, deadline) &&
            ReferenceEquals(_notificationReaderTarget, target) && NotificationWorkspace.IsVisible &&
            target.Scope == _inboxScope && target.NavigationVersion == _inboxNavigationVersion && !_lifetime.IsCancellationRequested;
        try
        {
            var summary = target.Summary;
            var command = new NpepNotificationCommand("get", target.Scope, summary.PublicationId, summary.Revision);
            var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "npep.notifications", Notification: command);
            var response = await HostClient.RequestAsync(_pipe, request, deadline.Token);
            if (!StillCurrent()) return;
            if (response is not { Outcome: "Succeeded", Inbox: { Connection: "ONLINE", CanPresent: true } inbox } ||
                inbox.Scope != target.Scope)
            {
                InvalidateNotificationReader("暂时无法在线核对，正文已隐藏。请返回列表后重试。");
                return;
            }
            if (VerifiedNotice(response, target.Scope, summary.PublicationId, summary.Revision) is not { } notice)
            {
                InvalidateNotificationReader("此通知已变化，请返回列表查看最新内容。");
                return;
            }
            if (!initial && notice == _notificationReaderNotice) return;
            _notificationReaderNotice = notice;
            SetNotificationReaderText("", notice, summary);
            if (initial && NotificationReaderBack.IsKeyboardFocused) NotificationReaderTitle.Focus();
        }
        catch (Exception error) when (IsManagementError(error))
        {
            if (StillCurrent()) InvalidateNotificationReader("暂时无法在线核对，正文已隐藏。请返回列表后重试。");
        }
        finally
        {
            if (ReferenceEquals(_notificationReaderRead, deadline)) _notificationReaderRead = null;
        }
    }

    private void ObserveNotificationReaderInbox(NpepInboxState inbox)
    {
        if (_notificationReaderTarget is not { } target || (_notificationReaderNotice is null && _notificationReaderRead is null)) return;
        if (inbox.Connection != "ONLINE" || !inbox.CanPresent)
        {
            InvalidateNotificationReader("通知暂时无法在线核对，正文已隐藏。");
            return;
        }
        if (inbox.Scope != target.Scope || !inbox.Items.Any(row =>
                row.PublicationId == target.Summary.PublicationId && row.Revision == target.Summary.Revision))
        {
            InvalidateNotificationReader("此通知已变化，请返回列表查看最新内容。");
            return;
        }
        if (_notificationReaderRead is null) _ = ReadNotificationBodyAsync(target, initial: false);
    }

    private void InvalidateNotificationReader(string message)
    {
        if (_notificationReaderTarget is null) return;
        CancelNotificationReaderRead();
        _notificationReaderNotice = null;
        bool restoreFocus = NotificationReaderPane.IsKeyboardFocusWithin && !NotificationReaderBack.IsKeyboardFocused;
        SetNotificationReaderText(message);
        NotificationReaderScroll.ScrollToTop();
        if (restoreFocus) NotificationReaderStatus.Focus();
    }

    private void ReturnToNotificationList()
    {
        var target = _notificationReaderTarget;
        ResetNotificationReader();
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsActive || !NotificationWorkspace.IsVisible || _notificationReaderTarget is not null) return;
            if (target is not null && target.Scope == _inboxScope && target.NavigationVersion == _inboxNavigationVersion)
            {
                NotificationListScroll.ScrollToVerticalOffset(target.ListOffset);
                NotificationListScroll.UpdateLayout();
                if (FocusNotificationRow(target.Summary.PublicationId, target.Summary.Revision)) return;
            }
            NotificationInboxMessage.Focus();
        }, DispatcherPriority.Input);
    }

    private void ResetNotificationReader()
    {
        CancelNotificationReaderRead();
        _notificationReaderTarget = null;
        _notificationReaderNotice = null;
        SetNotificationReaderText("选择一条通知查看正文。");
        LayoutNotificationWorkspace(NotificationWorkspace.ActualWidth);
    }

    private void CancelNotificationReaderRead()
    {
        var old = _notificationReaderRead;
        _notificationReaderRead = null;
        old?.Cancel();
    }

    private void SetNotificationReaderText(string status, NpepNotice? notice = null, NpepNoticeSummary? summary = null)
    {
        NotificationReaderStatus.Text = status;
        NotificationReaderTitle.Text = notice is null ? "" : string.IsNullOrWhiteSpace(notice.Title) ? "学校通知" : notice.Title;
        NotificationReaderBody.Text = notice is null ? "" : string.IsNullOrWhiteSpace(notice.Content) ? "（此通知没有正文）" : notice.Content;
        NotificationReaderMeta.Text = notice is null ? "" :
            $"{(string.IsNullOrWhiteSpace(notice.Source) ? "学校通知" : notice.Source)} · {notice.PublishAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}\n{summary?.DisplayLabel}";
    }
}
