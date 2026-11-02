using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class StudyReportTests
{
    [Theory]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 1, 20)]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 30, 20)]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 304, 20)]
    [InlineData("SCHOOL_CLOCK_UNAVAILABLE", 30, 360)]
    [InlineData("STUDY_OFFLINE", 1, 20)]
    [InlineData("STUDY_OFFLINE", 30, 20)]
    [InlineData("STUDY_OFFLINE", 304, 20)]
    [InlineData("STUDY_OFFLINE", 30, 360)]
    [InlineData("CONFIGURATION_STALE", 30, 20)]
    [InlineData("TEMPORARILY_UNAVAILABLE", 30, 20)]
    [InlineData("RATE_LIMITED", 30, 20)]
    public async Task Temporary_loss_after_end_observation_preserves_the_report_without_renewing_its_deadline(
        string reason, int secondsBeforeLoss, int outageSeconds)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id); var statistics = Report(id);
        await using var service = new StudyReportService(() => ("class-a", true, state), () => statistics, clock, false);
        var initial = service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms = secondsBeforeLoss * 1000;
        Assert.NotNull(service.Snapshot().Data);
        var ended = state;
        state = reason == "SCHOOL_CLOCK_UNAVAILABLE"
            ? state with { Phase = "Blocked", ClockReady = false, Reason = reason, SchoolNow = null }
            : new(null, "UNAVAILABLE", "NOT_INTEGRATED", "NOT_INTEGRATED", "Blocked", reason, false, null, null, null, null);
        var unavailable = service.Snapshot();
        Assert.False(unavailable.Available); Assert.Null(unavailable.Data);
        // A late terminal tail may still settle while presentation is quarantined.
        statistics = statistics with { Complete = true, Frames = 5 };
        clock.Ms += outageSeconds * 1000;
        Assert.False(service.Snapshot().Available);
        // Even a school clock which resumes from the old end timestamp cannot renew the deadline.
        state = ended;
        var recovered = service.Snapshot();
        bool expired = secondsBeforeLoss + outageSeconds >= 305;
        Assert.Equal(!expired, recovered.Available);
        if (expired) { Assert.Equal("Expired", recovered.State); Assert.Null(recovered.Data); }
        else
        {
            Assert.Equal(initial.InstanceId, recovered.InstanceId);
            Assert.Equal(305 - secondsBeforeLoss - outageSeconds, recovered.RemainingSeconds);
            Assert.True(recovered.Data!.Complete); Assert.Equal(5, recovered.Data.Frames);
            Assert.Equal(statistics.Raw.SampledSeconds, recovered.Data.SampledSeconds);
            Assert.Equal(statistics.Estimated!.SampledSeconds, recovered.Data.Estimated!.SampledSeconds);
            clock.Ms = 305000;
            Assert.Equal("Expired", service.Snapshot().State);
        }
        clock.Ms += 1000; Assert.False(service.Snapshot().Available);
    }

    [Theory]
    [InlineData("scope")] [InlineData("version")] [InlineData("mode")]
    [InlineData("unselected")] [InlineData("authorization")]
    public async Task Ended_report_quarantine_still_discards_withdrawn_authority(string change)
    {
        var clock = new Clock(); var id = Guid.NewGuid(); var state = Active(id);
        string scope = "class-a"; bool selected = true;
        await using var service = new StudyReportService(() => (scope, selected, state), () => Report(id, true), clock, false);
        service.Snapshot();
        state = state with { Phase = "Outside", Window = null, SchoolNow = Window.End, Execution = null };
        service.Snapshot(); clock.Ms = 30000; Assert.True(service.Snapshot().Available);
        var ended = state;
        state = state with { Phase = "Blocked", ClockReady = false, Reason = "SCHOOL_CLOCK_UNAVAILABLE", SchoolNow = null };
        Assert.False(service.Snapshot().Available);
        if (change == "scope") scope = "class-b";
        if (change == "version") state = state with { Version = "version-b" };
        if (change == "mode") state = state with { Mode = "Exam" };
        if (change == "unselected") selected = false;
        if (change == "authorization") state = state with { PolicyState = "UNAVAILABLE", Reason = "AUTH_INVALID" };
        Assert.False(service.Snapshot().Available); Assert.Null(service.Snapshot().Data);
        scope = "class-a"; selected = true; state = ended; clock.Ms = 50000;
        Assert.False(service.Snapshot().Available); Assert.Null(service.Snapshot().Data);
    }
}
