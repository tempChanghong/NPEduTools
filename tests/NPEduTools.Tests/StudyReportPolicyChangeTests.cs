using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyReportPolicyChangeTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private static readonly SchoolNoiseWindow Window = new(new(2026, 10, 22, 19, 15, 0, TimeSpan.Zero), new(2026, 10, 22, 21, 30, 0, TimeSpan.Zero));
    private static readonly StudySettings Settings = new(new("Asia/Shanghai", "2026-10-22", null,
        [new([4], "19:15", "21:30")], []), new("StatusTrend", 180), new(true, "OwnClass", [], 60),
        new(true, -30, -40, 600, 3), new(false, 3, 30, 30, true, false));
    private static StudyHostState State(Guid id, int elapsed) => new("version-a", "APPLIED", "ACTIVE", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
        Window.Start.AddSeconds(elapsed), Window, null, Settings, new("Schedule", "Active", id, "WINDOW_ACTIVE", true, null, null, true));
    private static StudySessionReport Statistics(Guid id, int seconds) => new(id, true, seconds, 0, seconds * 10, 1,
        new("dBFS", seconds, -30, -20, seconds, 10, 5, [], seconds), null, null);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Same_window_version_changes_keep_all_collected_seconds_without_double_counting(bool sameSession)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var nextId = sameSession ? id : Guid.NewGuid();
        var state = State(id, 600); var statistics = Statistics(id, 600) with { Complete = !sameSession };
        await using var reports = new StudyReportService(() => ("school/class", true, state), () => statistics, clock, false);
        var initial = reports.Snapshot(); clock.Ms = 1000;
        state = State(nextId, 900) with { Version = "version-b", Settings = Settings with { Homework = Settings.Homework with { RotationSeconds = 90 } } };
        statistics = Statistics(nextId, sameSession ? 900 : 300); reports.Snapshot();
        state = state with { Phase = "Outside", SchoolNow = Window.End, Window = null, Execution = null };
        reports.Snapshot(); clock.Ms += 5000;
        var final = reports.Snapshot();
        Assert.True(final.Available); Assert.Equal(initial.InstanceId, final.InstanceId);
        Assert.Equal("version-b", final.Version); Assert.Equal(900, final.Data!.SampledSeconds);
        Assert.Equal(900d / 8100, final.Data.Coverage, 8); Assert.Equal(9000, final.Data.Frames);
    }

    [Fact]
    public async Task Compatible_update_after_end_preserves_original_report_deadline()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = State(id, 600);
        await using var reports = new StudyReportService(() => ("school/class", true, state), () => Statistics(id, 600), clock, false);
        reports.Snapshot(); state = state with { Phase = "Outside", SchoolNow = Window.End, Window = null, Execution = null };
        reports.Snapshot(); clock.Ms = 10000;
        var before = reports.Snapshot(); state = state with { Version = "version-b" };
        var updated = reports.Snapshot();
        Assert.True(updated.Available); Assert.Equal(before.InstanceId, updated.InstanceId);
        Assert.Equal(before.RemainingSeconds, updated.RemainingSeconds); Assert.Equal(600, updated.Data!.SampledSeconds);
        clock.Ms = 305000; Assert.Equal("Expired", reports.Snapshot().State);
        state = state with { Version = "version-c" }; Assert.Equal("Expired", reports.Snapshot().State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Compatible_update_during_clock_loss_keeps_old_fragments_and_original_deadline(bool afterEnd)
    {
        var clock = new Clock(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var state = State(first, 600); var statistics = Statistics(first, 600);
        await using var reports = new StudyReportService(() => ("school/class", true, state), () => statistics, clock, false);
        var initial = reports.Snapshot(); clock.Ms = 1000;
        state = State(second, 900); statistics = Statistics(second, 300); reports.Snapshot();
        if (afterEnd)
        {
            state = state with { Phase = "Outside", SchoolNow = Window.End, Window = null, Execution = null };
            reports.Snapshot();
        }
        clock.Ms += 10000;
        state = state with { Version = "version-b", Phase = "Blocked", ClockReady = false, SchoolNow = null,
            Reason = "SCHOOL_CLOCK_UNAVAILABLE", Settings = Settings with { Homework = Settings.Homework with { RotationSeconds = 90 } } };
        var blocked = reports.Snapshot();
        Assert.False(blocked.Available); Assert.Null(blocked.Data); Assert.Equal(initial.InstanceId, blocked.InstanceId);
        clock.Ms += 10000;
        state = state with { Phase = "Outside", Reason = "OUTSIDE_WINDOW", ClockReady = true,
            SchoolNow = afterEnd ? Window.End.AddSeconds(20) : Window.End, Window = null, Execution = null };
        reports.Snapshot(); clock.Ms += 5000;
        var final = reports.Snapshot();
        Assert.True(final.Available); Assert.Equal(initial.InstanceId, final.InstanceId);
        Assert.Equal(900, final.Data!.SampledSeconds); Assert.Equal("version-b", final.Version);
        Assert.Equal(afterEnd ? 280 : 300, final.RemainingSeconds);
    }

    [Fact]
    public async Task Actual_schedule_change_during_clock_loss_does_not_keep_old_report()
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = State(id, 600);
        await using var reports = new StudyReportService(() => ("school/class", true, state), () => Statistics(id, 600), clock, false);
        var initial = reports.Snapshot();
        state = state with { Version = "version-b", Phase = "Blocked", ClockReady = false, SchoolNow = null,
            Reason = "SCHOOL_CLOCK_UNAVAILABLE", Settings = Settings with { Noise = Settings.Noise with { Enabled = false } } };
        var blocked = reports.Snapshot();
        Assert.False(blocked.Available); Assert.NotEqual(initial.InstanceId, blocked.InstanceId);
        state = state with { Phase = "Outside", ClockReady = true, SchoolNow = Window.End, Window = null, Execution = null };
        Assert.Null(reports.Snapshot().Data);
    }
}
