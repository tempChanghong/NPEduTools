using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private const double CompactNotificationWidth = 680;
    private Button? _notificationPagerFocus;

    private void ApplyNotificationInbox(NpepInboxState inbox)
    {
        if (inbox.Connection == "DISABLED")
        {
            ClearNotificationInbox(0, "学校互联已停用。启用学校互联后可接收通知。");
            return;
        }

        if (inbox.Scope is not { Length: 64 } scope || !scope.All(Uri.IsHexDigit))
        {
            ClearNotificationInbox(0, inbox.Message);
            return;
        }

        bool online = inbox.Connection == "ONLINE";
        bool empty = inbox.Total == 0;
        string status = online
            ? empty ? "当前没有有效通知。" : inbox.Message
            : inbox.Items.Count == 0 ? inbox.Message : $"显示上次同步的列表，打开前需在线核对。{inbox.Message}";

        if (_inboxOffset > 0 && _inboxOffset >= inbox.Total)
        {
            SetNotificationInboxPage(scope, 0, null, null, online && !empty ? "正在重新读取首页…" : status, "");
            return;
        }

        string range = "";
        if (inbox.Items.Count > 0)
        {
            int first = _inboxOffset + 1, last = _inboxOffset + inbox.Items.Count;
            range = online ? $"第 {first}–{last} 条，共 {inbox.Total} 条" : $"缓存列表第 {first}–{last} 条";
        }
        SetNotificationInboxPage(scope, _inboxOffset, inbox.NextOffset, inbox.Items, status, range);
        ObserveNotificationReaderInbox(inbox);
    }

    private void ClearNotificationInbox(int offset, string status) =>
        SetNotificationInboxPage(null, offset, null, null, status, "");

    private void SelectNotificationPage(int offset)
    {
        if (offset == _inboxOffset) return;
        ResetNotificationReader();
        SetNotificationInboxPage(null, offset, null, null, "正在读取通知列表…", "", awaitingSelectedPage: true);
    }

    private void SetNotificationInboxPage(string? scope, int offset, int? next,
        IReadOnlyList<NpepNoticeSummary>? rows, string status, string range, bool awaitingSelectedPage = false)
    {
        var focused = Keyboard.FocusedElement;
        var previousPagerFocus = _notificationPagerFocus;
        _notificationPagerFocus = awaitingSelectedPage && (focused == NotificationPrevious || focused == NotificationNext)
            ? (Button)focused : null;
        var focusedRow = (focused as Button)?.Tag as NpepNoticeSummary;
        int focusedIndex = focusedRow is null ? -1 : NotificationInboxItems.Items.IndexOf(focusedRow);
        bool sameScope = scope == _inboxScope;
        if (!sameScope || offset != _inboxOffset)
        {
            _inboxNavigationVersion++;
            InvalidateNotificationReader("通知所属范围已变化，请返回列表。");
        }
        bool replaceRows = !sameScope || !NotificationInboxItems.Items.Cast<NpepNoticeSummary>().SequenceEqual(rows ?? []);
        _inboxScope = scope;
        _inboxNext = next;
        _inboxOffset = offset;
        if (replaceRows) NotificationInboxItems.ItemsSource = rows;
        NotificationInboxMessage.Text = status;
        NotificationPageRange.Text = range;
        NotificationPrevious.IsEnabled = offset > 0;
        NotificationNext.IsEnabled = next is not null;
        NotificationPager.Visibility = offset > 0 || next is not null || rows is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        if (_notificationPagerFocus is not null)
        {
            NotificationInboxMessage.Focus();
        }
        else if (IsActive && focused == NotificationInboxMessage && previousPagerFocus is not null)
        {
            FocusNotificationPager(previousPagerFocus);
        }
        else if (focusedIndex >= 0 && replaceRows)
        {
            if (rows is { Count: > 0 })
            {
                int index = Math.Min(focusedIndex, rows.Count - 1);
                if (sameScope)
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].PublicationId != focusedRow!.PublicationId || rows[i].Revision != focusedRow.Revision) continue;
                        index = i;
                        break;
                    }
                }
                if (FocusNotificationRow(index)) return;
            }
            NotificationInboxMessage.Focus();
        }
        else if ((focused == NotificationPrevious && !NotificationPrevious.IsEnabled) ||
                 (focused == NotificationNext && !NotificationNext.IsEnabled))
        {
            FocusNotificationPager();
        }
    }

    private void FocusNotificationPager(Button? preferred = null)
    {
        if (preferred is { IsEnabled: true } && preferred.Focus()) return;
        if (NotificationPrevious.IsEnabled && NotificationPrevious.Focus()) return;
        if (NotificationNext.IsEnabled && NotificationNext.Focus()) return;
        NotificationInboxMessage.Focus();
    }

    private void NotificationInboxSizeChanged(object sender, SizeChangedEventArgs e) =>
        NotificationInboxItems.Tag = e.NewSize.Width < CompactNotificationWidth ? "compact" : null;

    private void RestoreNotificationRowFocus(string scope, long version, NpepNotice notice)
    {
        if (!IsActive || _lifetime.IsCancellationRequested || NotificationPresentationActive ||
            !NotificationInboxItems.IsVisible || version != _inboxNavigationVersion || scope != _inboxScope) return;
        if (!FocusNotificationRow(notice.PublicationId, notice.Revision)) NotificationInboxMessage.Focus();
    }

    private bool FocusNotificationRow(string publicationId, long revision)
    {
        var rows = NotificationInboxItems.Items;
        for (int i = 0; i < rows.Count; i++)
            if (rows[i] is NpepNoticeSummary row && row.PublicationId == publicationId &&
                row.Revision == revision && FocusNotificationRow(i)) return true;
        return false;
    }

    private bool FocusNotificationRow(int index)
    {
        NotificationInboxItems.UpdateLayout();
        return NotificationInboxItems.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter presenter &&
            NotificationInboxItems.ItemTemplate.FindName("NotificationInboxRow", presenter) is Button row && row.Focus();
    }

    private void NotificationPreviewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < CompactNotificationWidth;
        NotificationPreviewPriority.Width = Math.Min(compact ? 280 : 220, e.NewSize.Width);
        Grid.SetRow(NotificationPreviewButton, compact ? 1 : 0);
        Grid.SetColumn(NotificationPreviewButton, compact ? 0 : 1);
        NotificationPreviewButton.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(12, 0, 0, 0);
    }

    private void NotificationInboxInitialized(object? sender, EventArgs e) =>
        UpdateNotificationInboxColors(((Border)sender!).Resources);

    private void NotificationInboxLoaded(object sender, RoutedEventArgs e)
    {
        UpdateNotificationInboxColors(NotificationInboxCard.Resources);
        SystemParameters.StaticPropertyChanged += NotificationInboxSystemSettingsChanged;
    }

    private void NotificationInboxUnloaded(object sender, RoutedEventArgs e) =>
        SystemParameters.StaticPropertyChanged -= NotificationInboxSystemSettingsChanged;

    private void NotificationInboxSystemSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Dispatcher.InvokeAsync(() => UpdateNotificationInboxColors(NotificationInboxCard.Resources));
    }

    private static void UpdateNotificationInboxColors(ResourceDictionary resources)
    {
        foreach (var priority in Enum.GetValues<SchoolNotificationPriority>())
        {
            resources[$"Notification{priority}Brush"] = SystemParameters.HighContrast
                ? SystemColors.WindowTextBrush
                : (Brush)new BrushConverter().ConvertFromString(SchoolNotificationStyle.For(priority).Background)!;
        }
    }

    private void NotificationPreviousClicked(object sender, RoutedEventArgs e) => SelectNotificationPage(Math.Max(0, _inboxOffset - 10));
    private void NotificationNextClicked(object sender, RoutedEventArgs e) { if (_inboxNext is { } offset) SelectNotificationPage(offset); }
}
