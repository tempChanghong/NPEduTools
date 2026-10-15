using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData("AUTH_INVALID", false, false)] [InlineData("AUTH_INVALID", true, false)]
    [InlineData("DEVICE_SUSPENDED", false, false)] [InlineData("DEVICE_SUSPENDED", true, false)]
    [InlineData("SESSION_SUPERSEDED", false, false)] [InlineData("SESSION_SUPERSEDED", true, false)]
    [InlineData("AUTH_INVALID", false, true)] [InlineData("AUTH_INVALID", true, true)]
    [InlineData("DEVICE_SUSPENDED", false, true)] [InlineData("DEVICE_SUSPENDED", true, true)]
    [InlineData("SESSION_SUPERSEDED", false, true)] [InlineData("SESSION_SUPERSEDED", true, true)]
    [InlineData("STUDY_OFFLINE", false, false)] [InlineData("STUDY_OFFLINE", true, false)]
    [InlineData("TEMPORARILY_UNAVAILABLE", false, false)] [InlineData("TEMPORARILY_UNAVAILABLE", true, false)]
    [InlineData("RATE_LIMITED", false, false)] [InlineData("RATE_LIMITED", true, false)]
    [InlineData("STUDY_OFFLINE", false, true)] [InlineData("STUDY_OFFLINE", true, true)]
    [InlineData("TEMPORARILY_UNAVAILABLE", false, true)] [InlineData("TEMPORARILY_UNAVAILABLE", true, true)]
    [InlineData("RATE_LIMITED", false, true)] [InlineData("RATE_LIMITED", true, true)]
    public async Task Study_failure_at_the_start_boundary_cannot_start_capture_or_preempt_manual_study(string code, bool exam, bool manual)
    {
        await using var f = new Fixture(); f.Base = new(2026, 10, 1, 18, 59, 58, TimeSpan.Zero); f.ModeExam = exam;
        var config = StudyConfig(f);
        config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
        await f.Scheduler.DisposeAsync();
        int scheduleStarts = 0;
        f.Scheduler = f.NewScheduler((_, _) => scheduleStarts++);
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler,
            () => exam ? "Exam" : "Daily");
        study.BindStudy(f.Scope); study.ApplyStudy(config);
        var grant = Grant(config); if (exam) grant["mode"] = "Exam";
        study.ConfirmStudyExecution(config, grant);
        for (int i = 0; i < 3; i++) { f.Tick(); study.ApplyStudy(config); }
        Assert.Equal("Preparing", study.Snapshot().Phase); Assert.Equal(0, scheduleStarts); Assert.Empty(f.Captures);
        Guid? manualSession = null;
        if (manual)
        {
            Assert.Equal("Accepted", f.Noise.StartManualStudy(Guid.NewGuid(), "synthetic", 10800).Outcome);
            await Until(() => f.Noise.Snapshot().State == "Active");
            manualSession = f.Noise.Snapshot().SessionId;
        }

        // The confirmed calendar is now due, but a failure is the first new event.
        // No ordinary scheduler poll has run since the boundary.
        f.Time.Ms += 500;
        study.StudyFailed(code);
        Assert.Equal(0, scheduleStarts);
        if (manual)
        {
            Assert.Equal("Active", f.Noise.Snapshot().State);
            Assert.Equal(manualSession, f.Noise.Snapshot().SessionId);
        }
        else Assert.Null(f.Noise.Snapshot().SessionId);
        f.Tick();
        var state = study.Snapshot();
        Assert.Equal("UNAVAILABLE", state.PolicyState); Assert.Equal(code, state.Reason);
        Assert.Equal(0, scheduleStarts); Assert.Equal(manual ? 1 : 0, f.Captures.Count);
        if (manual) Assert.Equal("Active", f.Noise.Snapshot().State);
    }

    [Theory]
    [InlineData("STUDY_OFFLINE", false)] [InlineData("STUDY_OFFLINE", true)]
    [InlineData("TEMPORARILY_UNAVAILABLE", false)] [InlineData("TEMPORARILY_UNAVAILABLE", true)]
    [InlineData("RATE_LIMITED", false)] [InlineData("RATE_LIMITED", true)]
    public async Task Transport_failure_can_remember_an_already_started_capture_before_the_first_settings_poll(string code, bool exam)
    {
        await using var f = new Fixture(); f.Base = new(2026, 10, 1, 18, 59, 58, TimeSpan.Zero); f.ModeExam = exam;
        var config = StudyConfig(f);
        config["effective"]!["settings"]!["examSchedule"] = config["effective"]!["settings"]!["schedule"]!.DeepClone();
        long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler,
            () => exam ? "Exam" : "Daily");
        study.BindStudy(f.Scope); study.ApplyStudy(config);
        var grant = Grant(config); if (exam) grant["mode"] = "Exam";
        study.ConfirmStudyExecution(config, grant);
        for (int i = 0; i < 3; i++) { f.Tick(); study.ApplyStudy(config); }
        f.Tick(); // The scheduler starts first; StudySettings has not yet observed execution.
        await Until(() => f.Noise.Snapshot().State == "Active");
        var session = f.Noise.Snapshot().SessionId;
        Assert.Equal("Preparing", study.CachedCalibrationContext.State.Phase);
        study.StudyFailed(code);
        for (int i = 0; i < 40; i++) { f.Captures[0].Sample(); f.Tick(); study.Tick(); }
        var state = study.Snapshot();
        Assert.Equal("APPLIED", state.PolicyState); Assert.Equal("OFFLINE_CONTINUING", state.Reason);
        Assert.Equal("Active", state.Phase); Assert.Equal("Active", f.Noise.Snapshot().State);
        Assert.Equal(session, state.Execution!.SessionId); Assert.Single(f.Captures);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Read_only_execution_never_claims_a_stale_clock_or_an_unrelated_capture_as_fresh(bool replaceSession)
    {
        await using var f = new Fixture(); var config = StudyConfig(f); Select(f, config); await f.Start();
        f.Captures[0].Sample(); f.Tick();
        Assert.True(f.Scheduler.PeekStudyExecution()!["signalFresh"]!.GetValue<bool>());
        if (replaceSession)
        {
            f.Noise.StopScheduled(f.Noise.Snapshot().SessionId!.Value);
            await Until(() => !f.Noise.CaptureBusy);
            Assert.Equal("Accepted", f.Noise.Handle(f.Command("start", "synthetic")).Outcome);
            await Until(() => f.Noise.Snapshot().State == "Active");
        }
        else f.Time.Ms += 10000; // No clock refresh, but a fresh audio sample is still possible.
        f.Captures[^1].Sample();
        var state = f.Scheduler.PeekStudyExecution()!;
        Assert.False(state["signalFresh"]!.GetValue<bool>());
        Assert.Equal("Active", f.Noise.Snapshot().State); // A read must not stop capture either.
        if (replaceSession) { Assert.Equal("None", state["owner"]!.GetValue<string>()); Assert.Null(state["sessionId"]); }
        else Assert.False(state["clockReady"]!.GetValue<bool>());
    }
}
