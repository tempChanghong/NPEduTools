using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class DeviceReader : ILessonStatusReader
    { public Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken token) => throw new InvalidOperationException("No real clock/sensors"); }
    private static void RunStudyDeviceChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Application.Current.Dispatcher));
        string folder = Path.Combine(_output, "device-fixture." + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string pipe = "NPEduTools.Test.study-device-ui." + Guid.NewGuid().ToString("N");
        int captures = 0;
        var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("Capture forbidden"); }, () => [new("mic-a", "同名麦克风"), new("mic-b", "同名麦克风")], directory: folder);
        StudyCameraDevice[] catalog = [new("cam-a", "同名摄像头"), new("cam-b", "同名摄像头")];
        var cameras = new StudyDeviceService(() => catalog, folder);
        using var lifetime = new CancellationTokenSource();
        var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(lifetime.Token));
        var saved = new List<OnboardingState>();
        var window = PreparationWindow(pipe, new(Features: OnboardingFeatures.Noise, Step: "noise"), saved.Add);
        try
        {
            RunStudyCameraConfigurationRemovalChecks();
            RunStudyDeviceSaveEvidenceChecks();
            RunStudyDeviceUnlockFeedbackChecks();
            RunStudyDeviceProtocolChecks();
            RunDeviceRefreshSnapshotChecks();
            RunDeviceRejectionStateChecks();
            RunSelectedInventoryUiChecks();
            RunSelectionCatalogUiChecks();
            Exercise(window, owner =>
            {
                var picker = (StudyDevicePicker)owner.FindName("StudyDevices");
                var mic = (ComboBox)picker.FindName("StudyMicrophone"); var cam = (ComboBox)picker.FindName("StudyCamera");
                var saveMic = (Button)picker.FindName("SaveMicrophone"); var saveCam = (Button)picker.FindName("SaveCamera");
                PumpUntil(() => mic.Items.Count == 2 && cam.Items.Count == 3, "OOBE native catalogs did not load");
                Assert(mic.SelectedIndex == -1 && cam.SelectedValue as string == "", "OOBE silently selected a microphone/camera");
                Click(owner, "NextButton"); PumpUntil(() => ((TextBlock)owner.FindName("ErrorText")).Text.Contains("设备"), "unsaved microphone advanced OOBE");
                Assert(saved.All(s => s.Step != "review"), "unconfigured step marked complete");
                mic.SelectedValue = "mic-b"; cam.SelectedValue = "cam-b";
                PumpUntil(() => saveMic.IsEnabled, "selected microphone save unavailable"); saveMic.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => noise.Snapshot().SelectedDeviceId == "mic-b" && saveCam.IsEnabled, "OOBE microphone was not saved");
                saveCam.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(() => cameras.Snapshot().CameraId == "cam-b" && saveCam.IsEnabled, "OOBE camera was not saved");
                Assert(noise.Snapshot().State == "Idle" && captures == 0, "selection started microphone capture");
                Snapshot(owner, "oobe-study-devices.png");
                Click(owner, "NextButton"); PumpUntil(() => saved.Any(s => s.Step == "review"), "saved devices did not advance OOBE");
                Assert(captures == 0, "OOBE completion started capture"); owner.Close();
                Checks.Add("OOBE actual WPF/private Host pipe: separate fixed microphone/camera selection, optional camera, no silent default, next requires saved mic; no capture");
            });
            var manage = new StudyMonitoringWindow(pipe, () => { });
            Exercise(manage, owner =>
            {
                var picker = (StudyDevicePicker)owner.FindName("Devices"); var cam = (ComboBox)picker.FindName("StudyCamera");
                PumpUntil(() => cam.SelectedValue as string == "cam-b", "management did not read the same Host camera preference");
                catalog = [new("cam-a", "同名摄像头")];
                var refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "hotplug refresh did not finish"); refresh.GetAwaiter().GetResult();
                Assert(cam.SelectedValue as string == "cam-b" && !((Button)picker.FindName("SaveCamera")).IsEnabled, "hotplug switched to another camera or accepted unavailable camera");
                Assert(((TextBlock)picker.FindName("CameraStatus")).Text.Contains("未确认"), "missing camera not indicated");
                var pickerFlags=BindingFlags.Instance|BindingFlags.NonPublic;
                typeof(StudyDevicePicker).GetField("_busy",pickerFlags)!.SetValue(picker,true);
                picker.CancelPending();
                Assert(!((Button)picker.FindName("SaveMicrophone")).IsEnabled && picker.Summary.Contains("待刷新"),"cancelled pending selection leaves stale save controls enabled");
                refresh=picker.RefreshAsync();PumpUntil(()=>refresh.IsCompleted,"cancelled preference state could not refresh");refresh.GetAwaiter().GetResult();
                typeof(StudyDevicePicker).GetField("_pipe",pickerFlags)!.SetValue(picker,"NPEduTools.Test.absent."+Guid.NewGuid().ToString("N"));
                ((Button)picker.FindName("SaveMicrophone")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                PumpUntil(()=>((TextBlock)picker.FindName("DeviceMessage")).Text.Contains("保存结果未确认"),"save connection timeout escaped or lacked feedback");
                Assert(!((Button)picker.FindName("SaveMicrophone")).IsEnabled,"uncertain save permits stale retry");
                typeof(StudyDevicePicker).GetField("_pipe",pickerFlags)!.SetValue(picker,pipe);
                refresh=picker.RefreshAsync();PumpUntil(()=>refresh.IsCompleted,"post-timeout preferences did not recover");refresh.GetAwaiter().GetResult();
                Checks.Add("Device selection: cancellation invalidates stale controls; absent private pipe save timeout has feedback and refresh recovery; no capture");
                typeof(StudyMonitoringWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(manage, [null]);
                Assert(!((TextBlock)manage.FindName("StudyCapture")).Text.Contains("正在采集"), "unknown Host claimed live capture");
                var next = new SchoolNoiseWindow(new(2026,10,13,23,0,0,TimeSpan.Zero),new(2026,10,14,1,0,0,TimeSpan.Zero));
                var nextState = new StudyHostState("fixture", "APPLIED", "NOT_INTEGRATED", "MANUAL", "Active", "WINDOW_ACTIVE", true,
                    next.Start.AddDays(-1), new(next.Start.AddDays(-1),next.End.AddDays(-1)), next, null);
                var render = typeof(StudyMonitoringWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                render.Invoke(manage, [nextState]);
                Assert(((TextBlock)manage.FindName("StudyNext")).Text.Contains("2026-10-14 01:00"), "current study hid next overnight window");
                render.Invoke(manage, [nextState with {ClockReady=false}]);
                Assert(!((TextBlock)manage.FindName("StudyNext")).Text.Contains("2026"), "stale clock retained predicted window");
                Assert(((TextBlock)manage.FindName("StudyNext")).Text.Contains("安排已收到"), "received configuration misreported as missing when clock unavailable");
                render.Invoke(manage, [nextState with {PolicyState="UNAVAILABLE",Reason="STUDY_OFFLINE"}]);
                Assert(((TextBlock)manage.FindName("StudyPhase")).Text.Contains("自动重试") &&
                    !((TextBlock)manage.FindName("StudyConfiguration")).Text.Contains("2026"), "transport failure lacks recovery feedback or retains old schedule details");
                var devicesPanel = (Expander)manage.FindName("DeviceSettings");
                Assert(!devicesPanel.IsExpanded && ((TextBlock)manage.FindName("DeviceSummary")).Text.Contains("麦克风已配置"), "compact device summary missing or settings expanded by default");
                var active = nextState with {Execution = new("Schedule", "Active", Guid.NewGuid(), "fixture", true, null, null, true)};
                render.Invoke(manage, [active]);
                Assert(((TextBlock)manage.FindName("StudyCapture")).Text.Contains("正在采集"), "fresh execution no longer shows sampling");
                render.Invoke(manage, [active with {Execution = active.Execution! with {SignalFresh=false}}]);
                Assert(!((TextBlock)manage.FindName("StudyCapture")).Text.Contains("正在采集"), "stale execution retained active badge");
                render.Invoke(manage, [active with {ProjectionPriority=true}]);
                Assert(((TextBlock)manage.FindName("StudyPresentation")).Text.Contains("考试放映优先"), "projection priority missing from compact summary");
                render.Invoke(manage,[active with {ProjectionPriority=true,ClockReady=false,Execution=null}]);
                Assert(((TextBlock)manage.FindName("StudyPresentation")).Text.Contains("展示暂缓") && !((TextBlock)manage.FindName("StudyCapture")).Text.Contains("正在采集"),"projection priority falsely claims backend sampling");
                render.Invoke(manage, [active]);
                owner.Width = 660; owner.Height = 600; ((ScrollViewer)owner.Content).ScrollToTop(); owner.UpdateLayout();
                var calibration = Button(owner, "OpenMeterCalibration");
                var actionPoint = calibration.TranslatePoint(new Point(), owner);
                Assert(actionPoint.X >= 0 && actionPoint.X + calibration.ActualWidth <= owner.ActualWidth && actionPoint.Y + calibration.ActualHeight < owner.ActualHeight - 30,
                    "common calibration action is clipped or below the first viewport in compact layout");
                Snapshot(owner, "study-monitoring-compact-active.png");
                devicesPanel.IsExpanded = true; owner.UpdateLayout();
                Assert(picker.IsVisible && captures == 0, "expanding shared device settings started capture or hid controls");
                devicesPanel.IsExpanded = false;
                render.Invoke(manage, [null]);
                Snapshot(owner, "study-monitoring-devices.png"); owner.Close();
                Checks.Add("Study management actual WPF/private Host pipe: compact first-screen actions, expandable shared devices, fresh/stale/unknown execution, next schedule and projection priority; no capture");
            });
            var main = (MainWindow)typeof(MainWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([pipe, null, false]);
            Exercise(main, owner =>
            {
                Assert(Find(owner, "HomeStudyPrimary") is Button, "home primary study entry missing");
                var studyNavigation = (Button)Find(owner, "StudyTab");
                var sidebarPoint = studyNavigation.TranslatePoint(new Point(), owner);
                Assert(sidebarPoint.Y >= 0 && sidebarPoint.Y + studyNavigation.ActualHeight <= owner.ActualHeight, "study navigation is clipped by the fixed sidebar footer");
                Assert(Find(owner, "RecordingNav") is Button && Find(owner, "HomeRecordingPlan") is Button && Find(owner, "OpenClassroomMode") is Button, "existing classroom navigation was lost");
                Assert(Find(owner, "StudyHomeNext") is TextBlock, "home next-study summary missing");
                var homeState = new StudyHostState("fixture", "APPLIED", "NOT_INTEGRATED", "MANUAL", "Active", "WINDOW_ACTIVE", true,
                    new(2026,10,12,19,0,0,TimeSpan.Zero), null, new(new(2026,10,13,19,0,0,TimeSpan.Zero),new(2026,10,13,21,30,0,TimeSpan.Zero)), null);
                var update = typeof(MainWindow).GetMethod("UpdateStudyHome", BindingFlags.Instance | BindingFlags.NonPublic)!;
                update.Invoke(main, [homeState]);
                Assert(((TextBlock)Find(owner, "StudyHomeNext")).Text.Contains("2026-10-13 19:00"), "home did not show next school study during manual monitoring");
                Snapshot(owner, "home-monitoring-layout.png"); owner.Close();
                Checks.Add("Main window actual WPF: unified study entry; existing home tools retained; no production App startup");
            });
        }
        finally
        {
            window.Shutdown(); lifetime.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult(); SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
    private static void RunStudyDeviceProtocolChecks()
    {
        var errors = new List<Exception>();
        foreach (string stage in new[] { "noise.devices", "study.devices.refresh", "study.devices.microphone" })
        {
            string pipe = "NPEduTools.Test.device-protocol." + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(_output, "device-protocol-" + stage);
            var noise = new NoiseService(_ => throw new Exception("Capture forbidden"), () => [new("mic", "Synthetic microphone")], directory: directory);
            var cameras = new StudyDeviceService(() => [], directory);
            using var stop = new CancellationTokenSource();
            int broken = 0;
            var server = Task.Run(() => WorkspaceProxy(pipe, request => Task.FromResult(
                Volatile.Read(ref broken) != 0 && request.Capability == stage ?
                new HostResponse(2, request.RequestId, "Succeeded", null, "deliberate protocol mismatch") :
                request.Capability == "noise.devices" ? noise.Handle(request) : cameras.Handle(request)), stop.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe);
            try
            {
                var read = picker.RefreshAsync(); PumpUntil(() => read.IsCompleted, "device fixture did not load"); read.GetAwaiter().GetResult();
                ((ComboBox)picker.FindName("StudyMicrophone")).SelectedValue = "mic";
                Assert(((Button)picker.FindName("SaveMicrophone")).IsEnabled, "fresh fixture cannot save selected synthetic microphone");
                Volatile.Write(ref broken, 1);
                Task failure;
                if (stage == "study.devices.microphone")
                {
                    var state = noise.Snapshot();
                    var request = new HostRequest(1, Guid.NewGuid(), stage, Noise: new("select", state.InstanceId, state.Revision, "mic"));
                    failure = (Task)typeof(StudyDevicePicker).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [request, false])!;
                }
                else failure = picker.RefreshAsync();
                PumpUntil(() => failure.IsCompleted, "device protocol failure did not settle"); failure.GetAwaiter().GetResult();
                Assert(!((Button)picker.FindName("SaveMicrophone")).IsEnabled && picker.Summary.Contains("待刷新"), "invalid device reply retains stale save permissions");
                Assert(((TextBlock)picker.FindName("DeviceMessage")).Text.Contains(stage == "study.devices.microphone" ? "未确认" : "不可用"), "device protocol failure has no retry feedback");
                Volatile.Write(ref broken, 0);
                read = picker.RefreshAsync(); PumpUntil(() => read.IsCompleted, "device retry did not settle"); read.GetAwaiter().GetResult();
                Assert(((Button)picker.FindName("SaveMicrophone")).IsEnabled, "valid retry cannot recover device controls");
                Assert(noise.Snapshot().SelectedDeviceId is null, "uncertain save was replayed");
                Checks.Add(stage + ": private device protocol error withdraws stale save controls, gives feedback, and recovers by read-only refresh without capture or save replay");
            }
            catch (Exception error) { errors.Add(new InvalidOperationException(stage + ": " + error.Message, error)); }
            finally
            {
                picker.CancelPending(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private static void RunStudyDeviceUnlockFeedbackChecks()
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "rejected", "succeeded", "protocol", "relocked", "refresh-failure" })
        {
            string pipe = "NPEduTools.Test.unlock-feedback." + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(_output, "unlock-feedback-" + scenario);
            var noise = new NoiseService(_ => throw new Exception("Capture forbidden"), () => [new("mic", "Synthetic")], directory: directory);
            var cameras = new StudyDeviceService(() => [], directory);
            int locked = 1, readsAfterUnlock = 0, unlocks = 0;
            using var stop = new CancellationTokenSource();
            var server = Task.Run(() => WorkspaceProxy(pipe, request =>
            {
                if (request.Capability == "noise.management.command")
                {
                    Interlocked.Increment(ref unlocks);
                    if (scenario is "succeeded" or "refresh-failure") Volatile.Write(ref locked, 0);
                    return Task.FromResult(scenario == "protocol" ? new HostResponse(2, request.RequestId, "Succeeded", null, "bad protocol") :
                        new HostResponse(1, request.RequestId, scenario is "succeeded" or "relocked" or "refresh-failure" ? "Succeeded" : "Rejected", "fixture", "学校管理口令不正确"));
                }
                if (Volatile.Read(ref unlocks) != 0) Interlocked.Increment(ref readsAfterUnlock);
                if (scenario == "refresh-failure" && Volatile.Read(ref unlocks) != 0)
                    return Task.FromResult(new HostResponse(2, request.RequestId, "Succeeded", null, "bad refresh protocol"));
                return Task.FromResult(request.Capability == "noise.devices" ? noise.Handle(request) :
                    cameras.Handle(request) with { StudyDevices = cameras.Snapshot() with { Locked = Volatile.Read(ref locked) != 0 } });
            }, stop.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe);
            var unhandled = new List<Exception>();
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler observer = (_, e) => { unhandled.Add(e.Exception); e.Handled = true; };
            Application.Current.Dispatcher.UnhandledException += observer;
            try
            {
                var initial = picker.RefreshAsync(); PumpUntil(() => initial.IsCompleted, "unlock fixture failed to load"); initial.GetAwaiter().GetResult();
                ((PasswordBox)picker.FindName("EquipmentSecret")).Password = "fixture-secret";
                typeof(StudyDevicePicker).GetMethod("UnlockClicked", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [picker, new RoutedEventArgs()]);
                PumpUntil(() => unhandled.Count > 0 || Volatile.Read(ref readsAfterUnlock) >= (scenario == "refresh-failure" ? 1 : 2) &&
                    !(bool)typeof(StudyDevicePicker).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!, "unlock refresh did not finish");
                Assert(unhandled.Count == 0, "unlock protocol exception escaped its UI handler");
                string message = ((TextBlock)picker.FindName("DeviceMessage")).Text;
                Assert(message.Contains(scenario == "rejected" ? "口令不正确" : scenario == "succeeded" ? "设备已解锁" : "未确认"), "read-only refresh erased the unlock outcome: " + scenario);
                if (scenario == "refresh-failure") Assert(message.Contains("不可用") && picker.Summary.Contains("待刷新"), "failed refresh claimed a current unlocked state");
                Assert(((PasswordBox)picker.FindName("EquipmentSecret")).Password.Length == 0 && unlocks == 1, "unlock kept credential or replayed the command");
                Assert(((StackPanel)picker.FindName("EquipmentLockPanel")).Visibility == (scenario == "succeeded" ? Visibility.Collapsed : Visibility.Visible), "unlock receipt replaced the fresh lock state");
                Checks.Add("Device unlock " + scenario + ": feedback survives read-only refresh, fresh lock state owns controls, password clears and command is sent once; private fixture only");
            }
            catch (Exception error) { failures.Add(new InvalidOperationException(scenario + ": " + error.Message, error)); }
            finally
            {
                Application.Current.Dispatcher.UnhandledException -= observer;
                picker.CancelPending(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private static void RunStudyDeviceSaveEvidenceChecks()
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "microphone-missing", "camera-missing", "microphone-mismatch", "camera-mismatch" })
        {
            bool camera = scenario.StartsWith("camera", StringComparison.Ordinal);
            string pipe = "NPEduTools.Test.device-save-evidence." + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(_output, scenario);
            var noise = new NoiseService(_ => throw new Exception("Capture forbidden"), () => [new("mic", "Synthetic")], directory: directory);
            var cameras = new StudyDeviceService(() => [new("cam", "Synthetic")], directory);
            int commands = 0, savedEvents = 0;
            using var stop = new CancellationTokenSource();
            var server = Task.Run(() => WorkspaceProxy(pipe, request =>
            {
                if (request.Capability is "study.devices.microphone" or "study.devices.select")
                {
                    Interlocked.Increment(ref commands);
                    return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "incomplete or superseded reply",
                        Noise: scenario == "microphone-mismatch" ? noise.Snapshot() with { SelectedDeviceId = "other" } : null,
                        StudyDevices: scenario == "camera-mismatch" ? cameras.Snapshot() with { CameraId = "other" } : null));
                }
                return Task.FromResult(request.Capability == "noise.devices" ? noise.Handle(request) : cameras.Handle(request));
            }, stop.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe);
            picker.SelectionSaved += (_, _) => savedEvents++;
            try
            {
                var initial = picker.RefreshAsync(); PumpUntil(() => initial.IsCompleted, "save evidence fixture failed to load"); initial.GetAwaiter().GetResult();
                ((ComboBox)picker.FindName(camera ? "StudyCamera" : "StudyMicrophone")).SelectedValue = camera ? "cam" : "mic";
                var microphoneState = noise.Snapshot(); var cameraState = cameras.Snapshot();
                var request = camera ? new HostRequest(1, Guid.NewGuid(), "study.devices.select", StudyCamera: new("select", cameraState.InstanceId, cameraState.Revision, "cam")) :
                    new HostRequest(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", microphoneState.InstanceId, microphoneState.Revision, "mic"));
                var save = (Task)typeof(StudyDevicePicker).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, [request, camera])!;
                PumpUntil(() => save.IsCompleted, "save evidence request did not settle"); save.GetAwaiter().GetResult();
                Assert(savedEvents == 0 && ((TextBlock)picker.FindName("DeviceMessage")).Text.Contains("未确认"), "a successful envelope falsely certified the selected device as saved: " + scenario);
                Assert(!((Button)picker.FindName("SaveMicrophone")).IsEnabled && !((Button)picker.FindName("SaveCamera")).IsEnabled, "uncertain save retained stale save permissions");
                var refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "save evidence reconciliation did not finish"); refresh.GetAwaiter().GetResult();
                Assert(commands == 1 && noise.Snapshot().SelectedDeviceId is null && cameras.Snapshot().CameraId is null, "reconciliation replayed a save or invented a persisted selection");
                Checks.Add(scenario + ": successful envelope without matching device snapshot stays unconfirmed, emits no saved event, and reconciles read-only without replay or sensors");
            }
            catch (Exception error) { failures.Add(new InvalidOperationException(scenario + ": " + error.Message, error)); }
            finally
            {
                picker.CancelPending(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
