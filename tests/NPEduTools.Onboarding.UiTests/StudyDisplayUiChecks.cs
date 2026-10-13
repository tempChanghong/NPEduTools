using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private sealed class StudyUiClock : TimeProvider
    { public long Ms; public override long TimestampFrequency => 1000; public override long GetTimestamp() => Ms; }
    private static StudyHostState StudyFixture(string phase = "Active", double seconds = 600)
    {
        var start = new DateTimeOffset(2026, 10, 12, 19, 0, 0, TimeSpan.Zero);
        var settings = new StudySettings(new("Asia/Shanghai", "2026-10-12", null, [], []), new("StatusTrend", 180),
            new(true, "OwnClass", [], 60), new(true, -40, -50, 600, 5), new(false, 5, 30, 30, true, false));
        return new(new string('a', 64), "APPLIED", "NOT_INTEGRATED", "ACTIVE", phase, "WINDOW_ACTIVE", true,
            start.AddSeconds(seconds), new(start, start.AddMinutes(150)), null, settings,
            new("Schedule", "Active", Guid.NewGuid(), "WINDOW_ACTIVE", true, null, null, true), new(Guid.NewGuid(), true, true, 0));
    }
    private static void RunStudyDisplayChecks()
    {
        RunStudyIdleHideChecks();
        RunManualStudyOperationChecks();
        RunStudyTimingAndSidebarChecks();
        RunSeatingLifecycleChecks();
        RunStudyProgressChecks();
        RunSeatingUploadChecks();
        RunStudyEstimateVisualChecks();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try { RunSeatingVisualChecks(); RunSeatingPipeChecks(); RunSeatingMemoryChecks(); RunStudyVisualChecks(); RunStudyStageAudioRecoveryChecks(); RunStudyThresholdVisualChecks(); RunStudyDisciplinePipeChecks(); RunStudyDisciplineDelayedChecks(); RunStudyDisciplineLifecycleChecks(); RunStudyHomeworkVisualChecks(); RunStudyHomeworkPipeChecks(false); RunStudyHomeworkPipeChecks(true); RunStudyPresentationPipeChecks(); RunStudyDeniedAudioChecks(); RunStudyHideChecks(false, false); RunStudyHideChecks(true, false); RunStudyHideChecks(false, true); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunStudyDeniedAudioChecks()
    {
        var permission = new TaskCompletionSource<bool>();
        var state = StudyFixture("Preparing", -120); var clock = new StudyUiClock();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => permission.Task);
        var audio = (StudyReminderAudio)typeof(StudySurface).GetField("_audio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;
        try
        {
            surface.Apply(state, null); Assert(audio.Requests == 0, "reminder played before Host permission");
            surface.Suspend(); permission.SetResult(true);
            bool drained = false; Application.Current.Dispatcher.BeginInvoke(() => drained = true);
            PumpUntil(() => drained, "audio permission continuation did not drain");
            Assert(audio.Requests == 0, "late permission played after hiding");
        }
        finally { surface.Stop(); }
        surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => Task.FromResult(false));
        try
        {
            audio = (StudyReminderAudio)typeof(StudySurface).GetField("_audio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;
            surface.Apply(state, null); Assert(audio.Requests == 0, "suppressed Host claim still played audio on restarted App");
            Checks.Add("Study audio: permission required; delayed result after hide ignored; suppressed claim on another App cannot replay cue");
        }
        finally { surface.Stop(); }
    }
    private static void RunStudyVisualChecks()
    {
        var clock = new StudyUiClock(); int hides = 0;
        var surface = new StudySurface(() => { hides++; return Task.CompletedTask; }, () => { }, true, clock);
        var owner = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        T Element<T>(string name) where T : FrameworkElement => (T)surface.FindName(name);
        var audio = (StudyReminderAudio)typeof(StudySurface).GetField("_audio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;
        var state = StudyFixture("Preparing", -120);
        var noise = new NoiseState(Guid.NewGuid(), 1, "Active", "synthetic", "fixture", "合成麦克风", null, -57, "Good",
            new(600, 600, 1, -60, -40, 0, 100), [new(550, -65, "Good"), new(550.5, -57, "Good")], SessionId: state.Execution!.SessionId);
        try
        {
            Exercise(owner, window =>
            {
                surface.Apply(state, noise);
                PumpUntil(() => Element<StackPanel>("NoticeCopy").Opacity == 1, "preparation intro did not settle");
                Assert(Element<TextBlock>("NoticeTitle").Text == "准备开始" && Element<Border>("StageNotice").Visibility == Visibility.Visible, "preparation overlay missing");
                Assert(Element<TextBlock>("Clock").Text == "18:58:00", "school wall stamp was shifted by Windows timezone");
                PumpUntil(() => audio.Ready || audio.Error is not null, "reference audio did not load");
                Assert(audio.Ready && audio.LastCue == "exam-alert", "preparation audio asset missing or unsupported");
                Snapshot(window, "study-preparing.png");
                for (int second = 1; second <= 119; second++)
                { clock.Ms += 1000; surface.Apply(state with { SchoolNow = state.Window!.Start.AddSeconds(second - 120) }, noise); }
                Assert(Element<Border>("StageNotice").Visibility == Visibility.Collapsed && audio.Requests == 1, "expired preparation remained or repeated sound");
                clock.Ms += 1000; state = state with { Phase = "Active", SchoolNow = state.Window!.Start }; surface.Apply(state, noise);
                Assert(Element<TextBlock>("NoticeTitle").Text == "自习开始" && ((SolidColorBrush)Element<Border>("StageNotice").Background).Color.R == 255, "start did not replace preparation with red");
                PumpUntil(() => Element<StackPanel>("NoticeCopy").Opacity == 1, "start intro did not settle"); Snapshot(window, "study-start.png");
                for (int second = 1; second <= 5; second++)
                { clock.Ms += 1000; surface.Apply(state with { SchoolNow = state.Window!.Start.AddSeconds(second) }, noise); }
                Assert(Element<Border>("StageNotice").Visibility == Visibility.Collapsed && audio.Requests == 2, "start notice exceeded five seconds or repeated");
                state = state with { SchoolNow = state.Window!.Start.AddMinutes(100) }; surface.Apply(state, noise);
                Assert(Element<TextBlock>("ProgressPercent").Text == "66%", "time progress is not actual window progress");
                Assert(Element<TextBlock>("NoiseStatus").Text == "正在监测" && Element<Canvas>("TrendCanvas").Visibility == Visibility.Visible, "matching fresh execution did not show trend");
                Assert(Element<TextBlock>("Clock").Text == "20:40:00", "clock is using local time");
                var typeface = new Typeface(Element<TextBlock>("Clock").FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                Assert(typeface.TryGetGlyphTypeface(out var glyph) && glyph.FamilyNames.Values.Contains("TCloudNumber"), "reference number font did not resolve");
                Snapshot(window, "study-active.png");
                state = state with { Settings = state.Settings! with { Presentation = new("Status", 180) } }; surface.Apply(state, noise);
                Assert(Element<Canvas>("TrendCanvas").Visibility == Visibility.Collapsed && Element<TextBlock>("Dbfs").Visibility == Visibility.Collapsed, "status-only setting ignored");
                state = state with { Settings = state.Settings! with { Presentation = new("StatusDbfs", 180) } }; surface.Apply(state, noise);
                Assert(Element<TextBlock>("Dbfs").Visibility == Visibility.Visible && Element<Canvas>("TrendCanvas").Visibility == Visibility.Collapsed, "numeric dBFS setting ignored");
                Assert(Element<TextBlock>("Dbfs").Text.EndsWith(" dBFS"), "digital level lost its unit");
                surface.Apply(state, noise with { Quality = "NoData" });
                Assert(Element<TextBlock>("NoiseStatus").Text != "正在监测", "stale microphone frame mislabeled collecting");
                surface.Apply(state, noise with { SessionId = Guid.NewGuid() });
                Assert(Element<TextBlock>("NoiseStatus").Text != "正在监测", "unrelated microphone session mislabeled collecting");
                surface.Apply(state with { Execution = state.Execution! with { Owner = "Manual", SignalFresh = false } }, noise);
                Assert(Element<TextBlock>("NoiseStatus").Text == "当前为手动监测", "manual ownership lost");
                surface.Apply(state, noise); surface.BeginHold(); clock.Ms += 1900; surface.Apply(state, noise);
                Assert(hides == 0, "short press hid study"); surface.CancelHold(); clock.Ms += 200; surface.Apply(state, noise);
                Assert(hides == 0, "cancelled hold was still executed");
                surface.BeginHold(); for (int i = 0; i < 4; i++) { clock.Ms += 500; surface.Apply(state, noise); }
                PumpUntil(() => hides == 1, "two-second long hold was not executed");
                clock.Ms += 1500;
                PumpUntil(() => Element<TextBlock>("NoiseStatus").Text == "监测状态待确认", "disconnected observations stayed live");
                Assert(!Element<Button>("HoldButton").IsEnabled && Element<Border>("StageNotice").Visibility == Visibility.Collapsed, "stale display retains controls or notice");
                Assert(Element<TextBlock>("Clock").Text == "20:40:00", "freshness watchdog advanced school time");
                state = state with { Phase = "Ended", SchoolNow = state.Window!.End }; surface.Apply(state, noise);
                Assert(Element<TextBlock>("NoticeTitle").Text == "自习结束" && ((SolidColorBrush)Element<Border>("StageNotice").Background).Color.G == 204, "end is not green");
                PumpUntil(() => Element<StackPanel>("NoticeCopy").Opacity == 1, "end intro did not settle"); Snapshot(window, "study-end.png");
                for (int i = 0; i < 5; i++) { clock.Ms += 1000; surface.Apply(state with { SchoolNow = state.Window!.End.AddSeconds(i + 1) }, noise); }
                Assert(Element<Border>("StageNotice").Visibility == Visibility.Collapsed && audio.Requests == 3, "end notice duration or sound count wrong");
                var memory = System.Diagnostics.Process.GetCurrentProcess();
                System.IO.File.WriteAllText(System.IO.Path.Combine(_output, "memory-fixture.json"), System.Text.Json.JsonSerializer.Serialize(new
                { workingSetBytes = memory.WorkingSet64, privateBytes = memory.PrivateMemorySize64, managedHeapBytes = GC.GetTotalMemory(false),
                    kind = "isolated-wpf-test-process-after-screenshots", fullApp = false, browserRuntime = false, realDevices = false }));
                Checks.Add("Study WPF: preparation ten seconds, red start 5s, green end 5s, reference font/audio muted, real progress, three modes, session/freshness fences, long hold and cancel; no real Host/devices");
                window.Close();
            });
        }
        finally { surface.Stop(); owner.Close(); }
    }
    private static void RunStudyHideChecks(bool replacement, bool disconnect)
    {
        string pipe = "NPEduTools.Test.study-display." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var state = StudyFixture(); var window = new StudyWindow(pipe, () => { }, true) { Width = 1100, Height = 700 };
        var surface = (StudySurface)window.Content;
        try
        {
            Exercise(window, _ =>
            {
                Assert(window.Topmost, "study window is not fullscreen above ordinary application windows");
                window.Apply(state, null);
                window.Close(); Assert(window.IsVisible, "ordinary close bypasses long hold");
                async Task<HostRequest> Read() { await server.WaitForConnectionAsync(timeout.Token); return await Protocol.ReadAsync<HostRequest>(server, timeout.Token); }
                var read = Read(); var operation = surface.HideAsync(); PumpUntil(() => read.IsCompleted, "study hide did not connect");
                var request = read.GetAwaiter().GetResult();
                Assert(request.Capability == "study.hide" && Protocol.Validate(request) is null && request.Noise is null, "hide command can alter capture");
                if (replacement) { state = state with { Version = new string('b', 64) }; window.Apply(state, null); }
                if (disconnect) server.Disconnect();
                else
                {
                    var write = Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Succeeded", null, "fixture", Study: StudyFixture() with
                        { Version = request.StudyHide!.Version, Display = new(request.StudyHide.InstanceId, true, false, 180) }), timeout.Token);
                    PumpUntil(() => write.IsCompleted, "study hide reply did not send"); write.GetAwaiter().GetResult();
                }
                PumpUntil(() => operation.IsCompleted, "study hide did not settle"); operation.GetAwaiter().GetResult();
                Assert(window.IsVisible == (replacement || disconnect), "stale or unconfirmed hide changed window visibility");
                if (replacement || disconnect)
                {
                    if (server.IsConnected) server.Disconnect();
                    var retryRead = Read(); var retry = surface.HideAsync(); PumpUntil(() => retryRead.IsCompleted, "retry did not connect");
                    var next = retryRead.GetAwaiter().GetResult();
                    Assert((next.RequestId == request.RequestId) == disconnect, "uncertain same-context retry renewed identity, or new context reused old identity");
                    Assert(next.StudyHide!.Version == state.Version, "retry carries old configuration");
                    var write = Protocol.WriteAsync(server, new HostResponse(1, next.RequestId, "Rejected", "STUDY_STATE_CHANGED", "fixture"), timeout.Token);
                    PumpUntil(() => write.IsCompleted && retry.IsCompleted, "retry did not settle"); write.GetAwaiter().GetResult(); retry.GetAwaiter().GetResult();
                    Assert(window.IsVisible, "rejected hide closed window");
                }
                Checks.Add($"Study hide: replacement={replacement}, disconnect={disconnect}; identity fenced, uncertain id reused, no microphone command");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
