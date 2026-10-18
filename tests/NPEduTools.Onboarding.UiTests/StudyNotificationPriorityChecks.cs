using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void WorkspaceStudyNotificationPriority(string presentation, string blocker, bool duringRead)
    {
        string pipe = "NPEduTools.Test.study-notice-priority." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var study = StudyFixture();
        var outside = study with { Phase = "Outside", Execution = null,
            Display = study.Display! with { Selected = presentation == "school-report", Available = false } };
        var manual = study with { Mode = "Manual", ClockSource = "Windows", Execution = study.Execution! with { Owner = "Manual" } };
        var raw = new StudyReportChannel("dBFS", 600, -35, -18, 600, 20, 5, [], 600);
        var data = new StudyReportData(study.Window!, 9000, 600, 600d / 9000, 0, 6000, 1, raw, null, true, [],
            presentation == "school-report" ? "Daily" : "Manual");
        var report = new StudyReportState(Guid.NewGuid(), study.Version, true, "Report", 300, data);
        int cycles = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            Assert(request.Noise is null && request.ManualStudy is null && request.StudyHide is null && request.Notification is null,
                "notification priority sent a capture, hide, end or delivery mutation");
            string capability = NoiseCalibrationStatusContract.BaseCapability(request.Capability) ?? request.Capability;
            if (capability.StartsWith("study.status", StringComparison.Ordinal))
            {
                int cycle = Interlocked.Increment(ref cycles);
                if (cycle == 2) { nextPoll.TrySetResult(); await releaseNext.Task.WaitAsync(stop.Token); }
                if (cycle >= 3) { resumed.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
            }
            string delayed = presentation == "school-report" ? "study.report.status" : "manual.study.report.status";
            if (duringRead && cycles == 1 && capability == delayed)
            { reading.TrySetResult(); await releaseRead.Task.WaitAsync(stop.Token); }
            return new HostResponse(1, request.RequestId, "Succeeded", null, "isolated study/notice priority",
                Study: capability.StartsWith("study.status", StringComparison.Ordinal) ? outside
                    : capability == "manual.study.status" && presentation == "manual" ? manual : null,
                StudyReport: capability == "study.report.status" && presentation == "school-report" ||
                    capability == "manual.study.report.status" && presentation == "manual-report" ? report : null);
        }, stop.Token, supportsCalibrationStatus: true));
        var main = PrivateWorkspace(pipe);
        var notice = new SchoolNotificationWindow(SchoolNotification.Preview(SchoolNotificationPriority.Minor));
        var manualWindow = new StudyWindow(pipe, () => { }, muted: true, manual: true);
        manualWindow.Apply(manual, null);
        typeof(MainWindow).GetField("_manualStudyWindow", WorkspaceFlags)!.SetValue(main, manualWindow);
        var reportWindow = new StudyReportWindow();
        typeof(MainWindow).GetField("_studyReportWindow", WorkspaceFlags)!.SetValue(main, reportWindow);
        Task? polling = null;
        void Block(bool value) => typeof(MainWindow).GetField(blocker, WorkspaceFlags)!.SetValue(main,
            blocker == "_notificationOpening" ? value : value ? notice : null);
        bool AllHidden() => !manualWindow.IsVisible && !reportWindow.IsVisible &&
            WorkspaceField<StudyWindow>(main, "_studyWindow")?.IsVisible != true;
        try
        {
            if (presentation == "manual") manualWindow.ShowDisplay();
            else reportWindow.Apply(report, TimeSpan.Zero);
            if (!duringRead) Block(true);
            polling = (Task)typeof(MainWindow).GetMethod("NoiseDisplayPollAsync", WorkspaceFlags)!.Invoke(main, null)!;
            if (duringRead)
            {
                PumpUntil(() => reading.Task.IsCompleted || polling.IsCompleted, "report read did not start");
                Block(true);
                releaseRead.TrySetResult();
            }
            PumpUntil(() => nextPoll.Task.IsCompleted || polling.IsCompleted, "notification priority cycle did not finish");
            Assert(!polling.IsCompleted, "notification priority stopped display polling: " + polling.Exception);
            Assert(AllHidden(), "school notification allowed " + presentation + " to remain visible or reopen");
            Block(false); releaseNext.TrySetResult();
            PumpUntil(() => resumed.Task.IsCompleted || polling.IsCompleted, "display did not resume after notification");
            Assert(!polling.IsCompleted && (presentation == "manual" ? manualWindow.IsVisible : reportWindow.IsVisible),
                "closing notification did not restore the eligible study surface");
            Checks.Add($"{presentation}/{blocker}/{(duringRead ? "during-read" : "before-read")}: notification suppresses study, late reports cannot reopen it, closing restores display without capture or session mutations");
        }
        finally
        {
            Block(false); main.Close(); notice.Shutdown(); stop.Cancel();
            if (polling is not null) { PumpUntil(() => polling.IsCompleted, "notification priority cleanup stalled"); polling.GetAwaiter().GetResult(); }
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }

    private static void WorkspaceNotificationPreviewHidesStudy()
    {
        string pipe = "NPEduTools.Test.study-notice-preview." + Guid.NewGuid().ToString("N");
        var main = PrivateWorkspace(pipe);
        var manual = new StudyWindow(pipe, () => { }, muted: true, manual: true);
        var report = new StudyReportWindow();
        typeof(MainWindow).GetField("_manualStudyWindow", WorkspaceFlags)!.SetValue(main, manual);
        typeof(MainWindow).GetField("_studyReportWindow", WorkspaceFlags)!.SetValue(main, report);
        try
        {
            manual.Apply(StudyFixture(), null); manual.ShowDisplay(); report.Show();
            WorkspaceCall(main, "NotificationPreviewClicked", new Button { Tag = "Minor" }, new RoutedEventArgs());
            var preview = WorkspaceField<SchoolNotificationWindow>(main, "_notificationPreview");
            Assert(preview?.IsVisible == true && !manual.IsVisible && !report.IsVisible,
                "opening notification preview did not immediately hide manual study and its report");
            preview!.Shutdown();
            Assert(WorkspaceField<SchoolNotificationWindow>(main, "_notificationPreview") is null,
                "closing notification preview retained its display blocker");
            Checks.Add("Actual notification preview event immediately hides manual display/report and releases its blocker on close; no Host or physical capture");
        }
        finally { main.Close(); }
    }
}
