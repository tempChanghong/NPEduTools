using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunAutomaticRecordingChecks()
    {
        RunCorruptRecordingPreviewChecks();
        string pipe = "NPEduTools.Test.automatic-ui." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        var clientType = typeof(AutoRecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        // Cancel the nonexistent-pipe poll. Apply only synthetic responses, never a
        // real recorder, device probe, lease, or enable command.
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var apply = clientType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var constructor = typeof(AutoRecordingWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        AutoRecordingWindow? window = null;
        int settingsOpened = 0;
        try
        {
            window = (AutoRecordingWindow)constructor.Invoke([pipe, client, (Action)(() => settingsOpened++)]);
            Exercise(window, owner =>
            {
                string Text(string name) => ((TextBlock)owner.FindName(name)).Text;
                var idle = new AutomaticRecordingState(false, Guid.Empty, "自动录课未启用", null, []);
                void Show(AutomaticRecordingState state) => apply.Invoke(client,
                    [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离回执", Automatic: state, Recording: new("Idle", "隔离空闲"))]);
                Show(idle);
                var preview = Text("PreviewStatus");
                Click(owner, "ToggleReal"); // No saved options: validation exits before any recording command.
                string validation = "请先到“立即录制 → 修改录制设置”配置屏幕、音源和目录";
                bool visibleValidation = Descendants<TextBlock>(owner).Any(block => block.Text.Contains(validation));
                Assert(visibleValidation, "missing recording settings were not explained");

                var record = new RecordingExecution("fixture", Guid.NewGuid(), new(2026, 10, 7), false,
                    new(2026, 10, 7, 10, 0, 0, TimeSpan.FromHours(8)),
                    new(2026, 10, 7, 10, 40, 0, TimeSpan.FromHours(8)),
                    "隔离测试课程", Guid.NewGuid(), "Recorded", "已保存", OutputFile: "fixture.mp4");
                var active = idle with { Enabled = true, Message = "正在自动录制：隔离测试课程", Recent = [record] };
                Show(active);
                Assert(Text("RealStatus").Contains(active.Message), "a previous operation error masks the fresh recording status");
                Assert(Text("AutomaticFeedbackText").Contains(validation), "validation feedback vanished instead of remaining separate from live status");
                Assert(Text("PreviewStatus") == preview, "recording status changed the independent trial status");
                Assert(Button(owner, "ToggleReal").Content.ToString() == "关闭自动录课", "master toggle no longer follows the actual response");
                clientType.GetMethod("MarkUnavailable", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [null, null]);
                Assert(!Button(owner, "ToggleReal").IsEnabled && !Button(owner, "RealSkip").IsEnabled && !Button(owner, "RealSkipDay").IsEnabled,
                    "disconnected automatic page still permits changing today's schedule");
                Show(active);
                Assert(Button(owner, "ToggleReal").IsEnabled && Button(owner, "RealSkip").IsEnabled && Button(owner, "RealSkipDay").IsEnabled,
                    "fresh automatic snapshot did not restore controls");
                Checks.Add("Missing saved options fails before recording; a later actual recording response replaces live status while retaining separate operation feedback");

                Show(active with { Message = "录制后台连接中断；等待重新连接", Error = "当前运行状态未知" });
                Assert(Text("RealStatus").Contains("连接中断") && Text("RealStatus").Contains("未知"), "old operation feedback masks a disconnected status");
                Show(active with { Message = "自动录课已恢复，等待下一项", Error = null });
                Assert(Text("RealStatus").Contains("已恢复") && !Text("RealStatus").Contains("连接中断"), "recovery leaves a stale live message");
                var list = (ListBox)owner.FindName("RealEvents");
                Assert(list.Items.Count == 1 && list.Items[0].ToString()!.Contains("视频：fixture.mp4"), "actual execution records were lost");
                Assert(((TextBlock)owner.FindName("RealEventsEmpty")).Visibility == Visibility.Collapsed, "real records show an empty-state message");
                Checks.Add("Disconnect and recovery remain visible after an operation error; execution history still lists the saved-video path");

                var sameRows = list.ItemsSource;
                Show(active);
                Assert(ReferenceEquals(sameRows, list.ItemsSource), "identical polling snapshot rebuilt history and reset reading position");
                var recordFilter = (ComboBox)window.FindName("RecordFilter");
                recordFilter.SelectedIndex = 1;
                Assert(list.Items.Count == 0, "attention filter includes a successfully saved recording");
                recordFilter.SelectedIndex = 2;
                Assert(list.Items.Count == 1, "saved filter lost a recorded video");
                recordFilter.SelectedIndex = 0;
                Checks.Add("Actual record filters distinguish saved/attention states; unchanged polling keeps the history ItemsSource and reading position");
                Show(idle);
                File.WriteAllText(preferences.Replace(".startup.json", ".recording.json", StringComparison.Ordinal), "{");
                Click(owner, "ToggleReal");
                Assert(Text("AutomaticFeedbackText").Contains("录制设置无法读取"), "invalid saved settings lack separate feedback");
                Assert(File.ReadAllText(preferences.Replace(".startup.json", ".recording.json", StringComparison.Ordinal)) == "{", "rendering or validation rewrote unreadable saved options");
                Show(active);
                Assert(Text("RealStatus").Contains(active.Message), "unreadable settings mask the actual recording state");
                Snapshot(owner, "automatic-status-recovered.png");
                Checks.Add("Unreadable saved options: preserve the file, show operation feedback, and continue displaying fresh actual status");

                Show(active with { Message = "课堂模式暂停自动录课", SuspendedByMode = true });
                Assert(Button(owner, "ToggleReal").ToolTip.ToString()!.Contains("暂停仍然有效"), "mode suspension warning changed");
                Click(owner, "AutoRecordingSettings", true);
                Assert(settingsOpened == 1, "settings action no longer uses the existing entry point");
                Show(idle);
                Assert(((TextBlock)owner.FindName("RealEventsEmpty")).Visibility == Visibility.Visible, "empty execution history lacks its explanation");
                owner.Width = 1040; owner.Height = 700;
                Snapshot(owner, "automatic-status-minimum.png");
                Assert(((TextBlock)owner.FindName("AutomaticFeedbackText")).ActualHeight > 0, "minimum window loses operation feedback");
                Assert(Button(owner, "AutoHide", true).ActualHeight > 0, "minimum window loses footer controls");
                Checks.Add("Trial stays independent; preserve mode-pause warning, settings action, empty history, and minimum layout");
                window.Shutdown();
            });
        }
        finally
        {
            window?.Shutdown();
            clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string suffix in new[] { ".recording-preview.json", ".recording-plans.json", ".recording.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
    private static void RunCorruptRecordingPreviewChecks()
    {
        string pipe = "NPEduTools.Test.corrupt-preview-ui." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        string path = preferences.Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
        var clientType = typeof(AutoRecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        clientType.GetMethod("Detach")!.Invoke(client, null);
        var constructor = typeof(AutoRecordingWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var original = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new PreviewState(2, new(), [new(null, "应开始", "语文", null!)], []));
        AutoRecordingWindow? window = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, original);
            window = (AutoRecordingWindow)constructor.Invoke([pipe, client, (Action)(() => { })]);
            Assert(((TextBlock)window.FindName("ErrorText")).Text.Contains("原文件已保留"), "corrupt trial history is not explained");
            Assert(!Button(window, "TogglePreview").IsEnabled, "corrupt history still permits trial writes");
            clientType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client,
                [new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "isolated active recording",
                    Recording: new("Recording", "隔离后台仍在录制", Control: new("Automatic", Guid.NewGuid(), "fixture", 0, 0)),
                    Automatic: new(true, Guid.NewGuid(), "隔离自动录课仍在运行", null, []))]);
            Assert(Button(window, "StopCurrent").IsEnabled && ((TextBlock)window.FindName("LatestRecording")).Text.Contains("仍在录制"),
                "corrupt rehearsal history interfered with actual recording controls/status");
            Assert(File.ReadAllBytes(path).SequenceEqual(original), "opening the recording page changed corrupt history");
            Checks.Add("Malformed local trial history: null event text cannot crash page construction; preserve bytes, disable trial writes, retain fresh actual recording status and stop control; no recorder/sensors");
        }
        finally
        {
            window?.Shutdown(); clientType.GetMethod("Detach")!.Invoke(client, null);
            foreach (string suffix in new[] { ".recording-preview.json", ".recording-plans.json", ".recording-preview.json.pre-plan-book.bak" })
            {
                string item = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(item)) File.Delete(item);
            }
        }
    }
}
