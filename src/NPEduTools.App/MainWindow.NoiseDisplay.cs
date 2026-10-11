using System.Diagnostics;
using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ScheduledNoiseWindow? _scheduledNoiseWindow;
    private StudyWindow? _studyWindow;
    private StudyWindow? _manualStudyWindow;
    private long? _seatingUnsupportedAt;
    private long? _photosUnsupportedAt;
    private long? _reportUnsupportedAt;
    private StudyReportWindow? _studyReportWindow;
    private async Task NoiseDisplayPollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var studyRequestAt = Stopwatch.GetTimestamp();
                bool probeSeating = _seatingUnsupportedAt is null || Stopwatch.GetElapsedTime(_seatingUnsupportedAt.Value).TotalSeconds >= 60;
                bool probePhotos = _photosUnsupportedAt is null || Stopwatch.GetElapsedTime(_photosUnsupportedAt.Value).TotalSeconds >= 60;
                var studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), probePhotos ? "study.status.photos" : probeSeating ? "study.status.seating" : "study.status"));
                if (probePhotos && studyResponse is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) {
                    _photosUnsupportedAt = Stopwatch.GetTimestamp();
                    studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), probeSeating ? "study.status.seating" : "study.status"));
                } else if (probePhotos && studyResponse.Outcome == "Succeeded") _photosUnsupportedAt = null;
                if (probeSeating && studyResponse is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) {
                    _seatingUnsupportedAt = Stopwatch.GetTimestamp();
                    studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), "study.status"));
                } else if (probeSeating && studyResponse.Outcome == "Succeeded") _seatingUnsupportedAt = null;
                var studyRoundTrip = Stopwatch.GetElapsedTime(studyRequestAt);
                if (_lifetime.IsCancellationRequested) break;
                await PollStudyExitAsync(studyResponse.Study?.ProjectionPriority == true);
                long manualAt=Stopwatch.GetTimestamp();
                var manual=await ManagementRequestAsync(new(1,Guid.NewGuid(),"manual.study.status"));
                var manualAge=Stopwatch.GetElapsedTime(manualAt);
                bool schoolPriority=studyResponse.Study is {PolicyState:"APPLIED",ClockReady:true,Phase:"Preparing" or "Active"};
                if(schoolPriority)_manualStudyWindow?.HideDisplay();
                else if(studyResponse.Study?.ProjectionPriority != true && !_exitBusy && _exitWarning is not {IsVisible:true} && !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable() && manual.Outcome=="Succeeded")
                {
                    using var manualProbe=RuntimeOperationFile.TryAcquire(false);if(manualProbe is null){_manualStudyWindow?.HideDisplay();continue;}
                    long reportAt=Stopwatch.GetTimestamp();var report=await ManagementRequestAsync(new(1,Guid.NewGuid(),"manual.study.report.status"));
                    if(report.StudyReport is {Available:true} mr){_manualStudyWindow?.HideDisplay();_studyReportWindow??=new();_studyReportWindow.Apply(mr,Stopwatch.GetElapsedTime(reportAt));continue;}
                    if(manual.Study is {Display.Available:true} ms){_studyReportWindow?.HideDisplay();_studyWindow?.HideDisplay();_manualStudyWindow??=new(_pipe,()=>OpenNoiseClicked(this,new RoutedEventArgs()),manual:true);_manualStudyWindow.Apply(ms,manual.Noise,manualAge,studyResponse.StudyPhotos);_manualStudyWindow.ShowDisplay();continue;}
                    _manualStudyWindow?.HideDisplay();
                }
                else _manualStudyWindow?.HideDisplay();
                UpdateStudyHome(studyResponse.Outcome == "Succeeded" ? studyResponse.Study : null);
                if (studyResponse.Study is not { Display.Selected: true, PolicyState: "APPLIED", Seating.State: "READY" }) _studyWindow?.InvalidateSeating();
                bool blocked = _exitBusy || _notificationOpening || _schoolNotification is not null || _notificationPreview is not null ||
                    IsActive || _noiseWindow?.IsVisible == true || _onboardingWindow?.IsVisible == true ||
                    Application.Current.Windows.OfType<Window>().Any(w => w is not ScheduledNoiseWindow && w is not StudyWindow && w is not StudyReportWindow && w.IsVisible && w.IsActive);
                if (studyResponse.Outcome == "Succeeded" && studyResponse.Study?.Display?.Selected == true)
                {
                    _scheduledNoiseWindow?.Hide();
                    // Ordinary App/settings windows use the same bounded hide lease. Only real
                    // priority operations may defer display after its expiry; an open noise page cannot renew it.
                    bool studyBlocked = studyResponse.Study.ProjectionPriority || _exitBusy || _exitWarning is {IsVisible:true} || _notificationOpening || _schoolNotification is not null || _notificationPreview is not null ||
                        _onboardingWindow?.IsVisible == true || Application.Current.Windows.OfType<NoiseManagementDialog>().Any(w => w.IsVisible);
                    await UpdateStudyReportAsync(studyBlocked);
                    if (_studyReportWindow?.IsVisible == true) { _studyWindow?.HideDisplay(); continue; }
                    if (!studyBlocked && studyResponse.Study.Display.Available && !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable())
                    {
                        using var studyProbe = RuntimeOperationFile.TryAcquire(false);
                        if (studyProbe is null) _studyWindow?.HideDisplay();
                        else
                        {
                            _studyWindow ??= new(_pipe, () => OpenStudyMonitoringClicked(this, new RoutedEventArgs()));
                            _studyWindow.Apply(studyResponse.Study, studyResponse.Noise, studyRoundTrip, studyResponse.StudyPhotos);
                            _studyWindow.ShowDisplay();
                        }
                    }
                    else _studyWindow?.HideDisplay();
                    continue;
                }
                _studyWindow?.HideDisplay();
                _studyReportWindow?.HideDisplay();
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
            catch (Exception e) when (IsManagementError(e)) { UpdateStudyHome(null); _scheduledNoiseWindow?.Hide(); _studyWindow?.HideDisplay(); _studyReportWindow?.HideDisplay(); _manualStudyWindow?.HideDisplay(); }
            finally
            {
                if (!_lifetime.IsCancellationRequested && _studyWindow is not null) await _studyWindow.ReportPresentationAsync();
                if (!_lifetime.IsCancellationRequested && _manualStudyWindow is not null) await _manualStudyWindow.ReportPresentationAsync();
                try { await Task.Delay(500, _lifetime.Token); } catch (OperationCanceledException) { }
            }
        }
        _scheduledNoiseWindow?.Hide();
        _studyWindow?.HideDisplay();
        _studyReportWindow?.HideDisplay();
        _manualStudyWindow?.HideDisplay();
    }
    private async Task UpdateStudyReportAsync(bool blocked)
    {
        if (_reportUnsupportedAt is { } unsupported && Stopwatch.GetElapsedTime(unsupported).TotalSeconds < 60) return;
        long sent = Stopwatch.GetTimestamp();
        var reply = await ManagementRequestAsync(new HostRequest(1, Guid.NewGuid(), "study.report.status"));
        if (reply.ErrorCode is "UnknownCapability" or "STUDY_REPORT_UNSUPPORTED") { _reportUnsupportedAt = Stopwatch.GetTimestamp(); _studyReportWindow?.HideDisplay(); return; }
        _reportUnsupportedAt = null;
        if (_lifetime.IsCancellationRequested || blocked || RuntimeOperationFile.PriorityRequested || !NotificationDesktopAvailable() ||
            reply.Outcome != "Succeeded" || reply.StudyReport is not { Available: true, Data: not null } report)
        { _studyReportWindow?.HideDisplay(); return; }
        using var probe = RuntimeOperationFile.TryAcquire(false);
        if (probe is null) { _studyReportWindow?.HideDisplay(); return; }
        _studyReportWindow ??= new StudyReportWindow();
        _studyReportWindow.Apply(report, Stopwatch.GetElapsedTime(sent));
    }
}
