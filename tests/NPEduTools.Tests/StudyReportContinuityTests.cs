using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class StudyReportContinuityTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ms;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ms;
    }
    private static readonly SchoolNoiseWindow Window = new(new(2026,10,14,19,15,0,TimeSpan.Zero), new(2026,10,14,21,30,0,TimeSpan.Zero));
    private static StudyHostState Active(Guid id, double seconds) => new("version-a", "APPLIED", "ACTIVE", "ACTIVE", "Active", "WINDOW_ACTIVE", true,
        Window.Start.AddSeconds(seconds), Window, null, null, new("Schedule", "Active", id, "WINDOW_ACTIVE", true, null, null, true));
    private static StudySessionReport Statistics(Guid id, double elapsed, double sampled) => new(id, true, elapsed, 0, (long)(sampled * 10), 1,
        new("dBFS", sampled, -30, -20, sampled, 0, 0, [new(0, sampled, -30, -20)], sampled), null, null);

    [Theory]
    [InlineData("STUDY_OFFLINE", 23, false)] [InlineData("STUDY_OFFLINE", 8, false)]
    [InlineData("CONFIGURATION_STALE", 23, false)] [InlineData("TEMPORARILY_UNAVAILABLE", 8, false)]
    [InlineData("RATE_LIMITED", 23, false)] [InlineData("STUDY_OFFLINE", 23, true)]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 23, false)] [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 8, false)]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 23, true)] [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 8, true)]
    public async Task Temporary_configuration_or_clock_loss_keeps_known_sessions_without_claiming_live_state_or_filling_gaps(string reason, int lastMinutes, bool realGap)
    {
        var time = new Clock(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        double firstSeconds = (135 - lastMinutes) * 60;
        var prior = Statistics(first, firstSeconds, realGap ? firstSeconds / 2 : firstSeconds);
        StudySessionReport current = prior;
        var state = Active(first, firstSeconds);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => current, time, false);
        var initial = service.Snapshot();
        state = reason == "SCHOOL_CLOCK_UNAVAILABLE"
            ? state with { Phase = "Blocked", ClockReady = false, Reason = reason, Window = null, Execution = null }
            : new(null, "UNAVAILABLE", "NOT_INTEGRATED", "NOT_INTEGRATED", "Blocked", reason, false, null, null, null, null);
        var unavailable = service.Snapshot();
        Assert.False(unavailable.Available); Assert.Null(unavailable.Data);
        current = Statistics(second, lastMinutes * 60, lastMinutes * 60);
        time.Ms = 1000; state = Active(second, 135 * 60 - 1);
        var resumed = service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); time.Ms += 5000;
        var final = service.Snapshot();
        Assert.True(final.Available);
        Assert.Equal(135 * 60, final.Data!.ExpectedSeconds);
        Assert.Equal(prior.Raw.SampledSeconds + current.Raw.SampledSeconds, final.Data.SampledSeconds);
        Assert.Equal(final.Data.SampledSeconds / (135 * 60), final.Data.Coverage, 8);
        Assert.Equal(2, final.Data.DisciplineReminders);
        Assert.Equal(prior.Frames + current.Frames, final.Data.Frames);
        Assert.Equal(initial.InstanceId, resumed.InstanceId);
    }
    [Theory]
    [InlineData("scope", false)] [InlineData("version", false)] [InlineData("authorization", false)] [InlineData("unselected", false)]
    [InlineData("scope", true)] [InlineData("version", true)] [InlineData("authorization", true)] [InlineData("unselected", true)]
    [InlineData("mode", false)] [InlineData("mode", true)] [InlineData("window", false)] [InlineData("window", true)]
    public async Task Changed_authority_never_reuses_the_pre_outage_collection(string change, bool clockLoss)
    {
        var time = new Clock(); var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var state = Active(first, 600); var current = Statistics(first, 600, 600);
        string scope = "class-a"; bool selected = true;
        await using var service = new StudyReportService(() => (scope, selected, state), () => current, time, false);
        service.Snapshot();
        state = clockLoss
            ? state with { Phase = "Blocked", ClockReady = false, Reason = "SCHOOL_CLOCK_UNAVAILABLE", Window = null, Execution = null }
            : new(null, "UNAVAILABLE", "NOT_INTEGRATED", "NOT_INTEGRATED", "Blocked", "STUDY_OFFLINE", false, null, null, null, null);
        if (change == "scope") scope = "class-b";
        if (change == "version") state = state with { Version = "version-b" };
        if (change == "authorization") state = state with { Reason = "AUTH_INVALID" };
        if (change == "unselected") selected = false;
        if (change == "mode") state = state with { Mode = "Exam", ClockSource = "Windows" };
        Assert.False(service.Snapshot().Available);
        selected = true; current = Statistics(second, 60, 60); time.Ms = 1000;
        state = Active(second, 660) with { Version = change == "version" ? "version-b" : "version-a" };
        if (change == "mode") state = state with { Mode = "Exam", ClockSource = "Windows" };
        if (change == "window") state = state with { Window = new(Window.Start.AddDays(1), Window.End.AddDays(1)), SchoolNow = state.SchoolNow!.Value.AddDays(1) };
        service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = state.Window!.End, Execution = null };
        service.Snapshot(); time.Ms += 5000;
        Assert.Equal(60, service.Snapshot().Data!.SampledSeconds);
    }

    [Theory]
    [InlineData(120, true)] [InlineData(360, true)]
    [InlineData(120, false)] [InlineData(360, false)]
    public async Task Clock_recovery_after_the_end_keeps_only_a_witnessed_report_within_its_original_deadline(int delaySeconds, bool witnessed)
    {
        var time = new Clock(); var id = Guid.NewGuid();
        var state = Active(id, 8090); var current = Statistics(id, 8090, 8090);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => current, time, false);
        if (witnessed) service.Snapshot();
        state = state with { Phase = "Blocked", ClockReady = false, Reason = "SCHOOL_CLOCK_UNAVAILABLE", Window = null, Execution = null };
        Assert.False(service.Snapshot().Available); Assert.Null(service.Snapshot().Data);
        time.Ms = (10 + delaySeconds) * 1000;
        Assert.False(service.Snapshot().Available); Assert.Null(service.Snapshot().Data);
        current = current with { Complete = true };
        state = state with { Phase = "Outside", ClockReady = true, SchoolNow = Window.End.AddSeconds(delaySeconds), Reason = "OUTSIDE_WINDOW" };
        var recovered = service.Snapshot();
        Assert.Equal(witnessed && delaySeconds < 305, recovered.Available);
        if (recovered.Available)
        {
            Assert.Equal(305 - delaySeconds, recovered.RemainingSeconds);
            Assert.Equal(current.Raw.SampledSeconds, recovered.Data!.SampledSeconds);
        }
        else Assert.Null(recovered.Data);
    }
}
