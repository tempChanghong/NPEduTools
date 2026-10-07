using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNoiseStatusChecks()
    {
        // A unique nonexistent pipe cannot reach the user's Host. Synchronous rendering
        // checks finish before the first request completes; Shutdown cancels that request.
        var window = new NoiseWindow("NPEduTools.Test.noise-ui." + Guid.NewGuid().ToString("N"));
        try
        {
            Exercise(window, owner =>
            {
                var active = new NoiseState(Guid.NewGuid(), 2, "Active", "本机监测中；不保存或上传原音频。",
                    "fixture", "隔离测试麦克风", DateTimeOffset.UtcNow, -57, "Good",
                    new(30, 29.6, 29.6 / 30, -78.6, -48.3, 0, 100),
                    [new(1, -65, "Good"), new(1.5, -57, "Good")], SessionId: Guid.NewGuid());
                var render = typeof(NoiseWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                void Show(NoiseState? state) => render.Invoke(window, [state]);
                string Text(string name) => ((TextBlock)owner.FindName(name)).Text;
                var choices = (ComboBox)owner.FindName("Microphone");
                choices.ItemsSource = new[] { new NoiseDevice("fixture", "隔离测试麦克风") };
                choices.SelectedIndex = 0;

                Show(active);
                Assert(Text("LevelText") == "-57.0" && Text("SummaryText").Contains("29.6"), "active sampling missing");
                Assert(!Button(owner, "StartButton").IsEnabled && Button(owner, "StopButton").IsEnabled, "active controls changed");
                Show(null);
                bool staleSummary = Text("SummaryText").Contains("29.6");
                Assert(Text("StateText").Contains("未知") && Text("LevelText") == "—", "disconnect reported stopped or kept a live level");
                Assert(!Button(owner, "StartButton").IsEnabled && !Button(owner, "StopButton").IsEnabled, "unknown state enables capture commands");
                Assert(((Canvas)owner.FindName("TrendCanvas")).Children.Count == 0, "old trend survived disconnect");
                Snapshot(owner, "noise-disconnected.png");

                Show(active);
                bool staleGuidance = Text("MessageText").Contains("恢复连接") || Text("MessageText").Contains("旧读数");
                Assert(!staleSummary && !staleGuidance, $"Disconnected summary is stale: {staleSummary}; disconnect guidance survives recovery: {staleGuidance}");
                Assert(Text("StateHintText") == "" && ((TextBlock)owner.FindName("StateHintText")).Visibility == Visibility.Collapsed, "state hint survives recovery");
                Assert(Text("SummaryText").Contains("29.6") && Button(owner, "StopButton").IsEnabled, "fresh snapshot did not restore state and controls");
                Assert(Text("SummaryTitle").Contains("累计"), "active summary lacks accumulation label");
                Assert(Text("SummaryHint").Contains("不代表") && Text("SummaryHint").Contains("上传"), "local statistics claim upload completion");
                ((TextBlock)owner.FindName("MessageText")).Text = "本次操作被拒绝，请刷新后再试。";
                Show(active);
                Assert(Text("MessageText").Contains("被拒绝"), "rendering current state erases operation feedback");
                Show(null);
                Assert(Text("SummaryTitle").Contains("未知") && Text("StateHintText").Contains("恢复连接"), "disconnect lacks state-specific guidance");
                Show(active);
                Checks.Add("Active to disconnected to recovered: clear old statistics and trend; unknown disables start/stop; fresh snapshot restores without stale guidance");

                Show(active with { State = "Stopped", Message = "监测已停止，麦克风已释放。", CurrentDbfs = null });
                Assert(Text("SummaryTitle").Contains("上次") && Text("SummaryText").Contains("29.6"), "ended summary not preserved as history");
                Assert(Text("LevelText") == "—" && Button(owner, "StartButton").IsEnabled && !Button(owner, "StopButton").IsEnabled, "ended state controls or level changed");
                var scroll = Descendants<ScrollViewer>(owner).First();
                scroll.ScrollToBottom(); owner.UpdateLayout();
                Snapshot(owner, "noise-ended-summary.png");
                Checks.Add("Stopped: preserve ended statistics with an explicit history heading; retain existing device/start controls");

                Show(active with { State = "Faulted", Message = "无法采集所选麦克风，请检查设备连接。", CurrentDbfs = null, Quality = "NoData" });
                Assert(Text("SummaryTitle").Contains("中断") && Text("StateText").Contains("检查设备"), "fault summary hides interruption or backend advice");
                Checks.Add("Faulted: mark interrupted statistics and retain the Host failure message");

                Show(active with { State = "Stopped", Summary = null, Trend = [], CurrentDbfs = null, SessionId = null });
                Assert(!Text("SummaryText").Contains("29.6") && Text("SummaryTitle") == "监测统计", "fresh Host retains an old session summary");
                owner.Width = 650; owner.Height = 560;
                Snapshot(owner, "noise-minimum.png");
                scroll.ScrollToBottom(); owner.UpdateLayout();
                Assert(((TextBlock)owner.FindName("SummaryHint")).ActualHeight > 0, "minimum layout loses summary explanation");
                Snapshot(owner, "noise-summary-minimum.png");
                Checks.Add("New Host without a session clears old data; minimum window remains scrollable with statistics and protection guidance");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
