using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingPreferencesChecks()
    {
        foreach (string scenario in new[] { "version", "null-options", "invalid-options", "oversized", "invalid-json", "valid", "absent" })
            RunRecordingPreferencesCheck(scenario);
    }

    private static void RunRecordingPreferencesCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-preferences." + Guid.NewGuid().ToString("N");
        string path = StartupPreferencesStore.PathFor(pipe).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        var options = new RecordingOptions("fixture", Path.GetTempPath(), FramesPerSecond: 15, MaximumHeight: 720,
            SystemAudio: false, Microphone: false);
        string valid = JsonSerializer.Serialize(new { Version = 1, Options = options });
        string? content = scenario switch
        {
            "version" => JsonSerializer.Serialize(new { Version = 999, Options = options }),
            "null-options" => "{\"Version\":1,\"Options\":null}",
            "invalid-options" => JsonSerializer.Serialize(new { Version = 1, Options = options with { FramesPerSecond = 60 } }),
            "oversized" => new string(' ', 16385),
            "invalid-json" => "{",
            "valid" => valid,
            _ => null
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (content is not null) File.WriteAllText(path, content);
        byte[]? original = content is null ? null : File.ReadAllBytes(path);
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        type.GetMethod("Detach")!.Invoke(client, null);
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        PumpUntil(() => poll.IsCompleted, "isolated preferences client did not stop its poll");
        type.GetMethod("Apply", flags)!.Invoke(client,
            [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离状态", Recording: new("Idle", "隔离准备录制"))]);
        Func<Task<RecordingEnvironment>> probe = () => Task.FromResult(new RecordingEnvironment(true, null,
            [new("fixture", "隔离测试屏幕", 0, 0, 1920, 1080, true)], [], []));
        var constructor = typeof(RecordingWindow).GetConstructors(flags).Single(c => c.GetParameters().Length == 3);
        RecordingWindow? window = null;
        bool invalid = scenario is not ("valid" or "absent");
        try
        {
            window = (RecordingWindow)constructor.Invoke([client, pipe, probe]);
            Exercise(window, owner =>
            {
                try
                {
                    string Feedback() => ((TextBlock)owner.FindName("RecordingError")).Text;
                    Assert(Button(owner, "RecordStart").IsEnabled, "preferences problem prevents choosing temporary options with a ready synthetic device");
                    Assert((bool)typeof(RecordingWindow).GetField("_readable", flags)!.GetValue(window)! == !invalid,
                        "preferences readability does not match the file result");
                    if (invalid)
                    {
                        Assert(Feedback().Contains("原文件已保留"), "invalid preferences did not explain preservation");
                        Assert(((TextBox)owner.FindName("OutputDirectory")).Text != options.OutputDirectory, "invalid file partially applied saved options");
                    }
                    else if (scenario == "valid")
                    {
                        Assert(((TextBox)owner.FindName("OutputDirectory")).Text == options.OutputDirectory &&
                            ((ComboBox)owner.FindName("FpsChoice")).SelectedIndex == 1 &&
                            ((ComboBox)owner.FindName("QualityChoice")).SelectedIndex == 1, "valid preferences were not restored");
                    }
                    Check(owner, "SystemSound").IsChecked = Check(owner, "MicrophoneSound").IsChecked = false;
                    Click(owner, "SaveSettings"); // Saves only isolated preferences; never click Start.
                    if (invalid)
                    {
                        Assert(File.ReadAllBytes(path).SequenceEqual(original!), "invalid preferences were silently overwritten");
                        Assert(Feedback().Contains("原文件已保留"), "save attempt erased invalid-preferences feedback");
                        if (scenario == "version")
                        {
                            owner.Width = 540; owner.Height = 630;
                            Snapshot(owner, "invalid-preferences-minimum.png");
                            Descendants<ScrollViewer>(owner).First().ScrollToBottom();
                            owner.UpdateLayout();
                            Snapshot(owner, "invalid-preferences-details.png");
                        }
                    }
                    else
                    {
                        var saved = (RecordingOptions?)typeof(RecordingWindow).GetMethod("ReadSavedOptions", BindingFlags.Static | BindingFlags.NonPublic)!
                            .Invoke(null, [pipe]);
                        Assert(saved?.Display == "fixture" && Feedback().Contains("录制设置已保存"), "healthy preferences cannot be saved");
                    }
                    Checks.Add(scenario + ": actual recording window opens with a synthetic device; invalid file remains intact, or healthy settings load and save");
                }
                finally { window.Shutdown(); }
            });
            if (scenario == "version")
            {
                // A repaired file is picked up when reopening; the old window never overwrites it.
                File.WriteAllText(path, valid);
                window = (RecordingWindow)constructor.Invoke([client, pipe, probe]);
                Exercise(window, owner =>
                {
                    try
                    {
                        Assert((bool)typeof(RecordingWindow).GetField("_readable", flags)!.GetValue(window)! &&
                            ((TextBox)owner.FindName("OutputDirectory")).Text == options.OutputDirectory,
                            "reopening did not recover after the isolated preferences file was repaired");
                    }
                    finally { window.Shutdown(); }
                });
            }
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
