using NPEduTools.Contracts;
using NPEduTools.Host;
using Xunit;

namespace NPEduTools.Tests;

public sealed class WebsiteSchedulePhaseTests
{
    private static readonly DateTimeOffset At = new(2031, 4, 7, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("OnClass", "LESSON")]
    [InlineData("Breaking", "BREAK")]
    [InlineData("AfterSchool", "AFTER_SCHOOL")]
    [InlineData("None", "NO_ACTIVE_PERIOD")]
    [InlineData("PrepareOnClass", "NO_ACTIVE_PERIOD")]
    public void UsesTheNativeCurrentPhaseEvenWithoutASubject(string native, string phase) =>
        Assert.Equal(phase, WebsiteSchedulePhase.Read(Sample(native), 100, At.AddMilliseconds(600)).Phase);

    [Fact]
    public void TreatsOldExpiredOrDisabledPlanAsUnavailable()
    {
        var sample = Sample("OnClass");
        var now = At.AddMilliseconds(600);
        Assert.Equal("LESSON", WebsiteSchedulePhase.Read(sample, 100, now).Phase);
        Assert.Equal(default, WebsiteSchedulePhase.Read(sample, 2500, now));
        Assert.Equal(default, WebsiteSchedulePhase.Read(Sample("Breaking"), 1, At.AddSeconds(5)));
        Assert.Equal(default, WebsiteSchedulePhase.Read(sample with {Outcome = "Unavailable"}, 100, now));
        Assert.Equal(new(null, true), WebsiteSchedulePhase.Read(sample with {Status = sample.Status! with {IsTimerRunning = false}}, 100, now));
        Assert.Equal(new(null, true), WebsiteSchedulePhase.Read(sample with {Status = sample.Status! with {IsClassPlanEnabled = false}}, 100, now));
        Assert.Equal(new(null, true), WebsiteSchedulePhase.Read(sample with {Status = sample.Status! with {IsClassPlanLoaded = false}}, 100, now));
    }

    [Fact]
    public void ADisconnectKeepsTheLastVerifiedPhaseOnlyUntilTheFixedDeadline()
    {
        var phase = new WebsitePhaseContinuity();
        Assert.Equal(("UNAVAILABLE", false), phase.Resolve(default, 500));
        Assert.Equal(("LESSON", false), phase.Resolve(new("LESSON"), 1_000));
        Assert.Equal(("LESSON", true), phase.Resolve(default, 1_800));
        Assert.Equal(("LESSON", true), phase.Resolve(default, 10_400));
        Assert.Equal(("UNAVAILABLE", false), phase.Resolve(default, 13_800));
        Assert.Equal(("UNAVAILABLE", false), phase.Resolve(default, 14_000));
        Assert.Equal(("LESSON", false), phase.Resolve(new("LESSON"), 14_100));
        Assert.Equal(("LESSON", true), phase.Resolve(default, 14_200));
        Assert.Equal(("BREAK", false), phase.Resolve(new("BREAK"), 14_300));
        Assert.Equal(("UNAVAILABLE", false), phase.Resolve(new(null, true), 14_400));
        Assert.Equal(("UNAVAILABLE", false), phase.Resolve(default, 14_500));
    }

    private static WatchSnapshot Sample(string native) => new(1, Guid.Empty, Guid.NewGuid(), 1, Guid.NewGuid(), At,
        "Succeeded", null, "", new(At, At.AddMilliseconds(500), native, null, true, true, true, 0, new Dictionary<string, long>()));
}
