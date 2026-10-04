using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ScheduledNoiseWindow? _scheduledNoiseWindow;
    private async Task NoiseDisplayPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "noise.display.status"));
                if (_lifetime.IsCancellationRequested) break;
                bool blocked = _exitBusy || _notificationOpening || _schoolNotification is not null || _notificationPreview is not null ||
                    IsActive || _noiseWindow?.IsVisible == true || _onboardingWindow?.IsVisible == true ||
                    Application.Current.Windows.OfType<Window>().Any(w => w is not ScheduledNoiseWindow && w.IsVisible && w.IsActive);
                if (response.NoiseDisplay is not { Fallback: true } display || response.Noise is not { State: "Starting" or "Active" } noise ||
                    display.InstanceId != noise.InstanceId || display.SessionId != noise.SessionId ||
                    blocked || RuntimeOperationFile.PriorityRequested || !NotificationDesktopAvailable())
                { _scheduledNoiseWindow?.Hide(); }
                else
                {
                    // Do not hold a reservation while visible: examination and notifications win.
                    using var probe = RuntimeOperationFile.TryAcquire(false);
                    if (probe is null) _scheduledNoiseWindow?.Hide();
                    else
                    {
                        _scheduledNoiseWindow ??= new(_pipe, () => OpenNoiseClicked(this, new RoutedEventArgs()));
                        _scheduledNoiseWindow.Apply(display, noise);
                        if (!_scheduledNoiseWindow.IsVisible) _scheduledNoiseWindow.Show();
                    }
                }
            }
            catch (Exception e) when (IsManagementError(e)) { _scheduledNoiseWindow?.Hide(); }
            try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
        _scheduledNoiseWindow?.Hide();
    }
}
