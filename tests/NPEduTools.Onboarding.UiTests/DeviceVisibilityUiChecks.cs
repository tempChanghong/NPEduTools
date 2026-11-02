using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunDeviceVisibilityChecks()
    {
        var errors = new List<Exception>();
        foreach (string scenario in new[] { "hidden-lock", "hidden-unplug", "reopen-lock", "reopen-unplug", "reopen-healthy", "reopen-draft", "reopen-join" })
            try { DeviceVisibilityCheck(scenario); }
            catch (Exception error) { errors.Add(new InvalidOperationException(scenario + ": " + error.Message, error)); }
        try { DeviceVisibilityReadCancellationCheck(); }
        catch (Exception error) { errors.Add(new InvalidOperationException("cancelled refresh: " + error.Message, error)); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }
    private static void DeviceVisibilityCheck(string scenario)
    {
        string folder = Path.Combine(_output, "device-visibility." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string preference = Path.Combine(folder, "noise-microphone.json");
        File.WriteAllText(preference, JsonSerializer.Serialize("mic-a"));
        bool plugged = true; int captures = 0, microphoneReads = 0, saves = 0;
        var noise = new NoiseService(_ => { captures++; throw new InvalidOperationException("No sensors allowed"); },
            () => { Interlocked.Increment(ref microphoneReads); return plugged ? [new("mic-a", "Microphone A"), new("mic-b", "Microphone B")] : []; }, directory: folder);
        var cameras = new StudyDeviceService(() => [], folder);
        var equipment = new SchoolManagementService(noise, cameras, Path.Combine(folder, "school"));
        string pipe = "NPEduTools.Test.device-visibility." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, studyDevices: cameras).RunAsync(stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe); picker.SelectionSaved += (_, _) => saves++;
        var window = new Window { Content = picker, Width = 880, Height = 650, ShowActivated = false };
        try
        {
            Exercise(window, owner =>
            {
                var initial = picker.RefreshAsync(); PumpUntil(() => initial.IsCompleted, "initial inventory did not settle"); initial.GetAwaiter().GetResult();
                var microphone = (ComboBox)picker.FindName("StudyMicrophone");
                var save = (Button)picker.FindName("SaveMicrophone");
                Assert(save.IsEnabled && microphone.SelectedValue as string == "mic-a", "initial saved microphone was unavailable");
                if (scenario == "reopen-draft") microphone.SelectedValue = "mic-b";
                picker.Visibility = Visibility.Collapsed;
                if (scenario.EndsWith("lock")) equipment.BindSchool("{\"deviceId\":\"isolated-school-screen\"}");
                if (scenario.EndsWith("unplug")) plugged = false;
                if (scenario.StartsWith("hidden-"))
                {
                    Assert(!save.IsEnabled && !microphone.IsEnabled && picker.Summary.Contains("待刷新"), "hidden picker retained a stale editability claim");
                }
                else
                {
                    int priorReads = microphoneReads;
                    picker.Visibility = Visibility.Visible;
                    if (scenario == "reopen-join")
                    {
                        var first = picker.RefreshAsync(); var second = picker.RefreshAsync(); var ready = picker.CheckReadyAsync();
                        Assert(ReferenceEquals(first, second), "concurrent callers did not share the pending visibility refresh");
                        PumpUntil(() => first.IsCompleted && ready.IsCompleted, "joined refresh/readiness did not settle");
                        first.GetAwaiter().GetResult();
                        Assert(ready.GetAwaiter().GetResult() && microphoneReads == priorReads + 1, "readiness rejected an in-flight refresh or duplicated enumeration");
                    }
                    PumpUntil(() => microphoneReads > priorReads && ((Button)picker.FindName("DeviceRefresh")).IsEnabled, "reopened picker did not refresh the current Host state");
                    if (scenario == "reopen-lock")
                        Assert(!save.IsEnabled && !microphone.IsEnabled && ((FrameworkElement)picker.FindName("EquipmentLockPanel")).IsVisible, "school relock was not reflected on return");
                    else if (scenario == "reopen-unplug")
                        Assert(!save.IsEnabled && picker.Summary.Contains("不可用") && microphone.SelectedValue as string == "mic-a", "hotplug recovery switched devices or retained stale availability");
                    else
                        Assert(save.IsEnabled && microphone.SelectedValue as string == (scenario == "reopen-draft" ? "mic-b" : "mic-a"), "fresh read lost a real draft or failed to restore editable controls");
                }
                Assert(captures == 0 && saves == 0 && JsonSerializer.Deserialize<string>(File.ReadAllText(preference)) == "mic-a", "visibility changed saved preference or started capture");
                Checks.Add(scenario + ": actual shared picker/private Host withdraws hidden editability and refreshes on return; school relock/hotplug reflected, saved device and real draft preserved, no save/capture");
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
    private static void DeviceVisibilityReadCancellationCheck()
    {
        string pipe = "NPEduTools.Test.device-visible-read." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int phase = 0, cameraReads = 0;
        var noise = new NoiseState(Guid.NewGuid(), 1, "Idle", "synthetic input", null, null, null, null, "Waiting", null, [], SelectedDeviceId: "mic-a");
        var camera = new StudyDeviceState(Guid.NewGuid(), 1, null, null, null, []);
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            int at = Volatile.Read(ref phase);
            Assert(request.Capability is "noise.devices" or "study.devices.refresh", "visibility refresh emitted a mutation");
            if (request.Capability == "study.devices.refresh")
            {
                Interlocked.Increment(ref cameraReads);
                return new(1, request.RequestId, "Succeeded", null, "synthetic devices", StudyDevices: camera with { Revision = at == 2 ? 2 : 1, Locked = at == 2 });
            }
            var response = new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic microphones",
                Noise: at == 2 ? noise with { Revision = 2, SelectedDeviceId = "mic-b" } : noise,
                NoiseDevices: [new("mic-a", "A"), new("mic-b", "B")]);
            if (at == 1) { received.TrySetResult(); await release.Task.WaitAsync(stop.Token); replied.TrySetResult(); }
            return response;
        }, stop.Token));
        var picker = new StudyDevicePicker(); picker.Bind(pipe);
        var window = new Window { Content = picker, Width = 880, Height = 650, ShowActivated = false };
        try
        {
            Exercise(window, owner =>
            {
                var initial = picker.RefreshAsync(); PumpUntil(() => initial.IsCompleted, "initial read did not settle"); initial.GetAwaiter().GetResult();
                Volatile.Write(ref phase, 1); var old = picker.RefreshAsync();
                PumpUntil(() => received.Task.IsCompleted, "old read was not held by the fixture");
                picker.Visibility = Visibility.Collapsed;
                PumpUntil(() => old.IsCompleted, "hidden pending inventory did not cancel"); old.GetAwaiter().GetResult();
                Volatile.Write(ref phase, 2); picker.Visibility = Visibility.Visible;
                var current = picker.RefreshAsync();
                Assert(!ReferenceEquals(old, current), "return joined a cancelled read from the hidden view");
                PumpUntil(() => current.IsCompleted, "fresh return read did not settle"); current.GetAwaiter().GetResult();
                release.TrySetResult(); PumpUntil(() => replied.Task.IsCompleted, "old server reply did not settle");
                Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert(((ComboBox)picker.FindName("StudyMicrophone")).SelectedValue as string == "mic-b" &&
                    !((Button)picker.FindName("SaveMicrophone")).IsEnabled && ((FrameworkElement)picker.FindName("EquipmentLockPanel")).IsVisible,
                    "cancelled/late inventory replaced fresh locked device state");
                Assert(cameraReads == 2, "abandoned microphone read continued enumerating the old camera scope");
                Checks.Add("Pending inventory visibility: hide cancels the old read, immediate return joins only a new read, late response cannot restore old device/lock state, no write or sensor access");
                owner.Close();
            });
        }
        finally
        {
            release.TrySetResult(); picker.CancelPending(); window.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
