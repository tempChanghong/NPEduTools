using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.Tests;

public sealed class NoiseUsbReferenceTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
    private static SoundMeterReading Reading(double level = 60) => new(level, SoundMeterWeighting.A, SoundMeterTimeWeighting.Slow, false, false, 0);
    private static NoiseReferenceSample Sample() => new(Guid.NewGuid(), "Primary", 10, 1, -40, -40, -40, 0,
        "Good", Epoch.AddSeconds(5), Epoch.AddSeconds(15));
    [Fact]
    public void WarmupAndPostWindowAreExcludedAndStableIdenticalReadingsRemainValid()
    {
        var collector = new NoiseUsbReference("Slow");
        for (int i = 0; i <= 80; i++)
        {
            double at = i * .2;
            Assert.True(collector.Receive(Reading(at < 5 || at > 15 ? 120 : 60), at, at + .001, true));
        }
        var result = collector.Attach(Sample(), Epoch);
        Assert.Equal("Good", result.Quality); Assert.True(result.SameWindowConfirmed);
        Assert.InRange(result.ReferenceDbA!.Value, 59.9999, 60.0001);
        Assert.Equal(60, result.ReferenceMinimumDbA); Assert.Equal(60, result.ReferenceMaximumDbA);
        Assert.True(NoiseReferenceContract.MeterEvidenceValid(result.MeterEvidence));
    }
    [Theory]
    [InlineData(false, 0, .01)] [InlineData(true, 0, .251)]
    [InlineData(true, 0, -1)] [InlineData(true, double.NaN, .1)]
    public void UnflushedDelayedAndMalformedBracketsCannotBecomeReference(bool flushed, double requested, double received)
    {
        var collector = new NoiseUsbReference("Slow");
        Assert.False(collector.Receive(Reading(), requested, received, flushed));
        Assert.NotEqual("Good", collector.Attach(Sample(), Epoch).Quality);
    }
    [Theory]
    [InlineData(1.8, 1.81)] [InlineData(.009, .02)] [InlineData(.02, .01)]
    public void GapsDuplicatesAndReversedTimePoisonTheWholeWindow(double requested, double received)
    {
        var collector = new NoiseUsbReference("Slow"); Assert.True(collector.Receive(Reading(), 0, .01, true));
        Assert.False(collector.Receive(Reading(), requested, received, true));
        Assert.False(collector.Receive(Reading(), 1, 1.01, true));
    }
    [Theory]
    [InlineData("C")] [InlineData("Fast")] [InlineData("MAX")] [InlineData("Battery")] [InlineData("Range")]
    public void IneligibleOrChangedMeterSettingsInvalidateTheWindow(string change)
    {
        var collector = new NoiseUsbReference("Slow"); Assert.True(collector.Receive(Reading(), 0, .01, true));
        var bad = change switch { "C" => Reading() with { Weighting = SoundMeterWeighting.C },
            "Fast" => Reading() with { TimeWeighting = SoundMeterTimeWeighting.Fast }, "MAX" => Reading() with { MaximumHold = true },
            "Battery" => Reading() with { LowBattery = true }, _ => Reading() with { Range = 1 } };
        Assert.False(collector.Receive(bad, .2, .21, true));
    }
    [Fact]
    public void MissingBoundaryCoverageCannotBorrowWarmupOrFutureSamples()
    {
        var collector = new NoiseUsbReference("Slow");
        for (int i = 0; i <= 65; i++) collector.Receive(Reading(), i * .2, i * .2 + .001, true);
        var result = collector.Attach(Sample(), Epoch);
        Assert.Equal("Incomplete", result.Quality); Assert.Null(result.ReferenceDbA); Assert.Null(result.MeterEvidence);
    }
    [Fact]
    public void ReferenceStatisticUsesEnergyAndStillRequiresStableWindows()
    {
        var collector = new NoiseUsbReference("Slow");
        for (int i = 0; i <= 80; i++) collector.Receive(Reading(i % 2 == 0 ? 60 : 61), i * .2, i * .2 + .001, true);
        var result = collector.Attach(Sample(), Epoch);
        Assert.True(result.ReferenceDbA > 60.5); Assert.True(result.ReferenceDbA < 60.6);
        Assert.Equal(60, result.ReferenceMinimumDbA); Assert.Equal(61, result.ReferenceMaximumDbA);
    }
    [Fact]
    public void FractionalConstantLevelsWithUnevenBracketsStayInsideRecordedRange()
    {
        foreach (double level in new[] { 35.1, 50.2, 60.1, 120.7 })
        {
            var collector = new NoiseUsbReference("Slow");
            for (int i = 0; i <= 80; i++)
                collector.Receive(Reading(level), i * .199, i * .199 + .003 * (i % 7 + 1), true);
            var result = collector.Attach(Sample(), Epoch);
            Assert.Equal("Good", result.Quality);
            Assert.True(NoiseReferenceContract.ReferenceRange(result.ReferenceDbA, result.ReferenceMinimumDbA, result.ReferenceMaximumDbA));
        }
    }
    [Fact]
    public void FailedMicrophoneQualityNeverAcquiresAutomaticReference()
    {
        var collector = new NoiseUsbReference("Slow");
        for (int i = 0; i <= 80; i++) collector.Receive(Reading(), i * .2, i * .2 + .001, true);
        var result = collector.Attach(Sample() with { Quality = "Clipping" }, Epoch);
        Assert.Equal("Clipping", result.Quality); Assert.False(result.SameWindowConfirmed); Assert.Null(result.ReferenceDbA);
    }
}
