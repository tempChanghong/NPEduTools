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
    private long? _quickProjectionAt;
    private void UpdateQuickPresentationVisibility()
    {
        bool projector = _quickProjectionAt is { } at && Stopwatch.GetElapsedTime(at) < TimeSpan.FromSeconds(1.5);
        static bool Visible(Window? window) => window is { IsVisible: true } && window.WindowState != WindowState.Minimized;
        bool study = Visible(_studyWindow) || Visible(_manualStudyWindow) || Visible(_studyReportWindow) || Visible(_exitWarning);
        _quick?.SetAutoHidden(projector || study);
    }
    private T TrackStudyPresentationWindow<T>(T window) where T : Window
    {
        window.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, UpdateQuickPresentationVisibility);
        window.StateChanged += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, UpdateQuickPresentationVisibility);
        return window;
    }
    private async Task NoiseDisplayPollAsync()
    {
        var calibrationRead = new NoiseCalibrationRead();
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var studyRequestAt = Stopwatch.GetTimestamp();
                bool probeSeating = _seatingUnsupportedAt is null || Stopwatch.GetElapsedTime(_seatingUnsupportedAt.Value).TotalSeconds >= 60;
                bool probePhotos = _photosUnsupportedAt is null || Stopwatch.GetElapsedTime(_photosUnsupportedAt.Value).TotalSeconds >= 60;
                string studyCapability = probePhotos ? "study.status.photos" : probeSeating ? "study.status.seating" : "study.status";
                var studyResponse = await calibrationRead.RequestAsync(new(1, Guid.NewGuid(), studyCapability), ManagementRequestAsync);
                if (probePhotos && studyResponse is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) {
                    _photosUnsupportedAt = Stopwatch.GetTimestamp();
                    studyCapability = probeSeating ? "study.status.seating" : "study.status";
                    studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), studyCapability));
                } else if (probePhotos && studyResponse.Outcome == "Succeeded") _photosUnsupportedAt = null;
                if (probeSeating && studyResponse is { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) {
                    _seatingUnsupportedAt = Stopwatch.GetTimestamp();
                    studyCapability = "study.status";
                    studyResponse = await ManagementRequestAsync(new(1, Guid.NewGuid(), studyCapability));
                } else if (probeSeating && studyResponse.Outcome == "Succeeded") _seatingUnsupportedAt = null;
                var studyRoundTrip = Stopwatch.GetElapsedTime(studyRequestAt);
                if (_lifetime.IsCancellationRequested) break;
                UpdateStudyHome(studyResponse.Outcome == "Succeeded" && studyRoundTrip < TimeSpan.FromSeconds(3.5) ? studyResponse.Study : null);
                bool workspaceFresh = studyResponse.Outcome == "Succeeded" && studyRoundTrip < TimeSpan.FromSeconds(3.5);
                UpdateWorkspaceReadiness(workspaceFresh ? studyResponse.Noise : null, workspaceFresh ? studyResponse.Study?.Settings : null,
                    workspaceFresh ? studyResponse.NoiseCalibrationStatus : null);
                _quickProjectionAt = studyResponse.Outcome == "Succeeded" && studyResponse.Study?.ProjectionPriority == true && studyRoundTrip < TimeSpan.FromSeconds(1.5) ? Stopwatch.GetTimestamp() : null;
                if (_quickProjectionAt is not null) UpdateQuickPresentationVisibility();
                await PollStudyExitAsync(studyResponse.Study?.ProjectionPriority == true);
                long manualAt=Stopwatch.GetTimestamp();
                var manual=await calibrationRead.RequestAsync(new(1,Guid.NewGuid(),"manual.study.status"), ManagementRequestAsync);
                var manualAge=Stopwatch.GetElapsedTime(manualAt);
                UpdateWorkspaceManual(manual.Outcome == "Succeeded" && manualAge < TimeSpan.FromSeconds(3.5) ? manual.ManualStudy : null);
                bool schoolPriority=studyResponse.Outcome == "Succeeded" && studyResponse.Study is {PolicyState:"APPLIED",ClockReady:true,Phase:"Preparing" or "Active"};
                if(schoolPriority)_manualStudyWindow?.HideDisplay();
                else if(studyResponse.Study?.ProjectionPriority != true && !_exitBusy && _exitWarning is not {IsVisible:true} && !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable() && manual.Outcome=="Succeeded")
                {
                    using var manualProbe=RuntimeOperationFile.TryAcquire(false);if(manualProbe is null){_manualStudyWindow?.HideDisplay();continue;}
                    long reportAt=Stopwatch.GetTimestamp();var report=await ManagementRequestAsync(new(1,Guid.NewGuid(),"manual.study.report.status"));
                    if (report.Outcome == "Succeeded" && report.StudyReport is { Available: true, Data: not null } mr)
                    {
                        _manualStudyWindow?.HideDisplay(); _studyWindow?.HideDisplay(); _scheduledNoiseWindow?.Hide();
                        _studyReportWindow ??= TrackStudyPresentationWindow(new StudyReportWindow());
                        _studyReportWindow.Apply(mr, Stopwatch.GetElapsedTime(reportAt));
                        continue;
                    }
                    // A later report/exit read may have consumed the status's freshness budget.
                    // Re-evaluate age at presentation, including every intervening await.
                    manualAge = Stopwatch.GetElapsedTime(manualAt);
                    if (manualAge < TimeSpan.FromSeconds(1.5) && manual.Study is { Display.Available: true } ms)
                    {
                        _studyReportWindow?.HideDisplay();
                        _studyWindow?.HideDisplay(); _scheduledNoiseWindow?.Hide();
                        _manualStudyWindow ??= TrackStudyPresentationWindow(new StudyWindow(_pipe, () => OpenNoiseClicked(this, new RoutedEventArgs()), manual: true));
                        _manualStudyWindow.Apply(ms, manual.Noise, manualAge, studyResponse.StudyPhotos, manual.Npep, manual.NoiseCalibrationStatus);
                        _manualStudyWindow.ShowDisplay();
                        continue;
                    }
                    _manualStudyWindow?.HideDisplay();
                }
                else _manualStudyWindow?.HideDisplay();
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
                    studyRoundTrip = Stopwatch.GetElapsedTime(studyRequestAt);
                    if (!studyBlocked && studyRoundTrip >= TimeSpan.FromSeconds(1.5))
                    {
                        // Unrelated exit/manual/report reads must not repeatedly withdraw a healthy school display.
                        // Re-read authoritative state rather than extending its freshness allowance.
                        studyRequestAt = Stopwatch.GetTimestamp();
                        studyResponse = await calibrationRead.RequestAsync(new(1, Guid.NewGuid(), studyCapability), ManagementRequestAsync);
                        studyRoundTrip = Stopwatch.GetElapsedTime(studyRequestAt);
                        _quickProjectionAt = studyResponse.Outcome == "Succeeded" && studyResponse.Study?.ProjectionPriority == true &&
                            studyRoundTrip < TimeSpan.FromSeconds(1.5) ? Stopwatch.GetTimestamp() : null;
                    }
                    if (!studyBlocked && !_exitBusy && _exitWarning is not { IsVisible: true } &&
                        !_notificationOpening && _schoolNotification is null && _notificationPreview is null &&
                        _onboardingWindow?.IsVisible != true && !Application.Current.Windows.OfType<NoiseManagementDialog>().Any(w => w.IsVisible) &&
                        studyResponse.Outcome == "Succeeded" && studyRoundTrip < TimeSpan.FromSeconds(1.5) &&
                        studyResponse.Study is { Display: { Selected: true, Available: true }, ProjectionPriority: false } &&
                        !RuntimeOperationFile.PriorityRequested && NotificationDesktopAvailable())
                    {
                        using var studyProbe = RuntimeOperationFile.TryAcquire(false);
                        if (studyProbe is null) _studyWindow?.HideDisplay();
                        else
                        {
                            _studyWindow ??= TrackStudyPresentationWindow(new StudyWindow(_pipe, () => OpenStudyMonitoringClicked(this, new RoutedEventArgs())));
                            _studyWindow.Apply(studyResponse.Study, studyResponse.Noise, studyRoundTrip, studyResponse.StudyPhotos, studyResponse.Npep, studyResponse.NoiseCalibrationStatus);
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
            catch (Exception e) when (IsManagementError(e)) { UpdateStudyHome(null); UpdateWorkspaceManual(null); UpdateWorkspaceReadiness(null, null); _scheduledNoiseWindow?.Hide(); _studyWindow?.HideDisplay(); _studyReportWindow?.HideDisplay(); _manualStudyWindow?.HideDisplay(); }
            finally
            {
                UpdateQuickPresentationVisibility();
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
        _studyReportWindow ??= TrackStudyPresentationWindow(new StudyReportWindow());
        _studyReportWindow.Apply(report, Stopwatch.GetElapsedTime(sent));
    }
}
