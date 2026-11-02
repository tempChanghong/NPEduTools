using System.Reflection;
using System.Windows;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void ManualDisplayReadFailureCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.manual-continuity." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cycles = 0;
        var state = StudyFixture();
        var manual = new ManualStudyState(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, true, 600);
        state = state with { Execution = state.Execution with { Owner = "Manual" }, Manual = manual };
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            string capability = NoiseCalibrationStatusContract.BaseCapability(request.Capability) ?? request.Capability;
            bool school = capability is "study.status.photos" or "study.status.seating" or "study.status";
            if (school)
            {
                int cycle = Interlocked.Increment(ref cycles);
                if (cycle == 2) { nextPoll.TrySetResult(); await releaseNext.Task.WaitAsync(stop.Token); }
                if (cycle == 3) { recoveredPoll.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
            }
            bool report = capability == "manual.study.report.status";
            bool manualStatus = capability == "manual.study.status";
            if (cycles == 1 && manualStatus && scenario == "failed-manual-response")
                return new HostResponse(1, request.RequestId, "Failed", "UNAVAILABLE", "temporary read failure");
            bool error = cycles == 1 && (scenario == "failed-school-status" && school ||
                (scenario is "failed-manual-status" or "takeover-with-error" or "projection-with-error" or "hidden-with-error" or "expired-with-error" or "notification-with-error") && manualStatus ||
                (scenario is "failed-report" or "withdrawn-with-error" or "ended-with-error") && report);
            if (cycles == 1 && report && scenario == "slow-report") await Task.Delay(1700, stop.Token);
            var supplied = scenario == "withdrawn-with-error" ? state with { Display = state.Display! with { Available = false } } :
                scenario == "ended-with-error" ? state with { Phase = "Outside", Display = state.Display! with { Available = false }, Manual = manual with { State = "Ended" } } : state;
            var schoolState = scenario == "takeover-with-error" ? StudyFixture() :
                scenario == "projection-with-error" ? StudyFixture() with { Phase = "Outside", ProjectionPriority = true } : null;
            return new HostResponse(error ? 2 : 1, request.RequestId, "Succeeded", null, "isolated manual display fixture",
                Study: manualStatus ? supplied : school ? schoolState : null, ManualStudy: manualStatus ? supplied.Manual : null);
        }, stop.Token, supportsCalibrationStatus: true));
        var main = PrivateWorkspace(pipe);
        var clock = new StudyUiClock();
        var display = new StudyWindow(pipe, () => { }, muted: true, time: clock, manual: true);
        display.Apply(state, null); display.ShowDisplay();
        if (scenario == "hidden-with-error") display.HideDisplay();
        if (scenario == "expired-with-error") clock.Ms += (long)(state.Window!.End - state.SchoolNow!.Value).TotalMilliseconds + 5000;
        typeof(MainWindow).GetField("_manualStudyWindow", WorkspaceFlags)!.SetValue(main, display);
        if (scenario == "notification-with-error") typeof(MainWindow).GetField("_notificationOpening", WorkspaceFlags)!.SetValue(main, true);
        int changes = 0;
        display.IsVisibleChanged += (_, _) => changes++;
        Task? polling = null;
        try
        {
            polling = (Task)typeof(MainWindow).GetMethod("NoiseDisplayPollAsync", WorkspaceFlags)!.Invoke(main, null)!;
            PumpUntil(() => nextPoll.Task.IsCompleted || polling.IsCompleted, "manual next poll did not begin", 10);
            Assert(!polling.IsCompleted, "temporary error ended manual display polling");
            bool retain = scenario is "healthy" or "slow-report" or "failed-report" or "failed-manual-status" or "failed-manual-response" or "failed-school-status";
            Assert(display.IsVisible == retain, retain ? "temporary read hid an existing manual display" : "manual display ignored withdrawal or priority");
            if (retain)
            {
                Assert(changes == 0, "manual display flickered during auxiliary query");
                releaseNext.TrySetResult();
                PumpUntil(() => recoveredPoll.Task.IsCompleted || polling.IsCompleted, "manual display recovery stalled", 10);
                Assert(!polling.IsCompleted && display.IsVisible && changes == 0, "manual recovery reopened/stopped a healthy display");
                Assert(display.PresentationPulse()?.State == "Visible", "fresh manual recovery still reported stale");
            }
            Checks.Add("Manual " + scenario + ": real WPF/private pipe preserves only already-confirmed display; respects withdrawal, takeover and original expiry");
        }
        finally
        {
            main.Close(); stop.Cancel();
            if (polling is not null) { PumpUntil(() => polling.IsCompleted, "manual poll cleanup stalled"); polling.GetAwaiter().GetResult(); }
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
