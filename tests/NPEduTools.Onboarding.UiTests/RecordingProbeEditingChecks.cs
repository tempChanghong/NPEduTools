using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingProbeEditingChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string scenario in new[] { "success", "failure", "active", "unavailable" })
                try { RecordingProbeEditingCheck(scenario); }
                catch (Exception error) { failures.Add(new InvalidOperationException(scenario + ": " + error.Message, error)); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RecordingProbeEditingCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.recording-probe-edit." + Guid.NewGuid().ToString("N");
        string path = StartupPreferencesStore.PathFor(pipe).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        var saved = new RecordingOptions("display-a", Path.GetTempPath(), SpeakerId: "speaker-a", MicrophoneId: "mic-a");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Options = saved }));
        byte[] before = File.ReadAllBytes(path);
        var clientType = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var poll = (Task)clientType.GetField("_poll", WorkspaceFlags)!.GetValue(client)!;
        PumpUntil(() => poll.IsCompleted, "isolated recording probe poll did not stop");
        void Apply(RecordingState state) => clientType.GetMethod("Apply", WorkspaceFlags)!.Invoke(client,
            [new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "synthetic snapshot", Recording: state)]);
        Apply(new("Idle", "synthetic idle"));
        var inventory = new RecordingEnvironment(true, null,
            [new("display-a", "A", 0, 0, 1920, 1080, true), new("display-b", "B", 1920, 0, 1280, 720, false)],
            [new("mic-a", "A"), new("mic-b", "B")], [new("speaker-a", "A"), new("speaker-b", "B")]);
        var held = new TaskCompletionSource<RecordingEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        Func<Task<RecordingEnvironment>> probe = () => ++probes == 2 ? held.Task : Task.FromResult(inventory);
        var constructor = typeof(RecordingWindow).GetConstructors(WorkspaceFlags).Single(c => c.GetParameters().Length == 3);
        var window = (RecordingWindow)constructor.Invoke([client, pipe, probe]);
        Task Refresh() => (Task)typeof(RecordingWindow).GetMethod("ProbeAsync", WorkspaceFlags)!.Invoke(window, null)!;
        try
        {
            Exercise(window, owner =>
            {
                ComboBox Choice(string name) => (ComboBox)owner.FindName(name);
                var settings = (FrameworkElement)owner.FindName("RecordingSettings");
                Click(owner, "EditSettings");
                Assert(settings.IsEnabled && probes == 1, "initial recording settings did not load");
                Choice("DisplayChoice").SelectedValue = "display-b";
                Choice("SpeakerChoice").SelectedValue = "speaker-b";
                Choice("MicrophoneChoice").SelectedValue = "mic-b";
                Choice("FpsChoice").SelectedIndex = Choice("QualityChoice").SelectedIndex = 1;
                string directory = Path.Combine(_output, "draft-recordings");
                ((TextBox)owner.FindName("OutputDirectory")).Text = directory;
                var refresh = Refresh();
                Assert(probes == 2 && !refresh.IsCompleted, "probe did not hold the inventory response");
                bool formLocked = !settings.IsEnabled && !Button(owner, "RecordStart").IsEnabled && !Button(owner, "SaveSettings").IsEnabled;
                // The old page accepts this choice, then silently restores mic-b when the response arrives.
                if (Choice("MicrophoneChoice").IsEnabled) Choice("MicrophoneChoice").SelectedValue = "mic-a";
                Click(owner, "SaveSettings");
                Assert(File.ReadAllBytes(path).SequenceEqual(before), "pending detection wrote preferences");
                if (scenario == "active") Apply(new("Recording", "synthetic active", Control: new("Automatic", Guid.NewGuid(), "fixture", 1, 1)));
                if (scenario == "unavailable") clientType.GetMethod("MarkUnavailable", WorkspaceFlags)!.Invoke(client, [null, "synthetic disconnect"]);
                if (scenario == "failure") held.SetException(new IOException("synthetic probe failure"));
                else held.SetResult(inventory);
                PumpUntil(() => refresh.IsCompleted, "held probe did not settle"); refresh.GetAwaiter().GetResult();
                Assert(formLocked, "pending probe left edits/save enabled; returned microphone selection: " + Choice("MicrophoneChoice").SelectedValue);
                Assert(File.ReadAllBytes(path).SequenceEqual(before), "probe automatically saved draft settings");
                if (scenario is "active" or "unavailable")
                {
                    Assert(!settings.IsEnabled && !Button(owner, "RecordStart").IsEnabled && !Button(owner, "SaveSettings").IsEnabled,
                        "late probe completion overrode the latest recording/connection state");
                    if (scenario == "active") Assert(Button(owner, "RecordStop").IsEnabled, "device probe disabled active recording controls");
                }
                else
                {
                    Assert(settings.IsEnabled && Button(owner, "RecordStart").IsEnabled == (scenario == "success"),
                        "probe completion did not restore the appropriate settings/start controls");
                    if (scenario == "failure")
                    {
                        var retry = Refresh(); PumpUntil(() => retry.IsCompleted, "explicit probe retry stalled"); retry.GetAwaiter().GetResult();
                        Assert(Button(owner, "RecordStart").IsEnabled, "explicit inventory retry did not recover");
                    }
                    Assert(Choice("DisplayChoice").SelectedValue as string == "display-b" &&
                        Choice("SpeakerChoice").SelectedValue as string == "speaker-b" &&
                        Choice("MicrophoneChoice").SelectedValue as string == "mic-b" &&
                        ((TextBox)owner.FindName("OutputDirectory")).Text == directory,
                        "device detection discarded the preexisting unsaved draft");
                    Click(owner, "SaveSettings");
                    var expected = saved with { Display = "display-b", SpeakerId = "speaker-b", MicrophoneId = "mic-b",
                        FramesPerSecond = 15, MaximumHeight = 720, OutputDirectory = directory };
                    var actual = (RecordingOptions?)typeof(RecordingWindow).GetMethod("ReadSavedOptions", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [pipe]);
                    Assert(actual == expected, "explicit save did not persist the preserved draft");
                }
                Checks.Add("Recording probe/" + scenario + ": pending enumeration locks edits/save immediately; latest state governs completion, drafts survive, only explicit save persists; synthetic inventory and detached client, no capture");
                window.Shutdown();
            });
        }
        finally
        {
            held.TrySetResult(inventory); window.Shutdown();
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
