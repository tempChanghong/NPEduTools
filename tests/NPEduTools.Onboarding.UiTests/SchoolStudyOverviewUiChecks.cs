using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSchoolStudyOverviewChecks()
    {
        string pipe = "NPEduTools.Test.school-overview." + Guid.NewGuid().ToString("N");
        var active = StudyFixture();
        var window = active.Window!;
        static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        active = active with
        {
            Next = new(window.Start.AddDays(1), window.End.AddDays(1)),
            Display = active.Display! with { Available = false, HiddenRemainingSeconds = 600 },
            Execution = active.Execution! with { Window = new(Stamp(window.Start), Stamp(window.End)) }
        };
        var current = active;
        var session = active.Execution!.SessionId!.Value;
        var instance = Guid.NewGuid();
        var noise = new NoiseState(instance, 4, "Active", "Synthetic", "fixture", "隔离合成麦克风", null, null, "Good", null, [],
            SessionId: session, SelectedDeviceId: "fixture");
        var protection = new NoiseProtectionState(true, true, instance, session, ManagementSource: "School",
            ClassroomPolicy: new(new string('a', 64), "password", "deny"));
        var devices = new StudyDeviceService(() => [], Path.Combine(_output, "school-overview-fixture")).Snapshot();
        int microphoneMissing = 0;
        using var stop = new CancellationTokenSource();
        var writes = new ConcurrentQueue<HostRequest>();
        TaskCompletionSource? gate = null;
        var readWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            string capability = request.Capability;
            if (capability.StartsWith("study.status", StringComparison.Ordinal))
            {
                var pending = Volatile.Read(ref gate);
                if (pending is not null)
                {
                    readWaiting.TrySetResult();
                    await pending.Task.WaitAsync(stop.Token);
                }
                return new(1, request.RequestId, "Succeeded", null, "", Study: Volatile.Read(ref current), Noise: noise);
            }
            if (capability.StartsWith("noise.management.status", StringComparison.Ordinal))
                return new(1, request.RequestId, "Succeeded", null, "", NoiseProtection: protection);
            if (capability.StartsWith("noise.devices", StringComparison.Ordinal))
                return new(1, request.RequestId, "Succeeded", null, "", Noise: noise,
                    NoiseDevices: Volatile.Read(ref microphoneMissing) == 0 ? [new("fixture", "隔离合成麦克风")] : []);
            if (capability is "study.devices.refresh-microphone-policy" or "study.devices.refresh")
                return new(1, request.RequestId, "Succeeded", null, "", StudyDevices: devices);
            if (capability is "study.show" or "noise.command" or "noise.management.command")
                writes.Enqueue(request);
            return new(1, request.RequestId, "Rejected", "UnknownCapability", "isolated overview");
        }, stop.Token, supportsCalibrationStatus: true, supportsSchoolMicrophonePolicy: true));
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, _ =>
            {
                Click(main, "StudyNav");
                Click(main, "SchoolWorkspaceTab");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                T Item<T>(string name) where T : FrameworkElement => (T)school.FindName(name);
                var scroll = Item<ScrollViewer>("PageScroll");
                var phase = Item<TextBlock>("StudyPhase");
                var schedule = Item<TextBlock>("StudySchedule");
                var next = Item<TextBlock>("StudyNext");
                var capture = Item<TextBlock>("StudyCapture");
                var presentation = Item<TextBlock>("StudyPresentation");
                var returning = Item<Button>("ReturnStudyButton");
                var stopButton = Item<Button>("StopScheduledButton");
                var seating = Item<Button>("SchoolSeatingButton");
                var equipment = Item<TextBlock>("DeviceWorkspaceStatus");
                PumpUntil(() => returning.IsVisible && stopButton.IsEnabled && equipment.Text.Contains("麦克风已配置"),
                    "embedded school page did not receive the current schedule, scoped stop and equipment state");

                foreach (var (width, height) in new[] { (1040, 760), (840, 600) })
                {
                    main.Width = width;
                    main.Height = height;
                    scroll.ScrollToTop();
                    main.UpdateLayout();
                    PumpUntil(() => Math.Abs(main.ActualWidth - width) < 1 && Math.Abs(main.ActualHeight - height) < 1,
                        "school overview did not reach the requested size");
                    Assert(!school.IsVisible && Window.GetWindow(scroll) == main &&
                        new[] { "MinimizeWindow", "MaximizeRestoreWindow", "CloseWindow" }.All(id => Find(main, id) is Button { IsVisible: true }),
                        "school overview detached from the main window or lost its caption controls");
                    foreach (var item in new[] { phase, schedule, next, capture, presentation, equipment, Item<TextBlock>("CalibrationStatus") }) InViewport(item);
                    foreach (var button in new[] { returning, stopButton, seating })
                    {
                        Rect bounds = Bounds(button, main);
                        Assert(button.IsVisible && bounds.Width > 0 && bounds.Height >= 32 && bounds.Left >= 0 &&
                            bounds.Right <= main.ActualWidth && bounds.Bottom < main.ActualHeight - 4,
                            $"{button.Name} is outside the main-window action row");
                    }
                    Assert(Bounds(capture, scroll).Top < Bounds(next, scroll).Top,
                        "the active overview puts next-session information before current capture");
                    Assert(!Item<Button>("OpenMeterCalibration").IsVisible,
                        "a low-frequency calibration action is expanded on the overview");
                    Snapshot(main, $"school-active-{width}x{height}.png");
                }
                var calibration = Item<Expander>("CalibrationExpander");
                calibration.IsExpanded = true;
                main.UpdateLayout();
                Assert(new[] { "OpenMeterCalibration", "QuickCalibrationButton", "ManualReferenceButton" }.All(name => Item<Button>(name).IsVisible),
                    "one of the three original calibration entry points is hidden after expanding");
                calibration.IsExpanded = false;
                scroll.ScrollToTop();
                Checks.Add("School study in the real 1040x760/840x600 main window: current period, capture, display, equipment and all eligible classroom actions fit; all three original calibration actions stay in a default-collapsed section.");

                Volatile.Write(ref current, active with { ProjectionPriority = true });
                PumpUntil(() => presentation.Text.Contains("考试放映优先") && !returning.IsVisible, "exam screening did not withdraw the return action");
                Assert(capture.Text.Contains("正在采集") && stopButton.IsEnabled, "exam screening changed the independent capture or stop status");
                Snapshot(main, "school-projection-840x600.png");

                Volatile.Write(ref current, active with { Phase = "Outside", Window = null, Execution = null, Display = null });
                PumpUntil(() => phase.Text.Contains("不在自习时段") && !stopButton.IsVisible, "outside-window status still shows the previous stop action");
                Assert(Bounds(next, scroll).Top < Bounds(capture, scroll).Top && next.Text.Contains("2026-10-13"),
                    "the next confirmed school session does not come first outside an active period");
                Snapshot(main, "school-next-840x600.png");

                Volatile.Write(ref current, active with { ClockReady = false, SchoolNow = null, Reason = "SCHOOL_CLOCK_UNAVAILABLE" });
                Volatile.Write(ref microphoneMissing, 1);
                Click(main, "RefreshSchoolStudyOverview", automation: true);
                PumpUntil(() => phase.Text.Contains("时间") && !returning.IsVisible && equipment.Text.Contains("不可用"),
                    "clock loss did not take priority over stale active data, or the microphone fault is hidden");
                Assert(!capture.Text.Contains("正在采集") && !presentation.Text.Contains("已收起") && !stopButton.IsVisible,
                    "an old capture, display or stop remained live when the school clock was lost");
                InViewport(phase);
                InViewport(equipment);
                Snapshot(main, "school-clock-840x600.png");

                Volatile.Write(ref current, active);
                Volatile.Write(ref microphoneMissing, 0);
                PumpUntil(() => returning.IsVisible && stopButton.IsEnabled, "the original live state did not recover");
                Click(school, "DeviceWorkspaceEntry");
                Assert(((FrameworkElement)main.FindName("WorkspaceDevices")).IsVisible, "equipment did not open in the main window");
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref gate, hold);
                Click(main, "WorkspaceDevicesBack");
                PumpUntil(() => readWaiting.Task.IsCompleted, "return from equipment did not request new school status");
                Assert(!returning.IsVisible && !stopButton.IsVisible && !capture.Text.Contains("正在采集"),
                    "return from equipment used the old capture or its commands before a fresh read");
                Volatile.Write(ref gate, null);
                hold.TrySetResult();
                PumpUntil(() => returning.IsVisible && stopButton.IsEnabled && capture.Text.Contains("正在采集"),
                    "fresh school state did not restore after returning from equipment");
                Assert(writes.IsEmpty, "status changes or workspace navigation sent an unintended study, verification, or stop command");
                Checks.Add("School study projection priority, next session, clock loss and device return keep distinct evidence; no writes occur while reading and returning.");
                main.Close();

                void InViewport(FrameworkElement item)
                {
                    Rect bounds = Bounds(item, scroll);
                    Assert(item.IsVisible && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                        bounds.Right <= scroll.ViewportWidth + 1 && bounds.Bottom <= scroll.ViewportHeight + 1,
                        $"{item.Name} is clipped at {main.Width}x{main.Height}: {bounds.Bottom:0}/{scroll.ViewportHeight:0}");
                }
            });
        }
        finally
        {
            main.Close();
            stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Rect Bounds(FrameworkElement item, Visual parent) =>
        item.TransformToAncestor(parent).TransformBounds(new Rect(item.RenderSize));
}
