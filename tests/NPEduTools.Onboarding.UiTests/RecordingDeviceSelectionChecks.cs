using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingDeviceSelectionChecks()
    {
        var errors = new List<Exception>();
        foreach (string scenario in new[] { "missing-display", "missing-speaker", "missing-microphone", "disabled-speaker", "disabled-microphone", "default", "new" })
            try { RecordingDeviceSelectionCheck(scenario); }
            catch (Exception error) { errors.Add(new InvalidOperationException(scenario + ": " + error.GetBaseException().Message, error)); }
        if (errors.Count > 0) throw new AggregateException(errors);
    }

    private static void RecordingDeviceSelectionCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-selection." + Guid.NewGuid().ToString("N");
        string path = StartupPreferencesStore.PathFor(pipe).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        var saved = new RecordingOptions("saved-display", Path.GetTempPath(), SystemAudio: scenario != "disabled-speaker",
            Microphone: scenario != "disabled-microphone", SpeakerId: scenario == "default" ? "default" : "saved-speaker",
            MicrophoneId: scenario == "default" ? "default" : "saved-microphone");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (scenario != "new") File.WriteAllText(path, JsonSerializer.Serialize(new { Version = 1, Options = saved }));
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        // Stop polling/commands before injecting an idle snapshot. No capture process can start.
        type.GetMethod("Detach")!.Invoke(client, null);
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        PumpUntil(() => poll.IsCompleted, "selection fixture poll did not stop");
        type.GetMethod("Apply", flags)!.Invoke(client, [new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "isolated idle", Recording: new("Idle", "fixture"))]);
        var environment = new RecordingEnvironment(true, null,
            scenario == "missing-display" ? [] : [new("saved-display", "原屏幕", 0, 0, 1920, 1080, true)],
            scenario is "missing-microphone" or "disabled-microphone" ? [] : [new("saved-microphone", "原麦克风")],
            scenario is "missing-speaker" or "disabled-speaker" ? [] : [new("saved-speaker", "原扬声器")]);
        environment = environment with {
            Displays = [.. environment.Displays, new("other-display", "其他屏幕", 0, 0, 1280, 720, scenario == "missing-display")],
            Microphones = [.. environment.Microphones, new("other-microphone", "其他麦克风")],
            Speakers = [.. environment.Speakers, new("other-speaker", "其他扬声器")] };
        Func<Task<RecordingEnvironment>> probe = () => Task.FromResult(environment);
        var constructor = typeof(RecordingWindow).GetConstructors(flags).Single(c => c.GetParameters().Length == 3);
        RecordingWindow? window = null;
        try
        {
            window = (RecordingWindow)constructor.Invoke([client, pipe, probe]);
            Exercise(window, owner =>
            {
                try
                {
                    ComboBox Choice(string name) => (ComboBox)owner.FindName(name);
                    RecordingOptions? Options() => (RecordingOptions?)typeof(RecordingWindow).GetMethod("OptionsFromForm", flags)!.Invoke(window, null);
                    bool Unchanged() => before is null ? !File.Exists(path) : File.ReadAllBytes(path).SequenceEqual(before);
                    if (scenario != "new")
                        Assert(Choice("DisplayChoice").SelectedValue as string == saved.Display &&
                            Choice("SpeakerChoice").SelectedValue as string == saved.SpeakerId &&
                            Choice("MicrophoneChoice").SelectedValue as string == saved.MicrophoneId,
                            "device refresh silently replaced a saved selection");
                    if (scenario.StartsWith("missing-", StringComparison.Ordinal))
                    {
                        Assert(Options() is null && ((TextBlock)owner.FindName("RecordingError")).Text.Contains("不可用"),
                            "a missing fixed device was accepted for recording");
                        Assert(((TextBlock)owner.FindName("ReadySummary")).Text.Contains("不可用"), "idle summary hides the missing device");
                        Click(owner, "RecordStart"); Click(owner, "SaveSettings");
                        Assert(Unchanged(), "starting or saving silently persisted a replacement device");
                        string choice = scenario == "missing-display" ? "DisplayChoice" : scenario == "missing-speaker" ? "SpeakerChoice" : "MicrophoneChoice";
                        string replacementId = scenario.Replace("missing-", "other-", StringComparison.Ordinal);
                        Choice(choice).SelectedValue = replacementId;
                        Assert(Options() is not null && !((TextBlock)owner.FindName("ReadySummary")).Text.Contains("不可用") &&
                            !((TextBlock)owner.FindName("RecordingError")).Text.Contains("不可用"),
                            "explicit device selection retained an obsolete unavailable warning");
                        ((TextBlock)owner.FindName("RecordingError")).Text = "隔离后台错误";
                        Choice(choice).SelectedValue = scenario.Replace("missing-", "saved-", StringComparison.Ordinal);
                        Assert(((TextBlock)owner.FindName("RecordingError")).Text == "隔离后台错误", "device change hid unrelated backend feedback");
                        Assert(Options() is null && Unchanged(), "returning to the absent device bypassed validation or saved automatically");
                        if (scenario != "missing-display")
                        {
                            var audio = Check(owner, scenario == "missing-speaker" ? "SystemSound" : "MicrophoneSound");
                            audio.IsChecked = false;
                            Assert(Options() is not null && !((TextBlock)owner.FindName("RecordingError")).Text.Contains("不可用"),
                                "disabling the absent audio source did not clear its own warning");
                            audio.IsChecked = true;
                            Assert(Options() is null, "re-enabling the absent source bypassed device validation");
                        }
                        environment = environment with {
                            Displays = [new("saved-display", "原屏幕", 0, 0, 1920, 1080, true), .. environment.Displays.Where(d => d.Id != "saved-display")],
                            Microphones = [new("saved-microphone", "原麦克风"), .. environment.Microphones.Where(d => d.Id != "saved-microphone")],
                            Speakers = [new("saved-speaker", "原扬声器"), .. environment.Speakers.Where(d => d.Id != "saved-speaker")] };
                        var refresh = (Task)typeof(RecordingWindow).GetMethod("ProbeAsync", flags)!.Invoke(window, null)!;
                        PumpUntil(() => refresh.IsCompleted, "reconnected devices did not refresh"); refresh.GetAwaiter().GetResult();
                        Assert(Options() == saved, "reconnected device did not recover its original fixed selection");
                        Choice(choice).SelectedValue = replacementId;
                        var replacement = Options();
                        Assert(replacement is not null && replacement != saved && Unchanged(), "explicit replacement changed saved preferences before confirmation");
                        Click(owner, "SaveSettings");
                        var actual = (RecordingOptions?)typeof(RecordingWindow).GetMethod("ReadSavedOptions", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [pipe]);
                        Assert(actual == replacement, "an explicit replacement could not be saved");
                    }
                    else
                    {
                        var options = Options();
                        Assert(options is not null, "disabled missing audio or an explicit system default blocked recording");
                        if (scenario is "default" or "new") Assert(options!.SpeakerId == "default" && options.MicrophoneId == "default", "first-run or explicit default behaviour changed");
                        Assert(Unchanged(), "enumeration wrote preferences without a save");
                    }
                    Checks.Add(scenario + ": actual recording page preserves fixed selections, rejects absent enabled sources, and recovers/reselects explicitly; synthetic inventory, detached client, no capture");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window?.Shutdown();
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }
}
