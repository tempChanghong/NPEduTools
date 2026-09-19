using NPEduTools.ClassIsland.Bridge.Contracts;

namespace NPEduTools.Tests;

public sealed class BridgeClockTests
{
    [Fact]
    public void ArbitrarySchoolOffsetWarmsUpWithoutComparingSystemClock()
    {
        var clock = new ClockObservation(); var school = new DateTime(2030, 1, 2, 23, 59, 59);
        clock.Observe(school, 0); clock.Observe(school.AddMilliseconds(250), 250); clock.Observe(school.AddMilliseconds(500), 500);
        Assert.Equal("Advancing", clock.State); Assert.Equal(0, clock.Epoch);
        clock.Observe(school.AddSeconds(2), 2000); Assert.Equal("Advancing", clock.State);
    }
    [Theory]
    [InlineData(120)] [InlineData(-120)]
    public void CorrectionCreatesNewEpochAndRequiresStableSamples(int jump)
    {
        var clock = new ClockObservation(); var now = new DateTime(2026, 9, 19, 9, 58, 0);
        clock.Observe(now, 0); clock.Observe(now.AddSeconds(jump), 500);
        Assert.Equal("Discontinuous", clock.State); Assert.Equal(1, clock.Epoch);
        clock.Observe(now.AddSeconds(jump + 0.25), 750);
        clock.Observe(now.AddSeconds(jump + 0.5), 1000);
        clock.Observe(now.AddSeconds(jump + 0.75), 1250);
        Assert.Equal("Advancing", clock.State);
    }
    [Fact]
    public void ConnectedButFrozenClockIsNotHealthy()
    {
        var clock = new ClockObservation(); var now = new DateTime(2026, 9, 19);
        clock.Observe(now, 0); clock.Observe(now.AddSeconds(1), 1000);
        clock.Observe(now.AddSeconds(1), 3100);
        Assert.Equal("FrozenSuspected", clock.State);
    }
    [Fact]
    public void Utf8SizeIsBounded()
    {
        Assert.Throws<InvalidDataException>(() => BridgeProtocol.Encode(new { text = new string('中', 30000) }));
    }
}
