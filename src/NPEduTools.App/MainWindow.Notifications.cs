using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class MainWindow
{
    private SchoolNotificationWindow? _notificationPreview;

    private void NotificationPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (_schoolNotification is not null) { _schoolNotification.Activate(); return; }
        if (_notificationPreview is not null) { _notificationPreview.Activate(); return; }
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<SchoolNotificationPriority>(tag, out var priority)) return;
        IDisposable? reservation = null;
        try
        {
            reservation = NPEduTools.Core.RuntimeOperationFile.TryAcquire(false);
            if (reservation is null)
            { NotificationInboxMessage.Text = "正在切换运行环境，请在完成后预览通知。"; return; }
            var preview = new SchoolNotificationWindow(SchoolNotification.Preview(priority));
            _scheduledNoiseWindow?.Hide();
            _notificationPreview = preview;
            var visibleReservation = reservation;
            preview.Closed += (_, _) => { _notificationPreview = null; visibleReservation.Dispose(); };
            preview.Show();
            reservation = null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { NotificationInboxMessage.Text = "暂时无法保留通知展示位置，请检查本地配置目录权限。"; }
        finally { reservation?.Dispose(); }
    }
}
