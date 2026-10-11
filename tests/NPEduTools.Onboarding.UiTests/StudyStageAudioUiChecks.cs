using System.IO.Pipes;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunStudyStageAudioRecoveryChecks()
    {
        string pipe = "NPEduTools.Test.stage-audio-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int claims = 0;
        var server = Task.Run(async () =>
        {
            foreach (string phase in new[] { "Preparing", "Preparing", "Active" })
            {
                using var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await stream.WaitForConnectionAsync(timeout.Token); var request = await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
                Assert(Protocol.Validate(request) is null && request.StudyReminder?.Phase == phase && !Protocol.NoiseInterruption(request), "invalid stage recovery claim");
                Interlocked.Increment(ref claims);
                await Protocol.WriteAsync(stream, new HostResponse(1, request.RequestId, phase == "Preparing" ? "Suppressed" : "Succeeded", null, "fixture"), timeout.Token);
            }
        });
        var clock = new StudyUiClock(); var state = StudyFixture("Preparing", -120); var window = new StudyWindow(pipe, () => { }, true, clock);
        var surface = (StudySurface)window.Content; var stage = (Border)surface.FindName("StageNotice");
        try
        {
            Exercise(window, _ =>
            {
                window.Apply(state, null); PumpUntil(() => claims == 1, "preparation claim missing");
                state = state with { Display = state.Display! with { InstanceId = Guid.NewGuid() }, SchoolNow = state.Window!.Start.AddSeconds(-115) };
                window.Apply(state, null); PumpUntil(() => claims == 2, "reconstructed Host stage claim missing");
                Assert(stage.Visibility == Visibility.Visible && ((TextBlock)surface.FindName("NoticeTitle")).Text == "准备开始" && StudyAudio(surface).Requests == 0,
                    "suppressed preparation removed persistent visual or played again");
                state = state with { Phase = "Active", SchoolNow = state.Window!.Start.AddSeconds(1) }; window.Apply(state, ThresholdNoise(state));
                PumpUntil(() => StudyAudio(surface).Requests == 1, "start did not remain independent of preparation suppression");
                Assert(((TextBlock)surface.FindName("NoticeTitle")).Text == "自习开始", "start did not replace preparation");
                clock.Ms = 4000; window.Apply(state with { SchoolNow = state.Window!.Start.AddSeconds(5) }, ThresholdNoise(state));
                Assert(stage.Visibility == Visibility.Collapsed && StudyAudio(surface).Requests == 1, "reconnection extended the absolute five-second start window");
                PumpUntil(() => server.IsCompleted, "stage recovery pipe did not settle"); server.GetAwaiter().GetResult();
                Checks.Add("Study stage audio recovery: actual WPF/Named Pipe suppression across Host epoch, preparation visual recovers only within ten-second window, independent start sound and absolute 5s window; muted");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); timeout.Cancel(); }

        state = StudyFixture(); var noise = ThresholdNoise(state);
        state = state with { Display = state.Display! with { StageAudioError = "STUDY_STAGE_STORE_UNAVAILABLE" }, Threshold = new(noise.SessionId!.Value, "High") };
        surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, (cue, _) => Task.FromResult(cue == "Discipline"));
        try
        {
            surface.Apply(state, noise);
            Assert(((TextBlock)surface.FindName("SessionNote")).Text == "阶段提示音暂不可用" &&
                ((TextBlock)surface.FindName("NoiseStatus")).Text == "正在监测" && ((Grid)surface.FindName("NoiseEdge")).Visibility == Visibility.Visible,
                "stage journal fault changed capture or high-band presentation");
            surface.Apply(state with { Threshold = state.Threshold! with { DisciplineId = Guid.NewGuid() } }, noise);
            Assert(((TextBlock)surface.FindName("NoticeTitle")).Text == "注意纪律" && StudyAudio(surface).Requests == 1,
                "stage journal fault disabled independent discipline authorization");
            Checks.Add("Study stage audio fault: dedicated message preserves sampling/high edge and independent discipline audio; synthetic state, muted");
        }
        finally { surface.Stop(); }
    }
}
