using System.Diagnostics;
using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ScheduledNoiseWindow? _scheduledNoiseWindow;
    private StudyWindow? _studyWindow;
    private long? _seatingUnsupportedAt;
    private async Task NoiseDisplayPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var studyRequestAt = Stopwatch.GetTimestamp();
                bool probeSeating = _seatingUnsupportedAt is null || Stopwatch.GetElapsedTime(_seatingUnsupportedAt.Value).TotalSeconds >= 60;
                var studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), probeSeating ? "study.status.seating" : "study.status"));
                if (probeSeating && studyResponse is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) {
                    _seatingUnsupportedAt = Stopwatch.GetTimestamp();
                    studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), "study.status"));
                } else if (probeSeating && studyResponse.Outcome == "Succeeded") _seatingUnsupportedAt = null;
                var studyRoundTrip = Stopwatch.GetElapsedTime(studyRequestAt);
                if (_lifetime.IsCancellationRequested) break;
                UpdateStudyHome(studyResponse.Outcome == "Succeeded" ? studyResponse.Study : null);
                if (studyResponse.Study is not { Display.Selected: true, PolicyState: "APPLIED", Seating.State: "READY" }) _studyWindow?.InvalidateSeating();
                bool blocked = _exitBusy || _notificationOpening || _schoolNotification is not null || _notificationPreview is not null ||
                    IsActive || _noiseWindow?.IsVisible == true || _onboardingWindow?.IsVisible == true ||
                    Application.Current.Windows.OfType<Window>().Any(w => w is not ScheduledNoiseWindow && w is not StudyWindow && w.IsVisible && w.IsActive);
                if (studyResponse.Outcome == "Succeeded" && studyResponse.Study?.Display?.Selected == true)
                {
                    _scheduledNoiseWindow?.Hide();
                    // Ordinary App/settings windows use the same bounded hide lease. Only real
                    // priority operations may defer display after its expiry; an open noise page cannot renew it.
                    bool studyBlocked = _exitBusy || _notificationOpening || _schoolNotification is not null || _notificationPreview is not null ||
                        _onboardingWindow?.IsVisible == true || Application.Current.Windows.OfType<NoiseManagementDialog>().Any(w => w.IsVisible);
                    if (!studyBlocked && studyResponse.Study.Display.Available && !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable())
                    {
                        using var studyProbe = RuntimeOperationFile.TryAcquire(false);
                        if (studyProbe is null) _studyWindow?.HideDisplay();
                        else
                        {
                            _studyWindow ??= new(_pipe, () => OpenStudyMonitoringClicked(this, new RoutedEventArgs()));
                            _studyWindow.Apply(studyResponse.Study, studyResponse.Noise, studyRoundTrip);
                            _studyWindow.ShowDisplay();
                        }
                    }
                    else _studyWindow?.HideDisplay();
                    continue;
                }
                _studyWindow?.HideDisplay();
                // Only a successfully understood absent policy, or an older Host, allows legacy display.
                bool legacy = studyResponse.Outcome == "Succeeded" && studyResponse.Study?.Display?.Selected == false ||
                    studyResponse.Outcome == "Rejected" && studyResponse.ErrorCode is "STUDY_UNSUPPORTED" or "UnknownCapability";
                if (!legacy)
                { _scheduledNoiseWindow?.Hide(); continue; }
                var response = await ManagementRequestAsync(new(1, Guid.NewGuid(), "noise.display.status"));
                if (_lifetime.IsCancellationRequested) break;
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
            catch (Exception e) when (IsManagementError(e)) { UpdateStudyHome(null); _scheduledNoiseWindow?.Hide(); _studyWindow?.HideDisplay(); }
            finally
            {
                if (!_lifetime.IsCancellationRequested && _studyWindow is not null) await _studyWindow.ReportPresentationAsync();
                try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { }
            }
        }
        _scheduledNoiseWindow?.Hide();
        _studyWindow?.HideDisplay();
    }
}
