using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyDisciplineLifecycleChecks()
    {
        foreach (string change in new[] { "policy", "exam", "clock", "version", "window", "session", "manual", "signal", "noise", "transport", "end", "hide", "estimate-invalid", "estimate-warmup", "estimate-nodata", "estimate-missing" })
        {
            var state = StudyFixture(); var noise = ThresholdNoise(state); var clock = new StudyUiClock();
            if (change.StartsWith("estimate-", StringComparison.Ordinal))
            {
                state = state with { Settings = state.Settings! with { Noise = state.Settings!.Noise with { Estimated = new(65, 55) } } };
                noise = noise with { Estimate = new(new string('a', 64), "a-sos-band8500-v1|sample-energy-fs-v1", "ManualReference", "Slow",
                    90, "2026-10-11T00:00:00.000Z", "Active", null, 70, new(600, 1, 60, 70)) };
            }
            state = state with { Threshold = new(noise.SessionId!.Value, "High", Guid.NewGuid()) };
            var permission = new TaskCompletionSource<bool>(); int disciplineClaims = 0;
            var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (cue, _) =>
            { if (cue != "Discipline") return Task.FromResult(false); disciplineClaims++; return permission.Task; });
            try
            {
                surface.Apply(state, noise); Assert(disciplineClaims == 1 && StudyAudio(surface).Requests == 0, "missing pending discipline claim: " + change);
                var next = change switch
                {
                    "policy" => state with { PolicyState = "UNAVAILABLE" },
                    "exam" => state with { Phase = "Blocked", Reason = "EXAM_PAUSED", Window = null, Threshold = null },
                    "clock" => state with { ClockReady = false },
                    "version" => state with { Version = new string('b', 64) },
                    "window" => state with { Window = state.Window! with { Start = state.Window.Start.AddMinutes(-1) } },
                    "session" => state with { Execution = state.Execution! with { SessionId = Guid.NewGuid() }, Threshold = null },
                    "manual" => state with { Execution = state.Execution! with { Owner = "Manual" } },
                    "signal" => state with { Execution = state.Execution! with { SignalFresh = false } },
                    "end" => state with { Phase = "Ended", SchoolNow = state.Window!.End, Threshold = null },
                    _ => state
                };
                if (change == "hide") surface.Suspend();
                else
                {
                    var nextNoise = change switch
                    {
                        "noise" => noise with { Quality = "NoData" },
                        "estimate-invalid" => noise with { Estimate = noise.Estimate! with { State = "Invalidated", Reason = "INPUT_SETTINGS_CHANGED", CurrentDbA = null } },
                        "estimate-warmup" => noise with { Estimate = noise.Estimate! with { State = "WarmingUp", CurrentDbA = null } },
                        "estimate-nodata" => noise with { Estimate = noise.Estimate! with { State = "NoData", CurrentDbA = null } },
                        "estimate-missing" => noise with { Estimate = null },
                        _ => noise
                    };
                    surface.Apply(next, nextNoise, change == "transport" ? TimeSpan.FromSeconds(1.5) : default);
                }
                permission.SetResult(true);
                bool drained = false; Application.Current.Dispatcher.BeginInvoke(() => drained = true);
                PumpUntil(() => drained, "late discipline permission did not drain: " + change);
                Assert(StudyAudio(surface).Requests == 0, "late discipline permission played after " + change);
                var stage = (Border)surface.FindName("StageNotice");
                Assert(change == "end" ? ((TextBlock)surface.FindName("NoticeTitle")).Text == "自习结束" : stage.Visibility == Visibility.Collapsed,
                    "late discipline replaced current stage after " + change);
                if (change is not ("version" or "window"))
                    Assert(((Grid)surface.FindName("NoiseEdge")).Visibility == Visibility.Collapsed, "old edge survived " + change);
            }
            finally { surface.Stop(); }
        }
        Checks.Add("Study discipline lifecycle: 16 delayed-permission boundaries including estimate invalidation/warmup/no-data/missing; no late audio or overlay");
        RunEstimatedDisciplinePermissionCheck();
    }

    private static void RunEstimatedDisciplinePermissionCheck()
    {
        var state = StudyFixture(); var noise = ThresholdNoise(state); var clock = new StudyUiClock();
        state = state with { Settings = state.Settings! with { Noise = state.Settings!.Noise with { Estimated = new(65, 55) } },
            Threshold = new(noise.SessionId!.Value, "High", Guid.NewGuid()) };
        var estimate = new NoiseEstimate(new string('a', 64), "a-sos-band8500-v1|sample-energy-fs-v1", "ManualReference", "Slow",
            90, "2026-10-11T00:00:00.000Z", "Active", null, 70, new(600, 1, 60, 70));
        noise = noise with { Estimate = estimate };
        var permission = new TaskCompletionSource<bool>();
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => permission.Task);
        try
        {
            surface.Apply(state, noise);
            Assert(StudyAudio(surface).Requests == 0, "estimated discipline played before permission");
            permission.SetResult(true);
            PumpUntil(() => StudyAudio(surface).Requests == 1, "valid estimated discipline was suppressed");
            Assert(((TextBlock)surface.FindName("NoticeTitle")).Text == "注意纪律" &&
                ((Border)surface.FindName("StageNotice")).Visibility == Visibility.Visible, "valid estimated discipline lacks overlay");
            surface.Apply(state, noise with { Estimate = estimate with { State = "Invalidated", Reason = "INPUT_SETTINGS_CHANGED", CurrentDbA = null } });
            Assert(((Border)surface.FindName("StageNotice")).Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 1,
                "reference invalidation retained or replayed current discipline");
            Checks.Add("Estimated discipline: valid delayed permission still plays once; reference invalidation cancels the current overlay");
        }
        finally { surface.Stop(); }
    }

    private static NoiseState ThresholdNoise(StudyHostState state) => new(Guid.NewGuid(), 1, "Active", "synthetic", "fixture", "合成麦克风", null,
        -35, "Good", new(600, 600, 1, -60, -35, 0, 100), [], SessionId: state.Execution!.SessionId);
    private static StudyReminderAudio StudyAudio(StudySurface surface) =>
        (StudyReminderAudio)typeof(StudySurface).GetField("_audio", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(surface)!;
    private static void RunStudyThresholdVisualChecks()
    {
        var clock = new StudyUiClock(); var state = StudyFixture(); var noise = ThresholdNoise(state);
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => Task.FromResult(false));
        var owner = new Window { Content = surface, Width = 1440, Height = 810, WindowStyle = WindowStyle.None };
        var edge = (Grid)surface.FindName("NoiseEdge"); var stage = (Border)surface.FindName("StageNotice");
        try
        {
            Exercise(owner, window =>
            {
                state = state with { Threshold = new(noise.SessionId!.Value, "Medium") }; surface.Apply(state, noise with { CurrentDbfs = -45 });
                PumpUntil(() => edge.Opacity == .36, "medium edge animation did not settle"); Snapshot(window, "study-threshold-medium.png");
                Assert(edge.Visibility == Visibility.Visible && stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 0, "medium edge is not silent");
                state = state with { Threshold = new(noise.SessionId.Value, "High") }; surface.Apply(state, noise);
                PumpUntil(() => edge.Opacity == .85, "high edge animation did not settle"); Snapshot(window, "study-threshold-high.png");
                Assert(stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 0, "instant high band fabricated a qualified notice");
                state = state with { Threshold = new(noise.SessionId.Value, "High", Guid.NewGuid()) }; surface.Apply(state, noise);
                Assert(stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 0, "denied discipline claim displayed or played");
                surface.Apply(state with { Threshold = state.Threshold! with { ReminderError = "STUDY_REMINDER_STORE_UNAVAILABLE" } }, noise);
                Assert(edge.Visibility == Visibility.Visible && ((TextBlock)surface.FindName("NoiseStatus")).Text == "正在监测" &&
                    ((TextBlock)surface.FindName("NoiseDetail")).Text.Contains("纪律提醒暂不可用") && StudyAudio(surface).Requests == 0,
                    "cooldown journal failure stopped capture visual, hid gradient, or authorized sound");
                surface.Apply(state, noise with { Quality = "NoData" }); Assert(edge.Visibility == Visibility.Collapsed, "stale microphone kept red edge");
                surface.Apply(state, noise with { SessionId = Guid.NewGuid() }); Assert(edge.Visibility == Visibility.Collapsed, "unrelated session kept red edge");
                surface.Apply(state with { Execution = state.Execution! with { Owner = "Manual" } }, noise); Assert(edge.Visibility == Visibility.Collapsed, "manual session got scheduled visual protection");
                surface.Apply(state, noise); surface.Suspend(); Assert(edge.Visibility == Visibility.Collapsed, "hidden display retained edge animation");
                Checks.Add("Study thresholds WPF: medium silent gradient, high stronger, unqualified/denied no notice, freshness/session/manual/hide fences");
                window.Close();
            });
        }
        finally { surface.Stop(); owner.Close(); }
    }
    private static void RunStudyDisciplinePipeChecks()
    {
        string pipe = "NPEduTools.Test.study-discipline." + Guid.NewGuid().ToString("N");
        var clock = new StudyUiClock(); var state = StudyFixture(); var noise = ThresholdNoise(state); var id = Guid.NewGuid();
        state = state with { Threshold = new(noise.SessionId!.Value, "High", id) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await stream.WaitForConnectionAsync(timeout.Token); var request = await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
            Assert(request.Capability == "study.reminder" && Protocol.Validate(request) is null && request.StudyReminder is { Phase: "Discipline" } claim &&
                claim.DisciplineId == id && claim.SessionId == noise.SessionId && !Protocol.NoiseInterruption(request), "discipline pipe lost current event/session or alters capture");
            await Protocol.WriteAsync(stream, new HostResponse(1, request.RequestId, "Succeeded", null, "fixture"), timeout.Token);
        });
        var window = new StudyWindow(pipe, () => { }, true, clock); var surface = (StudySurface)window.Content;
        var stage = (Border)surface.FindName("StageNotice"); var title = (TextBlock)surface.FindName("NoticeTitle");
        try
        {
            Exercise(window, _ =>
            {
                window.Apply(state, noise); PumpUntil(() => title.Text == "注意纪律" && stage.Visibility == Visibility.Visible, "authorized discipline not presented");
                PumpUntil(() => ((StackPanel)surface.FindName("NoticeCopy")).Opacity == 1, "discipline intro did not settle");
                PumpUntil(() => StudyAudio(surface).Ready || StudyAudio(surface).Error is not null, "discipline audio did not load");
                AssertReminderPlayback(StudyAudio(surface));
                Assert(StudyAudio(surface).Requests == 1, "discipline reference sound request count incorrect");
                Assert(((SolidColorBrush)stage.Background).Color.R == 158 && !title.Text.Contains("摄像"), "discipline color/text incorrect");
                Snapshot(window, "study-discipline.png");
                for (int i = 1; i <= 5; i++) { clock.Ms = i * 1000; window.Apply(state with { Threshold = new(noise.SessionId!.Value, "None") }, noise with { CurrentDbfs = -60 }); }
                Assert(stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 1, "discipline lasted over five seconds or repeated");
                window.Apply(state, noise); Assert(stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 1, "same offer replayed");
                PumpUntil(() => server.IsCompleted, "discipline server did not settle"); server.GetAwaiter().GetResult();
                Checks.Add("Study actual WPF/Named Pipe: event/session-bound permission, deep-red discipline 5s, existing alert asset muted, no camera text or microphone command, no replay");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); timeout.Cancel(); }
    }
    private static void RunStudyDisciplineDelayedChecks()
    {
        var state = StudyFixture(); var noise = ThresholdNoise(state); var clock = new StudyUiClock(); var permission = new TaskCompletionSource<bool>();
        state = state with { Threshold = new(noise.SessionId!.Value, "High", Guid.NewGuid()) };
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => permission.Task);
        try
        {
            surface.Apply(state, noise); Assert(StudyAudio(surface).Requests == 0, "discipline played before Host reply");
            surface.Apply(state with { Phase = "Ended", SchoolNow = state.Window!.End, Threshold = null }, noise);
            surface.Suspend(); permission.SetResult(true);
            bool drained = false; Application.Current.Dispatcher.BeginInvoke(() => drained = true); PumpUntil(() => drained, "discipline continuation did not drain");
            Assert(StudyAudio(surface).Requests == 0 && ((Border)surface.FindName("StageNotice")).Visibility == Visibility.Collapsed, "late discipline reply replayed after end/hide");
            Checks.Add("Study discipline permission: delayed reply cannot interrupt end or hidden display");
        }
        finally { surface.Stop(); }
        surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (_, _) => Task.FromResult(true));
        try
        {
            surface.Apply(state, noise); Assert(((TextBlock)surface.FindName("NoticeTitle")).Text == "注意纪律", "fresh authorized discipline missing");
            surface.Apply(state, noise with { Quality = "NoData" });
            Assert(((Border)surface.FindName("StageNotice")).Visibility == Visibility.Collapsed && ((Grid)surface.FindName("NoiseEdge")).Visibility == Visibility.Collapsed,
                "stale actual microphone retained discipline despite cached execution receipt");
        }
        finally { surface.Stop(); }
    }
}
