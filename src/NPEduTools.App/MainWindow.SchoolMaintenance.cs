using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private bool DesktopIdleForMaintenance() =>
        !_exiting && !_exitBusy && !_adminBusy && !_actionInProgress && !_touchBusy && IsEnabled &&
        !IsVisible && (!_configurationLoaded || ExecutablePathBox.Text == (_savedPath ?? "")) &&
        !Application.Current.Windows.Cast<Window>().Any(w => w != this && w != _quick && w.IsVisible) &&
        !(_quick?.IsKeyboardFocusWithin ?? false);

    private async Task WatchSchoolMaintenanceAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);
                if (!DesktopIdleForMaintenance()) continue;
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(token);
                poll.CancelAfter(TimeSpan.FromSeconds(2));
                var status = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "software.update.status"),
                    poll.Token, StartupElevation.VerifyMaintenanceHost);
                if (status.SoftwareUpdate?.State != "WAITING_DESKTOP" || !DesktopIdleForMaintenance()) continue;
                _exitBusy = true;
                IsEnabled = false;
                if (_quick is not null) _quick.IsEnabled = false;
                try
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    var reply = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(),
                        "software.update.desktop-ready", TimeoutMs: 15000), wait.Token, StartupElevation.VerifyMaintenanceHost);
                    if (reply is { Outcome: "Succeeded", Message: "CLOSE" })
                    {
                        _exiting = true;
                        Close();
                        return;
                    }
                }
                finally
                {
                    if (!_exiting)
                    {
                        IsEnabled = true;
                        if (_quick is not null) _quick.IsEnabled = true;
                        _exitBusy = false;
                    }
                }
            }
            catch (Exception e) when (IsManagementError(e) || e is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException) { }
        }
    }
}
