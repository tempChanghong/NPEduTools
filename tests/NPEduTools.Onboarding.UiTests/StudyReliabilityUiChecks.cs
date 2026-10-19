using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunStudyReliabilityChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var errors = new List<Exception>();
            foreach (string scenario in new[] { "manual-status-error", "school-report-error", "school-status-error", "withdrawn-with-error", "projection-with-error", "notification-with-error", "clock-with-error", "policy-with-error", "hidden-status-error", "expired-status-error" })
                try { StudyTemporaryReadFailureCheck(scenario); } catch (Exception e) { errors.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
            try { StudyMediumHomeworkCheck(); } catch (Exception e) { errors.Add(e); }
            try { StudyContinuationExpiryCheck(); } catch (Exception e) { errors.Add(e); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void StudyTemporaryReadFailureCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.study-continuity." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        var state = StudyFixture();
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            string capability = NoiseCalibrationStatusContract.BaseCapability(request.Capability) ?? request.Capability;
            bool school = capability is "study.status.photos" or "study.status.seating" or "study.status";
            if (school)
            {
                int cycle = Interlocked.Increment(ref reads);
                if (cycle == 2) { nextPoll.TrySetResult(); await releaseNext.Task.WaitAsync(stop.Token); }
                if (cycle == 3) { recoveredPoll.TrySetResult(); await Task.Delay(Timeout.Infinite, stop.Token); }
            }
            bool primaryFailure = scenario is "school-status-error" or "hidden-status-error" or "expired-status-error";
            bool fail = reads == 1 && (primaryFailure && school || scenario == "school-report-error" && capability == "study.report.status" ||
                !primaryFailure && scenario != "school-report-error" && capability == "manual.study.status");
            var supplied = scenario == "withdrawn-with-error" ? state with { Display = state.Display! with { Available = false } } :
                scenario == "projection-with-error" ? state with { ProjectionPriority = true } :
                scenario == "clock-with-error" ? state with { ClockReady = false } :
                scenario == "policy-with-error" ? state with { PolicyState = "DISABLED" } : state;
            return new HostResponse(fail ? 2 : 1, request.RequestId, "Succeeded", null, "isolated transient status failure", Study: school ? supplied : null);
        }, stop.Token, supportsCalibrationStatus: true));
        var main = PrivateWorkspace(pipe);
        var clock = new StudyUiClock();
        var display = new StudyWindow(pipe, () => { }, muted: true, time: clock);
        display.Apply(state, null); display.ShowDisplay();
        if (scenario == "hidden-status-error") display.HideDisplay();
        if (scenario == "expired-status-error") clock.Ms += (long)(state.Window!.End - state.SchoolNow!.Value).TotalMilliseconds + 5000;
        typeof(MainWindow).GetField("_studyWindow", WorkspaceFlags)!.SetValue(main, display);
        if (scenario == "notification-with-error") typeof(MainWindow).GetField("_notificationOpening", WorkspaceFlags)!.SetValue(main, true);
        int visibilityChanges = 0;
        display.IsVisibleChanged += (_, _) => visibilityChanges++;
        Task? polling = null;
        try
        {
            polling = (Task)typeof(MainWindow).GetMethod("NoiseDisplayPollAsync", WorkspaceFlags)!.Invoke(main, null)!;
            PumpUntil(() => nextPoll.Task.IsCompleted || polling.IsCompleted, "next display poll did not begin", 8);
            Assert(!polling.IsCompleted, "transient error terminated polling");
            bool retain = scenario is "manual-status-error" or "school-report-error" or "school-status-error";
            Assert(display.IsVisible == retain, retain ? "one temporary read failure hid the confirmed school display" : "withdrawal/priority was ignored after a later read failed");
            if (retain) Assert(visibilityChanges == 0, "temporary read failure caused hide/show flicker");
            if (retain)
            {
                releaseNext.TrySetResult();
                PumpUntil(() => recoveredPoll.Task.IsCompleted || polling.IsCompleted, "display did not recover after transient read failure", 8);
                Assert(!polling.IsCompleted && display.IsVisible && visibilityChanges == 0, "read recovery caused hide/show flicker or stopped polling");
                Assert(display.PresentationPulse()?.State == "Visible", "recovered fresh state stayed stale");
            }
            Checks.Add(scenario + ": actual MainWindow/private pipe keeps only an already-confirmed display, respecting withdrawal and presentation priorities; no real Host or sensors");
        }
        finally
        {
            main.Close(); stop.Cancel();
            if (polling is not null) { PumpUntil(() => polling.IsCompleted, "display poll cleanup stalled"); polling.GetAwaiter().GetResult(); }
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
    private static void StudyMediumHomeworkCheck()
    {
        var clock = new StudyUiClock(); var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock);
        var window = new Window { Content = surface, Width = 1280, Height = 720, WindowStyle = WindowStyle.None };
        var state = StudyFixture() with { Homework = new("READY", new string('a', 64), 1, "2026-10-12", new string('a', 64)) };
        var items = Enumerable.Range(1, 6).Select(i => new StudyHomeworkItem("work" + i, 1, new[] { "语文", "数学", "英语", "物理", "化学", "生物" }[i - 1], "",
            "完成练习册第12—15页。\n订正第6、8题，写出过程。\n预习下一节并完成记录。", [new("one", "本班")])).ToArray();
        try
        {
            Exercise(window, shown =>
            {
                surface.Apply(state, null); surface.ApplyHomework(state.Homework!.SnapshotId!, new(state.Version!, "2026-10-12", items));
                var pages = (StudyHomeworkPage[])typeof(StudySurface).GetField("_pages", WorkspaceFlags)!.GetValue(surface)!;
                Assert(pages.Length == 2 && pages.All(page => page.Items.Count == 3 && page.Items.All(item => item.Parts == 1)), "six medium subjects were fragmented or wasted pages");
                var grid = (Grid)surface.FindName("HomeworkGrid");
                Assert(grid.Children.OfType<StackPanel>().All(child => Grid.GetRowSpan(child) == 2), "medium homework did not use the full available height");
                foreach (var child in grid.Children.OfType<StackPanel>()) { child.Measure(new Size(592, 302)); Assert(child.DesiredSize.Height <= 302, "medium homework overflows"); }
                Snapshot(shown, "study-six-medium-subjects.png");
                clock.Ms += 2000; typeof(StudySurface).GetMethod("Render", WorkspaceFlags | System.Reflection.BindingFlags.DeclaredOnly)!.Invoke(surface, null);
                Assert(grid.Children.Count == 3, "temporary local status staleness discarded previously loaded homework");
                Assert(((TextBlock)surface.FindName("NoiseStatus")).Text != "正在监测", "stale display still claimed fresh capture");
                Snapshot(shown, "study-six-medium-subjects-stale.png");
                Checks.Add("Six medium subjects: actual WPF heights, three full-height columns, two pages, no unnecessary parts, stale status preserves loaded text without claiming capture");
                shown.Close();
            });
        }
        finally { surface.Stop(); window.Close(); }
    }
    private static void StudyContinuationExpiryCheck()
    {
        var clock = new StudyUiClock(); var state = StudyFixture("Active", 8999);
        var window = new StudyWindow("NPEduTools.Test.expiry." + Guid.NewGuid().ToString("N"), () => { }, muted: true, time: clock);
        try
        {
            window.Apply(state, null); window.ShowDisplay(); Assert(window.CanKeepAfterReadFailure, "confirmed display cannot continue");
            clock.Ms += 6000;
            PumpUntil(() => !window.IsVisible, "cached display exceeded its deadline without any new pipe response");
            Assert(!window.CanKeepAfterReadFailure, "expired/hidden display remains eligible");
            Checks.Add("Confirmed display expires at original end plus end-notice duration using monotonic time even while status reads do not complete");
        }
        finally { window.Shutdown(); }
    }
}
