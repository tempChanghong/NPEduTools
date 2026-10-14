using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunSelectionCatalogUiChecks()
    {
        foreach (bool microphone in new[] { true, false })
        {
            string folder = Path.Combine(_output, "selection-save." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, microphone ? "noise-microphone.json" : "study-camera.json");
            File.WriteAllText(file, JsonSerializer.Serialize("old"));
            NoiseDevice[] micCatalog = [new("old", "Old microphone"), new("target", "Target microphone")];
            StudyCameraDevice[] camCatalog = [new("old", "Old camera"), new("target", "Target camera")];
            int captures = 0, saved = 0;
            var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No real capture"); }, () => micCatalog, directory: folder);
            var cameras = new StudyDeviceService(() => camCatalog, folder);
            string pipe = "NPEduTools.Test.selection-save-ui." + Guid.NewGuid().ToString("N");
            using var lifetime = new CancellationTokenSource();
            var serving = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(lifetime.Token));
            var picker = new StudyDevicePicker(); picker.Bind(pipe); picker.SelectionSaved += (_, _) => saved++;
            var window = new Window { Width = 880, Height = 650, Content = picker, ShowActivated = false };
            try
            {
                Exercise(window, owner =>
                {
                    var refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "initial device selection did not settle");
                    refresh.GetAwaiter().GetResult();
                    var choice = (ComboBox)picker.FindName(microphone ? "StudyMicrophone" : "StudyCamera");
                    choice.SelectedValue = "target";
                    var save = (Button)picker.FindName(microphone ? "SaveMicrophone" : "SaveCamera");
                    Assert(save.IsEnabled, "known target could not be selected");
                    // Grow/reorder only the save-boundary enumeration, after the
                    // real WPF picker has shown the user both current devices.
                    var ids = new[] { "old" }.Concat(Enumerable.Range(0, 126).Select(i => "filler-" + i)).Append("target").ToArray();
                    micCatalog = ids.Select(id => new NoiseDevice(id, new string('设', 256))).ToArray();
                    camCatalog = ids.Select(id => new StudyCameraDevice(id, new string('设', 256))).ToArray();
                    save.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    PumpUntil(() => saved == 1, "present target was rejected by truncated save inventory");
                    Assert(((TextBlock)picker.FindName("DeviceMessage")).Text.Contains("已保存"), "save success lacked feedback");
                    Assert(JsonSerializer.Deserialize<string>(File.ReadAllText(file)) == "target", "success did not persist the chosen endpoint");
                    if (!microphone) Assert(cameras.Snapshot().CameraAvailable == true, "first camera save snapshot falsely reported absence");
                    refresh = picker.RefreshAsync(); PumpUntil(() => refresh.IsCompleted, "new saved choice could not refresh");
                    refresh.GetAwaiter().GetResult();
                    Assert(choice.SelectedValue as string == "target", "refresh replaced the newly chosen endpoint");
                    Assert(picker.Summary.Contains(microphone ? "麦克风已配置" : "摄像头已配置"), "saved target shown unavailable");
                    Assert(captures == 0 && noise.Snapshot().State == "Idle" && saved == 1, "selection started capture or generated duplicate save events");
                    Checks.Add((microphone ? "Microphone" : "Camera") + " selection: real WPF/private Pipe saves a present endpoint beyond changed display budget, keeps choice on refresh, no capture");
                    owner.Close();
                });
            }
            finally
            {
                window.Close(); picker.CancelPending(); lifetime.Cancel();
                try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                noise.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Assert(Path.GetDirectoryName(Path.GetFullPath(folder)) == Path.GetFullPath(_output).TrimEnd(Path.DirectorySeparatorChar), "fixture cleanup escaped output root");
                Assert(Path.GetFileName(folder).StartsWith("selection-save."), "unexpected cleanup folder");
                Directory.Delete(folder, true);
            }
        }
    }
}
