using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunDeviceSelectionRefreshUiChecks()
    {
        var errors = new List<Exception>();
        foreach (string scenario in new[] { "untouched", "microphone-draft", "camera-draft", "both-drafts", "clear-camera-draft", "remote-camera-clear" })
            try { DeviceSelectionRefreshCheck(scenario); }
            catch (Exception error) { errors.Add(new InvalidOperationException(scenario + ": " + error.Message, error)); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private static void DeviceSelectionRefreshCheck(string scenario)
    {
        string folder = Path.Combine(_output, "selection-refresh." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string micPath = Path.Combine(folder, "noise-microphone.json"), cameraPath = Path.Combine(folder, "study-camera.json");
        File.WriteAllText(micPath, JsonSerializer.Serialize("old")); File.WriteAllText(cameraPath, JsonSerializer.Serialize("old"));
        string[] ids = ["old", "remote", "draft"];
        int captures = 0, saves = 0;
        var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No hardware allowed"); },
            () => ids.Select(id => new NoiseDevice(id, "Microphone " + id)).ToArray(), directory: folder);
        var cameras = new StudyDeviceService(() => ids.Select(id => new StudyCameraDevice(id, "Camera " + id)).ToArray(), folder);
        string pipe = "NPEduTools.Test.selection-refresh." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe); picker.SelectionSaved += (_, _) => saves++;
        var window = new Window { Content = picker, Width = 880, Height = 650, ShowActivated = false };
        try
        {
            Exercise(window, owner =>
            {
                void Refresh()
                {
                    var task = picker.RefreshAsync(); PumpUntil(() => task.IsCompleted, "device refresh stalled"); task.GetAwaiter().GetResult();
                }
                Refresh();
                var microphone = (ComboBox)picker.FindName("StudyMicrophone"); var camera = (ComboBox)picker.FindName("StudyCamera");
                Assert(microphone.SelectedValue as string == "old" && camera.SelectedValue as string == "old", "saved selections not loaded");
                bool micDraft = scenario is "microphone-draft" or "both-drafts", camDraft = scenario is "camera-draft" or "both-drafts" or "clear-camera-draft";
                if (micDraft) microphone.SelectedValue = "draft";
                if (camDraft) camera.SelectedValue = scenario == "clear-camera-draft" ? "" : "draft";
                // A second settings client changes the actual Host preference while this picker stays open.
                var mic = noise.Snapshot(); var cam = cameras.Snapshot();
                Assert(noise.Handle(new(1, Guid.NewGuid(), "noise.command", Noise: new("select", mic.InstanceId, mic.Revision, "remote"))).Outcome == "Succeeded", "remote microphone fixture save failed");
                string? remoteCamera = scenario == "remote-camera-clear" ? null : "remote";
                Assert(cameras.Handle(new(1, Guid.NewGuid(), "study.devices.select", StudyCamera: new(remoteCamera is null ? "clear" : "select", cam.InstanceId, cam.Revision, remoteCamera))).Outcome == "Succeeded", "remote camera fixture save failed");
                Refresh();
                string expectedCamera = camDraft ? scenario == "clear-camera-draft" ? "" : "draft" : remoteCamera ?? "";
                Assert(microphone.SelectedValue as string == (micDraft ? "draft" : "remote") && camera.SelectedValue as string == expectedCamera,
                    "refresh confused a previously saved selection with a genuine unsaved draft");
                Assert(((TextBlock)picker.FindName("MicrophoneStatus")).Text.Contains("remote") && ((TextBlock)picker.FindName("CameraStatus")).Text.Contains(remoteCamera ?? "未选择"), "saved status did not follow Host preference");
                var ready = picker.CheckReadyAsync(); PumpUntil(() => ready.IsCompleted, "readiness refresh stalled");
                Assert(ready.GetAwaiter().GetResult() == !(micDraft || camDraft), "OOBE readiness claimed phantom edits or discarded a real draft");
                Assert(JsonSerializer.Deserialize<string>(File.ReadAllText(micPath)) == "remote" && JsonSerializer.Deserialize<string>(File.ReadAllText(cameraPath)) == remoteCamera && captures == 0 && saves == 0,
                    "read-only refresh changed saved preferences or started capture");
                Checks.Add(scenario + ": actual picker/private Host pipe follows externally saved microphone/camera when untouched, preserves only real drafts independently, OOBE readiness remains consistent; refresh never saves or captures");
                owner.Close();
            });
        }
        finally
        {
            picker.CancelPending(); window.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
