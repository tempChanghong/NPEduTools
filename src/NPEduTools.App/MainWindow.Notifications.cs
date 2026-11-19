using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class MainWindow
{
    private const string OpeningNotificationMessage = "正在打开学校通知，请稍候。";
    private SchoolNotificationWindow? _notificationPreview;

    private void ShowNotificationWindow(SchoolNotificationWindow window)
    {
        try
        {
            window.CapturePresentationTarget();
            UpdateQuickPresentationVisibility();
            HideStudyPresentations();
            window.Show();
        }
        catch
        {
            window.Shutdown();
            throw;
        }
    }

    private void NotificationPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (_notificationOpening)
        {
            NotificationPreviewMessage.Text = OpeningNotificationMessage;
            return;
        }
        if (_lifetime.IsCancellationRequested) return;
        if (_schoolNotification is not null) { _schoolNotification.Activate(); return; }
        if (_notificationPreview is not null) { _notificationPreview.Activate(); return; }
        if (NotificationPreviewPriority.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse<SchoolNotificationPriority>(tag, out var priority)) return;
        IDisposable? reservation = null;
        try
        {
            reservation = NPEduTools.Core.RuntimeOperationFile.TryAcquire(false);
            if (reservation is null)
            { NotificationPreviewMessage.Text = "正在切换运行环境，请在完成后预览通知。"; return; }
            var preview = new SchoolNotificationWindow(SchoolNotification.Preview(priority));
            _notificationPreview = preview;
            var visibleReservation = reservation;
            preview.Closed += (_, _) =>
            {
                _notificationPreview = null;
                visibleReservation.Dispose();
                if (!_lifetime.IsCancellationRequested) UpdateQuickPresentationVisibility();
                Dispatcher.InvokeAsync(() =>
                {
                    if (!_lifetime.IsCancellationRequested && IsActive && NotificationPreviewButton.IsVisible && !NotificationPresentationActive)
                        NotificationPreviewButton.Focus();
                }, System.Windows.Threading.DispatcherPriority.Input);
            };
            NotificationPreviewMessage.Text = "";
            ShowNotificationWindow(preview);
            reservation = null;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
        { NotificationPreviewMessage.Text = "暂时无法保留通知展示位置，请检查本地配置目录权限。"; }
        finally { reservation?.Dispose(); }
    }
}
