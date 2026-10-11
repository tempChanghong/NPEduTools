using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Fact]
    public async Task Study_report_estimate_matches_reference_monitoring_without_mixing_threshold_units()
    {
        await using var f = new Fixture(); await f.PrepareAsync(); f.Command("confirm"); await StartFormal(f);
        var id = f.Noise.Snapshot().SessionId!.Value;
        f.Noise.StudyThresholds(id, new StudyNoise(true, -40, -50, 600, 3, new(55, 45)));
        FormalFrames(f, 60); await StopFormal(f);
        var state = f.Noise.Snapshot(); var report = f.Noise.StudyReportSnapshot()!;
        Assert.Equal(state.Summary!.EnergyMeanDbfs!.Value, report.Raw.EnergyMean!.Value, 8);
        Assert.Equal(state.Estimate!.Summary!.EnergyMeanDbA!.Value, report.Estimated!.EnergyMean!.Value, 8);
        Assert.Equal(state.Estimate.Summary.MaximumDbA, report.Estimated.Maximum);
        Assert.Equal(state.Estimate.Summary.SampledSeconds, report.Estimated.SampledSeconds, 8);
        Assert.Equal(0, report.Raw.ThresholdSampledSeconds);
        Assert.Equal(report.Estimated.SampledSeconds, report.Estimated.HighSeconds, 8);
    }
}
