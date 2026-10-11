namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Fact]
    public async Task Mode_change_stops_old_capture_requires_matching_grant_and_keeps_daily_skip()
    {
        await using var f = new Fixture(); var c = StudyConfig(f);
        c["effective"]!["settings"]!["examSchedule"] = c["effective"]!["settings"]!["schedule"]!.DeepClone();
        Select(f, c); await f.Start();
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        f.Tick(); Assert.Equal("WINDOW_SKIPPED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        f.ModeExam = true; f.Tick(); f.Tick(); f.Tick();
        Assert.Equal("POLICY_EXPIRED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Throws<NPEduTools.Integrations.Npep.NpepException>(() => f.Scheduler.ConfirmStudyExecution(c, Grant(c)));
        var grant = Grant(c); grant["mode"] = "Exam"; f.Scheduler.ConfirmStudyExecution(c, grant);
        await Until(() => f.Noise.Snapshot().State == "Active");
        Assert.Equal("Exam", f.Scheduler.ObserveStudyExecution()!["mode"]!.GetValue<string>());
        f.ModeExam = false; f.Tick();
        await Until(() => f.Noise.Snapshot().State is "Stopped" or "Idle");
        f.Tick(); f.Tick(); f.Tick(); f.Scheduler.ConfirmStudyExecution(c, Grant(c)); f.Tick();
        Assert.Equal("WINDOW_SKIPPED", f.Scheduler.Observe()["reason"]!.GetValue<string>());
        Assert.Equal(2, f.Captures.Count);
    }
}
