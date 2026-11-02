using NPEduTools.Contracts;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Fact]
    public async Task Same_capture_keeps_statistics_across_policy_versions_and_classifies_each_frame_in_its_own_unit()
    {
        await using var f = new Fixture();
        await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        await StartFormal(f);
        var session = f.Noise.Snapshot().SessionId!.Value;
        var raw = new StudyNoise(true, -35, -45, 600, 3);
        // Matches Program's report policy observer. No display polling, network,
        // physical microphone or sound meter is involved in this capture fixture.
        (string Band, bool Qualified) Apply(string version, StudyNoise policy) =>
            f.Noise.StudyThresholds(session, f.Noise.ResolveCalibration("synthetic-class", version, policy));
        void Statistics(double total, double rawClassified, double rawHigh, double rawMedium,
            double estimatedClassified, double estimatedHigh, double estimatedMedium)
        {
            var report = f.Noise.StudyReportSnapshot()!;
            Assert.Equal(session, report.SessionId);
            Assert.Equal("dBFS", report.Raw.Unit); Assert.Equal("estimated-dB(A)", report.Estimated!.Unit);
            Assert.Equal(total, report.Raw.SampledSeconds, 6);
            Assert.Equal(total, report.Estimated.SampledSeconds, 6);
            Assert.Equal(rawClassified, report.Raw.ThresholdSampledSeconds, 6);
            Assert.Equal(rawHigh, report.Raw.HighSeconds, 6); Assert.Equal(rawMedium, report.Raw.MediumSeconds, 6);
            Assert.Equal(estimatedClassified, report.Estimated.ThresholdSampledSeconds, 6);
            Assert.Equal(estimatedHigh, report.Estimated.HighSeconds, 6); Assert.Equal(estimatedMedium, report.Estimated.MediumSeconds, 6);
        }

        Apply("version-a", raw);
        FormalFrames(f, 40); // Raw -30 dBFS and estimated 60 dB(A).
        Assert.Equal(("High", true), Apply("version-a", raw));
        Statistics(4, 4, 4, 4, 0, 0, 0);
        // Merely replacing the configuration version cannot reset either the
        // cumulative report or the current continuous threshold evidence.
        Assert.Equal(("High", true), Apply("version-b", raw));
        Statistics(4, 4, 4, 4, 0, 0, 0);
        FormalFrames(f, 10);
        Statistics(5, 5, 5, 5, 0, 0, 0);

        var changedRaw = raw with { RedLineDbfs = -25, MediumLineDbfs = -35 };
        Assert.Equal(("None", false), Apply("version-c", changedRaw));
        Statistics(5, 5, 5, 5, 0, 0, 0);
        FormalFrames(f, 10);
        Assert.Equal(("Medium", false), Apply("version-c", changedRaw));
        Statistics(6, 6, 5, 6, 0, 0, 0);

        var estimated = raw with { Estimated = new(65, 50) };
        Assert.Equal(("None", false), Apply("version-d", estimated));
        FormalFrames(f, 40, aDigital: -24); // 66 dB(A), while raw remains -30 dBFS.
        Assert.Equal(("High", true), Apply("version-d", estimated));
        Statistics(10, 6, 5, 6, 4, 4, 4);
        Assert.Equal(("High", true), Apply("version-e", estimated));
        Statistics(10, 6, 5, 6, 4, 4, 4);

        var changedEstimated = estimated with { Estimated = new(70, 65) };
        Assert.Equal(("None", false), Apply("version-f", changedEstimated));
        FormalFrames(f, 10, aDigital: -24);
        Assert.Equal(("Medium", false), Apply("version-f", changedEstimated));
        Statistics(11, 6, 5, 6, 5, 4, 5);

        Assert.Equal(("None", false), Apply("version-g", raw));
        FormalFrames(f, 10);
        Assert.Equal(("High", false), Apply("version-g", raw));
        Statistics(12, 7, 6, 7, 5, 4, 5);
        await StopFormal(f);
        Assert.True(f.Noise.FinishedStudyReport(session)!.Complete);
        Statistics(12, 7, 6, 7, 5, 4, 5);
    }
}
