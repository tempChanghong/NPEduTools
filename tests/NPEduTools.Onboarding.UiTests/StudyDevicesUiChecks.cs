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
                typeof(StudyMonitoringWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(manage, [null]);
                Assert(!((TextBlock)manage.FindName("StudyCapture")).Text.Contains("正在采集"), "unknown Host claimed live capture");
                Snapshot(owner, "study-monitoring-devices.png"); owner.Close();
                Checks.Add("Study management actual WPF/private Host pipe: shared selection, unplugged camera retained without fallback, stale state no active claim");
            });
            var main = (MainWindow)typeof(MainWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([pipe, null, false]);
            Exercise(main, owner =>
            {
                Assert(Find(owner, "HomeOpenNoise") is Button && Find(owner, "HomeOpenStudyMonitoring") is Button, "home did not separate manual and study monitoring");
                var studyNavigation = (Button)Find(owner, "OpenStudyMonitoring");
                var sidebarPoint = studyNavigation.TranslatePoint(new Point(), owner);
                Assert(sidebarPoint.Y >= 0 && sidebarPoint.Y + studyNavigation.ActualHeight <= owner.ActualHeight, "study navigation is clipped by the fixed sidebar footer");
                Assert(Find(owner, "OpenRecordingPlan") is Button && Find(owner, "OpenClassroomMode") is Button, "existing classroom navigation was lost");
                Snapshot(owner, "home-monitoring-layout.png"); owner.Close();
                Checks.Add("Main window actual WPF: separate manual noise/self-study entries; existing home tools retained; no production App startup");
            });
        }
        finally
        {
            window.Shutdown(); lifetime.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            noise.DisposeAsync().AsTask().GetAwaiter().GetResult(); SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
