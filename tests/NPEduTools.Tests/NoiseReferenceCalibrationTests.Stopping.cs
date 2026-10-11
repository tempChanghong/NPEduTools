using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Theory]
    [InlineData("manual")]
    [InlineData("scheduled")]
    [InlineData("failure")]
    public async Task StoppingCaptureWithdrawsLiveEstimateButPreservesStatisticsAndValidTransport(string cause)
    {
        await using var f = new Fixture();
        await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        var transport = new NoiseTransport(f.Noise, f.DirectoryPath); transport.Bind("synthetic-school-class");
        await StartFormal(f, scheduled: cause == "scheduled"); FormalFrames(f, 60);
        var before = f.Noise.Snapshot(); Assert.Equal("Active", before.Estimate!.State);
        NpepNoiseProtocol.Validate("status", transport.Observe());
        using var cleanup = new ManualResetEventSlim(false);
        f.Capture!.CleanupGate = cleanup;
        try
        {
            if (cause == "failure") f.Capture.Disconnect();
            else if (cause == "scheduled") f.Noise.StopScheduled(before.SessionId!.Value);
            else Assert.Equal("Accepted", f.Noise.Handle(new(1, Guid.NewGuid(), "noise.command",
                Noise: new("stop", before.InstanceId, before.Revision))).Outcome);
            var stopping = f.Noise.Snapshot();
            Assert.Equal("Stopping", stopping.State); Assert.Null(stopping.CurrentDbfs);
            NpepNoiseProtocol.Validate("status", transport.Observe());
            Assert.Null(stopping.Estimate!.CurrentDbA); Assert.Equal("NoData", stopping.Estimate.State);
            Assert.Equal(before.Estimate.Summary, stopping.Estimate.Summary);
            Assert.Equal(before.EstimatedTrend, stopping.EstimatedTrend);
        }
        finally { cleanup.Set(); }
        await Until(() => !f.Noise.CaptureBusy);
        Assert.Equal("Stopped", f.Noise.Snapshot().Estimate!.State);
        var report = transport.Reports().Single()!.AsObject(); NpepNoiseProtocol.Validate("report", report);
        Assert.Null(report["estimate"]!["currentDbA"]);
        Assert.Equal(before.Estimate.Summary.SampledSeconds, report["estimate"]!["summary"]!["sampledSeconds"]!.GetValue<double>());
    }
}
