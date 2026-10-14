using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void WorkspaceDisplayPipelineCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.display-pipeline." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int schoolReads = 0, manualReads = 0, reportReads = 0;
        var study = StudyFixture();
        var absentSchool = study with { Phase = "Outside", Execution = null, Display = study.Display! with { Selected = false, Available = false } };
        var manual = study with { Mode = "Manual", ClockSource = "Windows", Execution = study.Execution! with { Owner = "Manual" } };
        var raw = new StudyReportChannel("dBFS", 600, -35, -18, 600, 20, 5, [new(0, 60, -35, -18)], 600);
        var data = new StudyReportData(study.Window!, 9000, 600, 600d / 9000, 0, 6000, 1, raw, null, true, [], "Manual", "Windows");
        var availableReport = new StudyReportState(Guid.NewGuid(), study.Version, true, "Report", 300, data);
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            HostResponse Reply(string outcome = "Succeeded", StudyHostState? state = null, StudyReportState? report = null) =>
                new(1, request.RequestId, outcome, outcome == "Rejected" ? "FixtureRejected" : null, "isolated display pipeline", Study: state, StudyReport: report);
            if (request.Capability is "study.status.photos" or "study.status.seating" or "study.status")
            {
                int cycle = Interlocked.Increment(ref schoolReads);
                bool delayedSchool = scenario.StartsWith("delayed-school", StringComparison.Ordinal);
                int nextCycle = delayedSchool ? 3 : 2;
                if (cycle == nextCycle)
                {
                    nextPoll.TrySetResult();
                    await releaseNext.Task.WaitAsync(stop.Token);
                }
                else if (cycle > nextCycle)
                {
                    recoveredPoll.TrySetResult();
                    await Task.Delay(Timeout.Infinite, stop.Token);
                }
                return Reply(state: scenario switch
                {
                    "delayed-school" => study,
                    "delayed-school-withdrawn" => cycle == 1 ? study : absentSchool,
                    "delayed-school-projection" => cycle == 1 ? study : study with { ProjectionPriority = true },
                    "school-report" => absentSchool with { Display = absentSchool.Display! with { Selected = true } },
                    _ => absentSchool
                });
            }
            if (request.Capability == "manual.study.status")
            {
                if (Interlocked.Increment(ref manualReads) == 1 && scenario.StartsWith("delayed-school", StringComparison.Ordinal)) await Task.Delay(2000, stop.Token);
                return Reply(state: scenario.StartsWith("delayed-school", StringComparison.Ordinal) || scenario == "school-report" ? null : manual);
            }
            if (request.Capability == "manual.study.report.status")
            {
                if (Interlocked.Increment(ref reportReads) == 1 && scenario == "delayed-manual") await Task.Delay(2000, stop.Token);
                return Reply(scenario == "rejected-report" ? "Rejected" : "Succeeded", report: scenario switch
                {
                    "delayed-manual" or "school-report" => availableReport with { Available = false, Data = null },
                    "empty-report" => availableReport with { Data = null },
                    _ => availableReport
                });
            }
            if (request.Capability == "study.report.status") return Reply(report: scenario == "school-report"
                ? availableReport with { Data = data with { Mode = "Daily", ClockSource = "ClassIsland" } }
                : availableReport with { Available = false, Data = null });
            // Read-only exit/recovery, legacy fallback and presentation receipts.
            Assert(request.Noise is null && request.ManualStudy is null && request.StudyHide is null, "display polling submitted a capture mutation");
            return Reply();
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        Task? polling = null;
        try
        {
            if (scenario is "accepted-report" or "delayed-school-withdrawn" or "delayed-school-projection")
            {
                var oldDisplay = new StudyWindow(pipe, () => { }, muted: true);
                oldDisplay.Apply(study, null); oldDisplay.ShowDisplay();
                typeof(MainWindow).GetField("_studyWindow", WorkspaceFlags)!.SetValue(main, oldDisplay);
            }
            polling = (Task)typeof(MainWindow).GetMethod("NoiseDisplayPollAsync", WorkspaceFlags)!.Invoke(main, null)!;
            PumpUntil(() => nextPoll.Task.IsCompleted || polling.IsCompleted, "display cycle did not finish", 8);
            Assert(!polling.IsCompleted, "display poll stopped unexpectedly: " + polling.Exception);
            var reportWindow = WorkspaceField<StudyReportWindow>(main, "_studyReportWindow");
            var manualWindow = WorkspaceField<StudyWindow>(main, "_manualStudyWindow");
            var schoolWindow = WorkspaceField<StudyWindow>(main, "_studyWindow");
            Assert((reportWindow?.IsVisible == true) == (scenario is "accepted-report" or "school-report"), "non-successful/empty report was presented as authoritative");
            if (scenario is "rejected-report" or "empty-report")
                Assert(manualWindow?.IsVisible == true, "invalid report suppressed a fresh manual display");
            if (scenario == "delayed-manual")
                Assert(manualWindow?.IsVisible != true && schoolWindow?.IsVisible != true, "time spent awaiting later reads was omitted from display freshness");
            if (scenario == "delayed-school") Assert(schoolWindow?.IsVisible == true, "fresh revalidation did not keep the school display visible after an unrelated slow read");
            if (scenario is "delayed-school-withdrawn" or "delayed-school-projection") Assert(schoolWindow?.IsVisible != true, "revalidation ignored newly withdrawn policy or examination priority");
            if (scenario == "accepted-report") Assert(schoolWindow?.IsVisible != true, "manual report left the previous school display visible");
            if (!scenario.StartsWith("delayed-school", StringComparison.Ordinal)) Assert(reportReads == 1, "fixture did not exercise manual report polling");
            int reportVisibilityChanges = 0;
            int schoolVisibilityChanges = 0;
            if (schoolWindow is not null) schoolWindow.IsVisibleChanged += (_, _) => schoolVisibilityChanges++;
            if (reportWindow is not null) reportWindow.IsVisibleChanged += (_, _) => reportVisibilityChanges++;
            releaseNext.TrySetResult();
            PumpUntil(() => recoveredPoll.Task.IsCompleted || polling.IsCompleted, "fresh display reconciliation did not finish", 8);
            Assert(!polling.IsCompleted, "display recovery stopped polling");
            if (scenario == "delayed-school") Assert(WorkspaceField<StudyWindow>(main, "_studyWindow")?.IsVisible == true, "fresh school status did not restore display");
            else if (scenario.StartsWith("delayed-school", StringComparison.Ordinal)) Assert(WorkspaceField<StudyWindow>(main, "_studyWindow")?.IsVisible != true, "withdrawn/priority school display reappeared");
            else if (scenario is not ("accepted-report" or "school-report")) Assert(WorkspaceField<StudyWindow>(main, "_manualStudyWindow")?.IsVisible == true, "fresh manual status did not restore display");
            else Assert(WorkspaceField<StudyReportWindow>(main, "_studyReportWindow")?.IsVisible == true, "valid report vanished during recovery");
            if (scenario == "school-report") Assert(reportVisibilityChanges == 0, "manual report probe hid/reopened the unchanged school report");
            if (scenario == "delayed-school") Assert(schoolVisibilityChanges == 0, "unrelated slow reads caused school display hide/show flicker");
            Checks.Add(scenario + ": actual MainWindow display loop/private pipe validates report evidence, ages snapshots across downstream reads, and restores fresh display read-only; no capture or settings mutation");
        }
        finally
        {
            main.Close(); stop.Cancel();
            if (polling is not null) { PumpUntil(() => polling.IsCompleted, "display poll cleanup stalled"); polling.GetAwaiter().GetResult(); }
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
