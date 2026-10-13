using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunDeviceRejectionStateChecks()
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "microphone-locked", "camera-locked", "microphone-changed", "camera-changed", "microphone-busy", "camera-busy", "microphone-cleanup", "microphone-stopping" })
            try { DeviceRejectionStateCheck(scenario); }
            catch (Exception e) { failures.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private static void DeviceRejectionStateCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.device-rejection-state." + Guid.NewGuid().ToString("N");
        bool camera = scenario.StartsWith("camera-", StringComparison.Ordinal);
        string code = scenario == "microphone-cleanup" ? "NoiseCleanupRequired" : scenario == "microphone-stopping" ? "NoiseStopping" :
            scenario.EndsWith("locked", StringComparison.Ordinal) ? "EQUIPMENT_LOCKED" :
            scenario.EndsWith("changed", StringComparison.Ordinal) ? camera ? "CameraStateChanged" : "NoiseStateChanged" :
            camera ? "StudyDevicesBusy" : "NoiseBusy";
        using var stop = new CancellationTokenSource();
        int phase = 0, mutations = 0;
        var initialNoise = new NoiseState(Guid.NewGuid(), 1, "Idle", "synthetic preferences", "mic-one", "one", null, null,
            "NoData", null, [], SelectedDeviceId: "mic-one");
        var initialCamera = new StudyDeviceState(Guid.NewGuid(), 1, "cam-one", "one", true, [new("cam-one", "one"), new("cam-two", "two")]);
        // Calibration sessions deliberately have an Idle public noise snapshot. A busy
        // receipt cannot certify editability from that raw-monitoring state alone.
        var currentNoise = scenario == "microphone-changed" ? initialNoise with { InstanceId = Guid.NewGuid(), Revision = 2 } :
            initialNoise with { State = scenario == "microphone-cleanup" ? "Faulted" : scenario == "microphone-stopping" ? "Stopped" : "Idle" };
        var recoveredNoise = scenario is "microphone-cleanup" or "microphone-stopping"
            ? initialNoise with { InstanceId = Guid.NewGuid(), Revision = 2 } : currentNoise;
        var currentCamera = initialCamera with { InstanceId = scenario == "camera-changed" ? Guid.NewGuid() : initialCamera.InstanceId,
            Revision = 2, Locked = code == "EQUIPMENT_LOCKED" };
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            int currentPhase = Volatile.Read(ref phase);
            if (request.Capability is "study.devices.microphone" or "study.devices.select")
            {
                Interlocked.Increment(ref mutations);
                return Task.FromResult(new HostResponse(1, request.RequestId, "Rejected", code, "synthetic rejection",
                    Noise: camera ? null : currentNoise, StudyDevices: camera ? currentCamera : null));
            }
            Assert(request.Capability is "noise.devices" or "study.devices.refresh", "recovery emitted a non-read request");
            return Task.FromResult(request.Capability == "noise.devices"
                ? new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic microphone list",
                    Noise: currentPhase == 3 ? recoveredNoise with { SelectedDeviceId = "unplugged" } : currentPhase == 2 ? recoveredNoise : initialNoise,
                    NoiseDevices: [new("mic-one", "one"), new("mic-two", "two")])
                : new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic camera list",
                    StudyDevices: currentPhase >= 2 ? currentCamera : initialCamera));
        }, stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe);
        var owner = new Window { Content = picker, Width = 700, Height = 550 };
        try
        {
            Exercise(owner, _ =>
            {
                var read = picker.RefreshAsync(); PumpUntil(() => read.IsCompleted, "initial device read stalled"); read.GetAwaiter().GetResult();
                var mic = (ComboBox)picker.FindName("StudyMicrophone"); var cam = (ComboBox)picker.FindName("StudyCamera");
                var saveMic = (Button)picker.FindName("SaveMicrophone"); var saveCam = (Button)picker.FindName("SaveCamera");
                mic.SelectedValue = "mic-two"; cam.SelectedValue = "cam-two";
                Assert(saveMic.IsEnabled && saveCam.IsEnabled, "healthy fixture did not allow draft choices");
                Volatile.Write(ref phase, 1);
                HostRequest request = camera
                    ? new(1, Guid.NewGuid(), "study.devices.select", StudyCamera: new("select", initialCamera.InstanceId, initialCamera.Revision, "cam-two"))
                    : new(1, Guid.NewGuid(), "study.devices.microphone", Noise: new("select", initialNoise.InstanceId, initialNoise.Revision, "mic-two"));
                var save = (Task)typeof(StudyDevicePicker).GetMethod("SaveAsync", WorkspaceFlags)!.Invoke(picker, [request, camera])!;
                PumpUntil(() => save.IsCompleted, "rejected device save stalled"); save.GetAwaiter().GetResult();
                Assert(!saveMic.IsEnabled && !saveCam.IsEnabled && !mic.IsEnabled && !cam.IsEnabled && picker.Summary.Contains("待刷新"),
                    "partial rejection left an actionable or fresh combined device state");
                Assert(((TextBlock)picker.FindName("MicrophoneStatus")).Text.Contains("待重新读取") &&
                    ((TextBlock)picker.FindName("CameraStatus")).Text.Contains("待重新读取"), "rejected state retained current saved-device claims");
                string feedback = ((TextBlock)picker.FindName("DeviceMessage")).Text;
                Assert(feedback.Contains(code == "EQUIPMENT_LOCKED" ? "学校" : code.EndsWith("Changed", StringComparison.Ordinal) ? "变化" :
                    code == "NoiseCleanupRequired" ? "重启" : code == "NoiseStopping" ? "退出" : "采集") && feedback.Contains("刷新"),
                    "rejected save did not explain the recovery path: " + feedback);
                Assert(mic.SelectedValue as string == "mic-two" && cam.SelectedValue as string == "cam-two", "rejection erased the user's draft choices");
                Volatile.Write(ref phase, 2);
                read = picker.RefreshAsync(); PumpUntil(() => read.IsCompleted, "device reconciliation stalled"); read.GetAwaiter().GetResult();
                Assert(mutations == 1, "read-only reconciliation replayed a rejected mutation");
                Assert(mic.SelectedValue as string == "mic-two" && cam.SelectedValue as string == "cam-two", "refresh erased the draft after rejection");
                Assert(saveMic.IsEnabled == (code != "EQUIPMENT_LOCKED") && saveCam.IsEnabled == (code != "EQUIPMENT_LOCKED"),
                    "recovery did not use the current school lock");
                Assert(((StackPanel)picker.FindName("EquipmentLockPanel")).Visibility == (code == "EQUIPMENT_LOCKED" ? Visibility.Visible : Visibility.Collapsed),
                    "current lock verification panel was not restored by read-only refresh");
                Assert(!picker.Summary.Contains("待刷新"), "fresh combined snapshot stayed unknown after reconciliation");
                var ready = picker.CheckReadyAsync(); PumpUntil(() => ready.IsCompleted, "OOBE readiness reconciliation stalled");
                Assert(ready.GetAwaiter().GetResult() == (code == "EQUIPMENT_LOCKED"),
                    "school-locked valid preferences cannot complete OOBE, or an editable unsaved draft was falsely confirmed");
                Assert(mutations == 1 && mic.SelectedValue as string == "mic-two" && cam.SelectedValue as string == "cam-two",
                    "OOBE reconciliation replayed save or erased the read-only draft");
                if (code == "EQUIPMENT_LOCKED")
                {
                    Volatile.Write(ref phase, 3);
                    ready = picker.CheckReadyAsync(); PumpUntil(() => ready.IsCompleted, "locked unplugged microphone read stalled");
                    Assert(!ready.GetAwaiter().GetResult() && mutations == 1, "school lock let an unavailable saved microphone complete OOBE");
                }
                Checks.Add(scenario + ": actual picker/private pipe rejects stale editability, preserves draft, reconciles current lock read-only and does not replay save or start sensors");
                owner.Close();
            });
        }
        finally { picker.CancelPending(); owner.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
