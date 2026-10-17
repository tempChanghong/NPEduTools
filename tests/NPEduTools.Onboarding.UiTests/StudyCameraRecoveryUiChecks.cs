using System.IO;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunStudyCameraConfigurationRemovalChecks()
    {
        foreach (bool locked in new[] { false, true })
        {
            string pipe = "NPEduTools.Test.camera-recovery." + Guid.NewGuid().ToString("N");
            string folder = Path.Combine(_output, "camera-recovery-" + locked + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "study-camera.json");
            const string original = "{isolated broken camera preference";
            File.WriteAllText(path, original);
            var noise = new NoiseService(_ => throw new InvalidOperationException("No capture allowed"),
                () => [new("mic", "Synthetic microphone")], directory: folder);
            var camera = new StudyDeviceService(() => [new("camera", "Synthetic camera")], folder);
            using var stop = new CancellationTokenSource();
            var server = Task.Run(() => WorkspaceProxy(pipe, request => Task.FromResult(request.Capability == "noise.devices"
                ? noise.Handle(request) : camera.Handle(request) with { StudyDevices = camera.Snapshot() with { Locked = locked } }), stop.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe);
            try
            {
                var refresh = picker.RefreshAsync();
                PumpUntil(() => refresh.IsCompleted, "broken camera preference did not settle"); refresh.GetAwaiter().GetResult();
                var choice = (ComboBox)picker.FindName("StudyCamera");
                var save = (Button)picker.FindName("SaveCamera");
                Assert(picker.Summary.Contains("待刷新") && !save.IsEnabled, "broken camera preference left editable stale controls");
                File.Move(path, path + ".broken");
                refresh = picker.RefreshAsync();
                PumpUntil(() => refresh.IsCompleted, "camera recovery refresh did not settle"); refresh.GetAwaiter().GetResult();
                Assert(picker.Summary.Contains("未使用摄像头") && choice.SelectedValue as string == "" && camera.Snapshot().CameraId is null,
                    "read-only recovery did not restore an unselected camera state");
                Assert(!File.Exists(path) && File.ReadAllText(path + ".broken") == original, "refresh rewrote the removed preference or its backup");
                Assert(choice.IsEnabled == !locked && save.IsEnabled == !locked, "recovery failed to restore controls or bypassed the school device lock");
                if (!locked)
                {
                    choice.SelectedValue = "camera";
                    save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    PumpUntil(() => camera.Snapshot().CameraId == "camera" && save.IsEnabled, "explicit camera selection did not save after recovery");
                }
                Assert(noise.Snapshot().State == "Idle", "camera preference recovery started noise sampling");
                Checks.Add($"Camera configuration removal, school locked={locked}: actual Host service/private pipe and shared picker recover by refresh; no automatic selection/file rewrite; explicit save and read-only lock remain distinct; no capture");
            }
            finally
            {
                picker.CancelPending(); stop.Cancel();
                try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }
}
