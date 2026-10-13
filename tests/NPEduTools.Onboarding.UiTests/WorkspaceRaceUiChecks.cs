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
            foreach (var check in new Action[] { WorkspaceVisibilityRace, WorkspaceLateStartRace, WorkspaceLateStartObserverRace, WorkspaceReportLeaveRace })
                try { check(); } catch (Exception e) { errors.Add(new InvalidOperationException(check.Method.Name + ": " + e.Message, e)); }
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
    private static async Task WorkspaceProxy(string pipe, Func<HostRequest, Task<HostResponse>> handle, CancellationToken token)
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
                    var response = await handle(request);
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
                ((PasswordBox)view.FindName("ManagementSecret")).Password = secret;
                WorkspaceCall(main, "ShowWorkspaceReference", view);
                PumpUntil(() => Button(view, "StartSample").IsEnabled, "reference fixture not connected");
                Click(view, "StartSample"); PumpUntil(() => delayed.Task.IsCompleted && backend.Opens == 1, "start did not reach backend before receipt");
                WorkspaceCall(main, "HomeNavClicked", main, new RoutedEventArgs());
                release.TrySetResult();
                if (owned)
                    PumpUntil(() => cancellations == 1 && backend.Disposals == 1 && capture?.Disposed == true,
                        "leaving during delayed acceptance did not cancel its own capture");
                else
                {
                    PumpUntil(() => ((PasswordBox)view.FindName("ManagementSecret")).Password.Length == 0,"rejected start did not close after delayed receipt");
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
                Assert(received.Result.Capability == "study.report.status", "unexpected report request");
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
}
