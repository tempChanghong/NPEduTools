using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Fact]
    public async Task Guardian_projects_school_wall_fields_to_utc_and_does_not_start_capture()
    {
        await using var f = new Fixture(); f.Base = f.Base.AddMinutes(-5);
        f.Time.UtcBase = new(2026, 10, 1, 10, 55, 0, TimeSpan.Zero);
        var configuration = StudyConfig(f); Select(f, configuration);
        f.Tick(); f.Tick(); f.Tick();
        var plan = f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200));
        Assert.True(plan.Valid()); Assert.Equal(16, plan.Windows.Count); Assert.Empty(f.Captures);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero), plan.Windows[0].StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), plan.Windows[0].EndUtc);
        Assert.NotNull(plan.Due(f.Time.GetUtcNow()));
        Assert.Null(plan.Due(plan.Windows[0].StartUtc.AddMinutes(-5).AddTicks(-1)));
        Assert.NotNull(plan.Due(plan.Windows[0].StartUtc.AddMinutes(-5)));
    }
    [Theory]
    [InlineData("no-confirmation")] [InlineData("disabled")] [InlineData("revoked")] [InlineData("clock")] [InlineData("exam")] [InlineData("expired")]
    public async Task Guardian_cannot_cache_wake_hints_without_confirmed_eligibility_and_fresh_school_clock(string change)
    {
        await using var f = new Fixture(); var configuration = StudyConfig(f);
        if (change == "disabled") { configuration["effective"]!["source"] = "Disabled"; configuration["effective"]!["settings"] = null; }
        Select(f, configuration, change != "no-confirmation"); f.Tick(); f.Tick(); f.Tick();
        if (change == "revoked") f.Scheduler.Bind(null, false);
        if (change == "exam") f.Exam = true;
        if (change == "expired") f.Scheduler.InvalidateStudy();
        if (change == "clock") { f.Frozen = true; for (int i = 0; i < 9; i++) f.Tick(); }
        var plan = f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200));
        if (change == "exam") { Assert.Null(plan.Due(plan.IssuedUtc)); Assert.NotNull(plan.CurrentEndUtc); Assert.NotEmpty(plan.Windows); }
        else Assert.Empty(plan.Windows);
    }
    [Fact]
    public async Task Guardian_uses_school_calendar_exception_and_date_bounds_instead_of_a_fixed_school_start_date()
    {
        await using var f = new Fixture(); var configuration = StudyConfig(f);
        configuration["effective"]!["settings"]!["schedule"]!["exceptions"] = System.Text.Json.Nodes.JsonNode.Parse("[{\"date\":\"2026-10-01\",\"kind\":\"Skip\",\"weekday\":null}]");
        Select(f, configuration); f.Tick(); f.Tick(); f.Tick();
        var plan = f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200));
        Assert.NotEmpty(plan.Windows); Assert.Null(plan.CurrentEndUtc); Assert.Null(plan.Due(plan.IssuedUtc));
        Assert.True(plan.Windows[0].StartUtc > plan.IssuedUtc.AddDays(6));
        configuration["effective"]!["settings"]!["schedule"]!["effectiveTo"] = "2026-10-01";
        Select(f, configuration); Assert.Empty(f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200)).Windows);
    }
    [Fact]
    public async Task Skipped_current_window_is_excluded_from_wake_cache_and_survives_host_restart()
    {
        await using var f = new Fixture(); var configuration = StudyConfig(f); Select(f, configuration); await f.Start();
        Assert.NotNull(f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200)).CurrentEndUtc);
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(() => f.Noise.Snapshot().State == "Stopped"); f.Tick();
        var before = f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200));
        Assert.NotNull(before.CurrentEndUtc); Assert.Null(before.Due(before.IssuedUtc)); Assert.NotEmpty(before.Windows);
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(); Select(f, configuration); f.Tick(); f.Tick(); f.Tick();
        var after = f.Scheduler.GuardPlan(Guid.NewGuid(), new(20, 200)); Assert.Null(after.Due(after.IssuedUtc));
        Assert.True(after.Windows[0].StartUtc > after.IssuedUtc.AddDays(6)); Assert.Single(f.Captures);
    }
}
