using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private const BindingFlags WorkspaceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainWindow PrivateWorkspace(string pipe) => (MainWindow)typeof(MainWindow).GetConstructors(WorkspaceFlags).Single().Invoke([pipe, null, false]);
    private static void WorkspaceCall(MainWindow main, string method, params object[] values) => typeof(MainWindow).GetMethod(method, WorkspaceFlags)!.Invoke(main, values);
    private static T WorkspaceField<T>(MainWindow main, string name) where T : class => (T)typeof(MainWindow).GetField(name, WorkspaceFlags)!.GetValue(main)!;
    private sealed class WorkspaceSyntheticCapture : INoiseCapture
    {
        public string DeviceName => "隔离合成监测输入";
        public event Action<NoiseFrame>? Frame;
        public event Action<string>? Failed { add { } remove { } }
        private System.Threading.Timer? _timer;
        public void Start() => _timer = new(_ => Frame?.Invoke(new(.1, .000001, .01, 0, false)), null, 10, 100);
        public void Dispose() => _timer?.Dispose();
    }
    private static void RunWorkspaceRaceChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var errors = new List<Exception>();
            foreach (var check in new Action[] { WorkspaceVisibilityRace, WorkspaceLateStartRace, WorkspaceLateStartObserverRace, WorkspaceReportLeaveRace,
                WorkspaceSchoolReportSurvives, WorkspaceManualReportSurvives, WorkspaceSchoolReportSurvivesProtocolFailure, WorkspaceManualReportSurvivesProtocolFailure,
                WorkspaceCalibrationProtocolFailures, WorkspaceSchoolProtocolFailure,
                WorkspacePresentationAccessFailure, WorkspaceSeatingAccessFailure, WorkspaceManualEndAccessFailure })
                try { check(); } catch (Exception e) { errors.Add(new InvalidOperationException(check.Method.Name + ": " + e.Message, e)); }
            foreach (string scenario in new[] { "rejected-report", "empty-report", "accepted-report", "delayed-manual", "delayed-school", "delayed-school-withdrawn", "delayed-school-projection", "school-report" })
                try { WorkspaceDisplayPipelineCheck(scenario); } catch (Exception e) { errors.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void WorkspaceVisibilityRace()
    {
        string folder = Path.Combine(_output, "background-fixture"), pipe = "NPEduTools.Test.workspace-background." + Guid.NewGuid().ToString("N");
        var noise = new NoiseService(_ => new WorkspaceSyntheticCapture(), () => [new("synthetic", "隔离合成监测输入")], directory: folder);
        var manual = new ManualStudyService(noise, () => (null, false, StudyFixture()), () => false, folder);
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, manualStudy: manual).RunAsync(stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "ShowStudyWorkspace", "Manual");
                var view = WorkspaceField<NoiseWindow>(main, "_noiseWindow");
                PumpUntil(() => Button(view, "StartButton").IsEnabled, "manual fixture did not connect");
                Click(view, "StartButton");
                PumpUntil(() => noise.Snapshot().State == "Active" && Button(view, "StopButton").IsEnabled, "synthetic formal monitoring did not start");
                var session = noise.Snapshot().SessionId; long frames = noise.Snapshot().Summary!.Frames;
                WorkspaceCall(main, "HomeNavClicked", main, new RoutedEventArgs());
                PumpUntil(() => noise.Snapshot().Summary!.Frames > frames + 2, "leaving page stopped background samples");
                WorkspaceCall(main, "ShowStudyWorkspace", "Manual");
                main.UpdateLayout();
                Assert(((TextBlock)view.FindName("LevelText")).Text == "—" && !Button(view, "StopButton").IsEnabled,
                    "returning page exposes the old live reading and command before refresh");
                PumpUntil(() => Button(view, "StopButton").IsEnabled, "returning page never recovered fresh state");
                Assert(noise.Snapshot().SessionId == session, "returning page replaced the monitoring session");
                WorkspaceCall(main, "ShowStudyWorkspace", "School");
                var school = WorkspaceField<StudyMonitoringWindow>(main, "_studyMonitoringWindow");
                typeof(StudyMonitoringWindow).GetMethod("Render", WorkspaceFlags | BindingFlags.DeclaredOnly)!.Invoke(school,
                    [StudyFixture() with { PolicyState = "APPLIED", ClockReady = true, Phase = "Active", Execution = new("Schedule", "Active", Guid.NewGuid(), "fixture", true, null, null, true) }]);
                WorkspaceCall(main, "HomeNavClicked", main, new RoutedEventArgs());
                WorkspaceCall(main, "ShowStudyWorkspace", "School"); main.UpdateLayout();
                Assert(!((TextBlock)school.FindName("StudyCapture")).Text.Contains("正在采集"), "school page reuses a hidden fresh-signal claim");
                Assert(noise.Snapshot().SessionId == session, "school page navigation stopped manual capture");
                Checks.Add("Private Host + synthetic continuous samples: page navigation preserves session and growing frames; returning manual/school pages withdraw stale state before refresh");
                main.Close();
            });
        }
        finally
        {
            main.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            var manualDisposal = manual.DisposeAsync().AsTask(); PumpUntil(() => manualDisposal.IsCompleted,"manual fixture cleanup stalled"); manualDisposal.GetAwaiter().GetResult();
            var disposal = noise.DisposeAsync().AsTask(); PumpUntil(() => disposal.IsCompleted,"noise fixture cleanup stalled"); disposal.GetAwaiter().GetResult();
        }
    }
    private static async Task WorkspaceProxy(string pipe, Func<HostRequest, Task<HostResponse>> handle, CancellationToken token, bool supportsCalibrationStatus = false)
    {
        async Task Loop()
        {
            while (!token.IsCancellationRequested)
            {
                await using var socket = new NamedPipeServerStream(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await socket.WaitForConnectionAsync(token);
                    var request = await Protocol.ReadAsync<HostRequest>(socket, token);
                    // Existing fixtures emulate an older Host; new metadata is opt-in and must fall back.
                    var response = !supportsCalibrationStatus && NoiseCalibrationStatusContract.BaseCapability(request.Capability) is not null
                        ? new HostResponse(1, request.RequestId, "Rejected", "UnknownCapability", "old fixture Host")
                        : await handle(request);
                    await Protocol.WriteAsync(socket, response, token);
                }
                catch (IOException) { } // Closed UI transport is intentional, not a replay trigger.
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Loop()));
    }
    private static void WorkspaceLateStartRace() => WorkspaceLateStartRace(true);
    private static void WorkspaceLateStartObserverRace() => WorkspaceLateStartRace(false);
    private static void WorkspaceLateStartRace(bool owned)
    {
        const string secret = "fixture-late-reference";
        string pipe = "NPEduTools.Test.workspace-late-reference." + Guid.NewGuid().ToString("N"), folder = Path.Combine(_output, owned ? "late-reference-fixture" : "late-observer-fixture");
        var store = new NoiseManagementStore(folder); Assert(store.Configure(null, secret, false) is null, "fixture credential failed");
        var backend = new MeterUiBackend { Value = 60 };
        var meter = new SoundMeterService(() => backend, value => value == secret ? null : "MANAGEMENT_SECRET_INVALID");
        UsbUiCapture? capture = null;
        var noise = new NoiseService(_ => throw new Exception("No formal/physical input"), () => [new("synthetic", "Synthetic")], directory: folder, management: store,
            referenceCreate: _ => capture = new(-30), referenceMeterOpen: meter.OpenReferenceAsync);
        var state = noise.Snapshot(); noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", state.InstanceId, state.Revision, "synthetic")));
        var referenceState = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.status")).NoiseReference!;
        var begin = noise.ReferenceHandle(new(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: new("begin", referenceState.InstanceId, referenceState.Revision,
            secret, Instrument: "DLY-2202", TimeWeighting: "Slow", StabilityDb: 1, CheckToleranceDb: 1, MeterDevicePath: "synthetic-meter", Workflow: "Stable")));
        Assert(begin.Outcome == "Succeeded", "reference draft fixture failed");
        using var stop = new CancellationTokenSource();
        var delayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellations = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            if (request.Capability == "noise.management.status") return noise.ManagementHandle(request);
            if (request.Capability.StartsWith("noise.reference."))
            {
                if (!owned && request.NoiseReference?.Action == "start")
                {
                    var other = noise.ReferenceHandle(request with { RequestId = Guid.NewGuid() });
                    Assert(other.Outcome == "Accepted", "independent client did not own the synthetic sample");
                }
                var result = noise.ReferenceHandle(request);
                if (request.NoiseReference?.Action == "start") { delayed.TrySetResult(); await release.Task.WaitAsync(stop.Token); }
                if (request.NoiseReference?.Action == "cancel" && result.Outcome == "Succeeded") Interlocked.Increment(ref cancellations);
                return result;
            }
            return new(1, request.RequestId, "Rejected", "UnknownCapability", "fixture unsupported");
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "WorkspaceCalibrationClicked", main, new RoutedEventArgs());
                var view = new NoiseReferenceWindow(pipe, "synthetic-meter");
                bool referenceClosed = false;
                view.Closed += (_, _) => referenceClosed = true;
                ((PasswordBox)view.FindName("ManagementSecret")).Password = secret;
                WorkspaceCall(main, "ShowWorkspaceReference", view);
                PumpUntil(() => Button(view, "StartSample").IsEnabled, "reference fixture not connected");
                Click(view, "StartSample"); PumpUntil(() => delayed.Task.IsCompleted && backend.Opens == 1, "start did not reach backend before receipt");
                WorkspaceCall(main, "HomeNavClicked", main, new RoutedEventArgs());
                release.TrySetResult();
                if (owned)
                    PumpUntil(() => referenceClosed && cancellations == 1 && backend.Disposals == 1 && capture?.Disposed == true,
                        "leaving during delayed acceptance did not cancel its own capture");
                else
                {
                    PumpUntil(() => referenceClosed,"rejected start did not close after delayed receipt");
                    Assert(cancellations == 0 && capture is { Disposed: false } && backend.Disposals == 0, "rejected late start cancelled another client sample");
                }
                Assert(((PasswordBox)view.FindName("ManagementSecret")).Password.Length == 0, "closed reference retained credential");
                Checks.Add(owned ? "Delayed real Host reference receipt + synthetic HID/microphone: immediate navigation awaits ownership then cancels exactly once, releases both devices, clears secret" : "Delayed rejected start + another client sample: navigation clears secret without cancelling the other owner; synthetic only");
                main.Close();
            });
        }
        finally
        {
            release.TrySetResult(); main.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            var disposal = noise.DisposeAsync().AsTask(); PumpUntil(() => disposal.IsCompleted,"reference fixture cleanup stalled"); disposal.GetAwaiter().GetResult();
        }
    }
    private static void WorkspaceReportLeaveRace()
    {
        string pipe = "NPEduTools.Test.workspace-report-leave." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        using var waiting = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var received = Task.Run(async () => { await waiting.WaitForConnectionAsync(stop.Token); return await Protocol.ReadAsync<HostRequest>(waiting, stop.Token); });
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "ShowStudyWorkspace", "Reports");
                PumpUntil(() => received.IsCompleted, "report request not observed");
                Assert(received.Result.Capability is "study.report.status" or "manual.study.report.status", "unexpected report request");
                WorkspaceCall(main, "HomeNavClicked", main, new RoutedEventArgs());
                var closing = Task.Run(async () => { try { return await waiting.ReadAsync(new byte[1], stop.Token); } catch (IOException) { return 0; } });
                PumpUntil(() => closing.IsCompleted && closing.Result == 0, "leaving report page retains its pending transport", 2);
                Assert(((FrameworkElement)main.FindName("HomePage")).IsVisible, "late report changed navigation");
                Checks.Add("Stalled private report transport: leaving page cancels pending read immediately; no stale navigation or accumulating sockets");
                main.Close();
            });
        }
        finally { main.Close(); stop.Cancel(); }
    }
    private static void WorkspaceSchoolReportSurvives() => WorkspaceIndependentReport("study.report.status", false);
    private static void WorkspaceManualReportSurvives() => WorkspaceIndependentReport("manual.study.report.status", false);
    private static void WorkspaceSchoolReportSurvivesProtocolFailure() => WorkspaceIndependentReport("study.report.status", true);
    private static void WorkspaceManualReportSurvivesProtocolFailure() => WorkspaceIndependentReport("manual.study.report.status", true);
    private static void WorkspaceIndependentReport(string successfulCapability, bool invalidProtocol)
    {
        string pipe = "NPEduTools.Test.workspace-independent-report." + Guid.NewGuid().ToString("N");
        var data = new StudyReportData(StudyFixture().Window!, 600, 590, 590d / 600, .1, 100, 2,
            new("dBFS", 590, -55, -30, 580, 30, 10, []), null, true, []);
        using var stop = new CancellationTokenSource();
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            requests.Enqueue(request.Capability);
            if (request.Capability != successfulCapability && !invalidProtocol) throw new IOException("Fixture closes before replying");
            return Task.FromResult(request.Capability == successfulCapability
                ? new HostResponse(1, request.RequestId, "Succeeded", null, "fixture",
                    StudyReport: new(Guid.NewGuid(), "fixture", true, "Ended", 0, data))
                : new HostResponse(2, request.RequestId, "Succeeded", null, "deliberate invalid protocol"));
        }, stop.Token));
        var main = PrivateWorkspace(pipe);
        try
        {
            Exercise(main, owner =>
            {
                WorkspaceCall(main, "ShowStudyWorkspace", "Reports");
                var school = (TextBlock)main.FindName("WorkspaceSchoolReport");
                var manual = (TextBlock)main.FindName("WorkspaceManualReport");
                try { PumpUntil(() => !school.Text.Contains("正在读取") && !manual.Text.Contains("正在读取"), "independent reports did not settle", 7); }
                finally { File.WriteAllText(Path.Combine(_output, successfulCapability + (invalidProtocol ? ".protocol" : ".disconnect") + ".txt"),
                    string.Join(",", requests) + "\n" + school.Text + "\n" + manual.Text + "\n" +
                    typeof(MainWindow).GetField("_workspaceReportGeneration", WorkspaceFlags)!.GetValue(main)); }
                var successful = successfulCapability == "study.report.status" ? school : manual;
                var failed = ReferenceEquals(successful, school) ? manual : school;
                Assert(successful.Text.Contains("dBFS") && successful.Text.Contains("提醒 2 次"), "one failed report erased the independently available report");
                Assert(failed.Text.Contains("未确认") && !failed.Text.Contains("dBFS"), "failed report reused a successful or stale report");
                Checks.Add(successfulCapability + ": private " + (invalidProtocol ? "protocol mismatch" : "disconnect") + " in the other source preserves this report and labels only the failed source unknown");
                main.Close();
            });
        }
        finally { main.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
    private static void WorkspaceCalibrationProtocolFailures()
    {
        var failures = new List<Exception>();
        foreach (bool brokenAccess in new[] { true, false })
        foreach (string kind in new[] { "Quick", "Reference", "Usb" })
        {
            string pipe = "NPEduTools.Test.workspace-calibration-protocol." + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_output, kind + "-" + brokenAccess);
            var store = new NoiseManagementStore(folder);
            const string secret = "fixture-protocol-check";
            Assert(store.Configure(null, secret, false) is null, "fixture credential failed");
            var noise = new NoiseService(_ => throw new Exception("No physical input allowed"), () => [], directory: folder, management: store);
            using var stop = new CancellationTokenSource();
            int recovered = 0;
            var server = Task.Run(() => WorkspaceProxy(pipe, request => Task.FromResult(
                Volatile.Read(ref recovered) != 0 ? request.Capability switch
                {
                    "noise.management.status" => noise.ManagementHandle(request),
                    "noise.calibration.status" => noise.CalibrationHandle(request),
                    "noise.reference.status" => noise.ReferenceHandle(request),
                    _ => new HostResponse(1, request.RequestId, "Succeeded", null, "empty synthetic list", SoundMeter: new([]))
                } : !brokenAccess && request.Capability == "noise.management.status" ? noise.ManagementHandle(request) :
                new HostResponse(2, request.RequestId, "Succeeded", null, "deliberate protocol mismatch")), stop.Token));
            Window view = kind switch { "Quick" => new NoiseCalibrationWindow(pipe), "Reference" => new NoiseReferenceWindow(pipe), _ => new SoundMeterWindow(pipe) };
            try
            {
                ((PasswordBox)view.FindName("ManagementSecret")).Password = secret;
                // Invoke without showing: exercise real IPC/UI state, never Load a
                // device loop or start hardware. Await instead of crashing async void.
                string method = kind == "Usb" ? "ExecuteAsync" : "ReadCoreAsync";
                var task = (Task)view.GetType().GetMethod(method, WorkspaceFlags)!.Invoke(view, kind == "Usb" ? ["list"] : [true])!;
                PumpUntil(() => task.IsCompleted, "protocol fixture timed out");
                task.GetAwaiter().GetResult();
                string message = ((TextBlock)view.FindName(kind == "Usb" ? "Message" : "OperationMessage")).Text;
                Assert(message.Contains("后台") && (message.Contains("未确认") || message.Contains("不可用")), "protocol failure lacks usable retry feedback");
                Assert((bool)view.GetType().GetField("_busy", WorkspaceFlags)!.GetValue(view)! == false, "protocol failure left calibration busy");
                Volatile.Write(ref recovered, 1);
                var retry = (Task)view.GetType().GetMethod(method, WorkspaceFlags)!.Invoke(view, kind == "Usb" ? ["list"] : [true])!;
                PumpUntil(() => retry.IsCompleted, "recovered calibration retry timed out");
                retry.GetAwaiter().GetResult();
                Assert(kind == "Usb" ? ((TextBlock)view.FindName("Message")).Text.Contains("验证通过") :
                    (bool)view.GetType().GetField("_connected", WorkspaceFlags)!.GetValue(view)!, "valid retry did not recover calibration state");
                Checks.Add(kind + ": invalid " + (brokenAccess ? "management" : "status/command") + " protocol is handled, states remain unconfirmed, valid retry reconnects; no sensor access");
            }
            catch (Exception error) { failures.Add(new InvalidOperationException(kind + "/" + brokenAccess + ": " + error.Message, error)); }
            finally
            {
                view.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private static void WorkspaceSchoolProtocolFailure()
    {
        string pipe = "NPEduTools.Test.workspace-school-protocol." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request => Task.FromResult(
            new HostResponse(2, request.RequestId, "Succeeded", null, "deliberate protocol mismatch")), stop.Token));
        var view = new StudyMonitoringWindow(pipe, () => { });
        try
        {
            typeof(StudyMonitoringWindow).GetMethod("Render", WorkspaceFlags | BindingFlags.DeclaredOnly)!.Invoke(view, [StudyFixture()]);
            Assert(((TextBlock)view.FindName("StudyCapture")).Text.Contains("正在采集"), "fresh school fixture was not rendered");
            var task = (Task)typeof(StudyMonitoringWindow).GetMethod("ReadAsync", WorkspaceFlags)!.Invoke(view, null)!;
            PumpUntil(() => task.IsCompleted, "school protocol failure timed out");
            task.GetAwaiter().GetResult();
            Assert(((TextBlock)view.FindName("StudyCapture")).Text == "采集状态待确认" &&
                ((TextBlock)view.FindName("StudyPhase")).Text == "学校安排状态待确认", "protocol failure retained fresh school execution claim");
            Checks.Add("School monitor: invalid private protocol withdraws previous fresh execution state and returns to unknown without throwing or stopping Host");
        }
        finally { view.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
