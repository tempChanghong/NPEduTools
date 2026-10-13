using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunDeviceRefreshSnapshotChecks()
    {
        var failures = new List<Exception>();
        foreach (string scenario in new[] { "missing-camera", "rejected-camera", "null-camera-list", "null-camera-entry", "null-microphone-entry" })
            try { DeviceRefreshSnapshotCheck(scenario); } catch (Exception e) { failures.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
        if (failures.Count > 0) throw new AggregateException(failures);
    }
    private static void DeviceRefreshSnapshotCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.device-refresh-snapshot." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); int phase = 0;
        var noise = new NoiseState(Guid.NewGuid(), 1, "Idle", "saved synthetic microphone", "mic", "synthetic", null, null, "NoData", null, [], SelectedDeviceId: "mic");
        var cameras = new StudyDeviceState(Guid.NewGuid(), 1, null, null, null, [new("cam", "synthetic camera")]);
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            Assert(request.Capability is "noise.devices" or "study.devices.refresh", "device recovery submitted a mutation");
            bool broken = Volatile.Read(ref phase) == 1;
            if (request.Capability == "noise.devices") return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic microphone list",
                Noise: noise, NoiseDevices: broken && scenario == "null-microphone-entry" ? [null!] : [new("mic", "synthetic")]));
            return Task.FromResult(new HostResponse(1, request.RequestId, broken && scenario == "rejected-camera" ? "Rejected" : "Succeeded", null, "synthetic camera list",
                StudyDevices: broken ? scenario switch
                {
                    "missing-camera" => null,
                    "null-camera-list" => cameras with { Cameras = null! },
                    "null-camera-entry" => cameras with { Cameras = [null!] },
                    _ => cameras
                } : cameras));
        }, stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe);
        var owner = new Window { Content = picker, Width = 700, Height = 550 };
        try
        {
            Exercise(owner, _ =>
            {
                var refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "initial device refresh stalled"); refresh.GetAwaiter().GetResult();
                Assert(((Button)picker.FindName("SaveMicrophone")).IsEnabled, "healthy optional-camera state is unusable");
                Volatile.Write(ref phase, 1); refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "broken device refresh stalled");
                Assert(!refresh.IsFaulted, "semantic device error escaped refresh: " + refresh.Exception?.GetBaseException().GetType().Name);
                refresh.GetAwaiter().GetResult();
                Assert(!((Button)picker.FindName("SaveMicrophone")).IsEnabled && !((Button)picker.FindName("SaveCamera")).IsEnabled && picker.Summary.Contains("待刷新"),
                    "partial/rejected device snapshot leaves a fresh claim or enabled save controls");
                var ready = picker.CheckReadyAsync(); PumpUntil(() => ready.IsCompleted, "OOBE partial-state check stalled");
                Assert(!ready.IsFaulted && !ready.GetAwaiter().GetResult(), "unconfirmed device state advanced OOBE");
                Volatile.Write(ref phase, 2); refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "device recovery stalled"); refresh.GetAwaiter().GetResult();
                Assert(((Button)picker.FindName("SaveMicrophone")).IsEnabled && ((ComboBox)picker.FindName("StudyMicrophone")).SelectedValue as string == "mic",
                    "read-only device recovery lost selection or kept controls unavailable");
                Checks.Add(scenario + ": actual picker/private pipe withdraws unconfirmed snapshot, blocks OOBE/save, preserves selection and recovers read-only; no sensors");
                owner.Close();
            });
        }
        finally { picker.CancelPending(); owner.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
