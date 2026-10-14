using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSelectedInventoryUiChecks()
    {
        foreach (bool countLimit in new[] { false, true })
        {
            int count = countLimit ? 129 : 128, captures = 0, saved = 0;
            string name = countLimit ? "Synthetic endpoint" : new string('设', 256);
            string folder = Path.Combine(_output, "selected-catalog." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string micId = "mic-" + (count - 1), camId = "cam-" + (count - 1);
            string micFile = Path.Combine(folder, "noise-microphone.json"), camFile = Path.Combine(folder, "study-camera.json");
            string micSaved = JsonSerializer.Serialize(micId), camSaved = JsonSerializer.Serialize(camId);
            File.WriteAllText(micFile, micSaved); File.WriteAllText(camFile, camSaved);
            NoiseDevice[] microphones = Enumerable.Range(0, count).Select(i => new NoiseDevice("mic-" + i, name)).ToArray();
            StudyCameraDevice[] catalog = Enumerable.Range(0, count).Select(i => new StudyCameraDevice("cam-" + i, name)).ToArray();
            var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); }, () => microphones, directory: folder);
            var cameras = new StudyDeviceService(() => catalog, folder);
            string pipe = "NPEduTools.Test.selected-inventory." + Guid.NewGuid().ToString("N");
            using var lifetime = new CancellationTokenSource();
            var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(lifetime.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe); picker.SelectionSaved += (_, _) => saved++;
            var window = new Window { Width = 880, Height = 650, Content = picker, ShowActivated = false };
            try
            {
                Exercise(window, owner =>
                {
                    var refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "selected inventory refresh did not settle");
                    refresh.GetAwaiter().GetResult();
                    Assert(picker.Summary.Contains("麦克风已配置") && picker.Summary.Contains("摄像头已配置"), "bounded catalog falsely marked present selected equipment unavailable");
                    Assert(((ComboBox)picker.FindName("StudyMicrophone")).SelectedValue as string == micId &&
                        ((ComboBox)picker.FindName("StudyCamera")).SelectedValue as string == camId, "refresh replaced the saved choices");
                    Assert(cameras.Snapshot().CameraAvailable == true, "omitted camera would block photo eligibility");
                    Assert(captures == 0 && saved == 0 && noise.Snapshot().State == "Idle", "inventory refresh started capture or reported a save");
                    Assert(File.ReadAllText(micFile) == micSaved && File.ReadAllText(camFile) == camSaved, "inventory refresh rewrote preferences");
                    Checks.Add((countLimit ? "Count" : "Byte") + " budget: real WPF/private Pipe retains present saved microphone/camera, truthful availability, no sensor start or preference write");
                    owner.Close();
                });
            }
            finally
            {
                window.Close(); picker.CancelPending(); lifetime.Cancel();
                try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult(); Directory.Delete(folder, true);
            }
        }
    }
}
