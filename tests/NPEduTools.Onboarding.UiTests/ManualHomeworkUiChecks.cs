using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;
using NPEduTools.Host;

internal static partial class Program
{
    private sealed class ManualHomeworkUiClock : TimeProvider
    {
        public long Ms;
        private readonly DateTimeOffset _origin = DateTimeOffset.Now.Date.AddHours(10);
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
        public override DateTimeOffset GetUtcNow() => _origin.AddMilliseconds(Ms).ToUniversalTime();
    }
    private static void RunManualHomeworkUiChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        var clock = new ManualHomeworkUiClock();
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/study-device-cases.json")))![0]!["value"]!.DeepClone().AsObject();
        config["effective"]!["settings"]!["schedule"]!["rules"] = new JsonArray();
        var study = new StudySettingsService(() => SchoolClockFrame.Unavailable("isolated absent ClassIsland"), () => false, clock, false);
        study.BindStudy(config["identity"]!.ToJsonString()); study.ApplyStudy(config);
        var noise = new NoiseService(_ => throw new InvalidOperationException("No capture is needed for this preparation-only fixture"), () => [new("mic", "Synthetic")], clock);
        var manual = new ManualStudyService(noise, study.DisplaySnapshot, () => false, Path.Combine(_output, "manual-homework"), clock, false, displayLeadSeconds: () => 600);
        study.ManualHomeworkNow = manual.HomeworkNow;
        var initial = manual.Status();
        var started = manual.Handle(new(1, Guid.NewGuid(), "manual.study.command", ManualStudy: new("start", initial.InstanceId,
            initial.Revision, DeviceId: "mic", StartUtc: clock.GetUtcNow().AddMinutes(5), EndUtc: clock.GetUtcNow().AddMinutes(10))));
        string pipe = "NPEduTools.Test.manual-homework." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => new PipeServer(pipe, new DeviceReader(), _ => { }, noise: noise, study: study, manualStudy: manual).RunAsync(stop.Token));
        var window = new StudyWindow(pipe, () => { }, true, clock, manual: true) { Width = 1100, Height = 700, Topmost = false };
        var surface = (StudySurface)window.Content;
        var grid = (Grid)surface.FindName("HomeworkGrid");
        string Shown() => string.Join("\n", grid.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()).Select(text => text.Text));
        void Publish(string content, int revision)
        {
            var request = study.StudyHomeworkRequest()!;
            var payload = new StudyHomeworkSnapshot(request["version"]!.GetValue<string>(), request["date"]!.GetValue<string>(),
                [new("manual-work", revision, "物理", "", content, [new("one", "行政一班"), new("walk", "物理走班 A")])]);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Protocol.Json);
            study.ApplyStudyHomework(JsonNode.Parse(bytes)!.AsObject(), bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        void Refresh()
        {
            // This fixture jumps its synthetic clock by 30 seconds at a time.
            // A real dispatcher tick while awaiting the private pipe would
            // correctly mark that old sample stale and restart reading time.
            // Render only through the fresh explicit Apply at these jumps.
            var timer = (DispatcherTimer)typeof(StudySurface).GetField("_timer", WorkspaceFlags)!.GetValue(surface)!;
            timer.Stop();
            var reading = HostClient.RequestAsync(pipe, "manual.study.status", stop.Token);
            PumpUntil(() => reading.IsCompleted, "manual homework status did not settle");
            var reply = reading.GetAwaiter().GetResult();
            Assert(reply.Outcome == "Succeeded" && reply.Study?.Manual is not null, "actual manual status missing");
            window.Apply(reply.Study!, reply.Noise);
            timer.Stop();
        }
        try
        {
            Assert(started.Outcome == "Succeeded" && manual.Status().State == "Preparing", "manual preparation was not created");
            Exercise(window, _ =>
            {
                clock.Ms = 12000; study.ApplyStudy(config); // Preparation cue has finished; no recording has started.
                Publish("原手动作业", 1); Refresh();
                PumpUntil(() => Shown().Contains("原手动作业"), "manual homework never reached the actual WPF surface");
                Assert(Shown().Contains("行政一班") && Shown().Contains("物理走班 A"), "manual display lost source labels");
                Assert(((FrameworkElement)surface.FindName("SeatingButton")).Visibility == Visibility.Collapsed, "manual homework exposed seating");
                Assert(!study.Snapshot().ClockReady && study.Snapshot().Phase == "Blocked", "fixture accidentally used a scheduled window/clock");
                Publish("更新手动作业", 2); Refresh();
                PumpUntil(() => ((TextBlock)surface.FindName("WorkUpdateLabel")).Text.Contains("本页读完"), "manual update did not reach the reading boundary queue");
                Assert(Shown().Contains("原手动作业") && !Shown().Contains("更新手动作业"), "update interrupted the current page");
                // The real network task refreshes unchanged content every 30s;
                // keep that cache fresh while the page finishes its 60s interval.
                clock.Ms += 30000; study.ApplyStudy(config); Publish("更新手动作业", 2); manual.Tick(); Refresh();
                clock.Ms += 30000; study.ApplyStudy(config); manual.Tick(); Refresh();
                Assert(Shown().Contains("更新手动作业") && !Shown().Contains("原手动作业"), "manual update never rotated into view");
                config["version"] = new string('b', 64); study.ApplyStudy(config); Publish("新来源策略作业", 3); Refresh();
                Assert(!Shown().Contains("更新手动作业"), "manual session retained homework from a superseded school configuration");
                PumpUntil(() => Shown().Contains("新来源策略作业"), "new school homework policy never reached manual WPF");
                study.BindStudy(null); Refresh();
                Assert(!Shown().Contains("手动作业") && manual.Status().State == "Preparing", "unbind retained school homework or cancelled the manual task");
                Checks.Add("Actual manual Host/StudySettings/private pipe/WPF: no school clock or scheduled window, bound-source homework loads, updates wait for 60s reading boundary, unbind removes homework while reservation continues; no capture");
                window.Shutdown();
            });
        }
        finally
        {
            window.Shutdown(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            manual.DisposeAsync().AsTask().GetAwaiter().GetResult(); noise.DisposeAsync().AsTask().GetAwaiter().GetResult(); study.DisposeAsync().AsTask().GetAwaiter().GetResult();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
