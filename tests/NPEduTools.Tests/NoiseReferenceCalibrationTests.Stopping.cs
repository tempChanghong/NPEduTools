using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseReferenceCalibrationTests
{
    [Theory]
    [InlineData("format", "A_WEIGHTING_UNAVAILABLE")]
    [InlineData("disconnect-on-stop", "INPUT_SETTINGS_UNAVAILABLE")]
    public async Task Finalization_preserves_session_reference_failure_in_statistics_and_upload(string cause, string reason)
    {
        await using var f = new Fixture();
        await f.PrepareAsync(); Assert.Equal("Succeeded", f.Command("confirm").Outcome);
        var profile = f.State.Profile;
        var transport = new NoiseTransport(f.Noise, Path.Combine(f.DirectoryPath, "outbox")); transport.Bind("synthetic-school-class");
        f.NormalReferenceUnavailable = cause == "format";
        await StartFormal(f); FormalFrames(f, 60);
        var before = f.Noise.Snapshot();
        if (cause == "format") Assert.Equal(reason, before.Estimate!.Reason);
        else
        {
            Assert.Equal("Active", before.Estimate!.State);
            // The endpoint vanishes at normal stopping, without a failure event or
            // a live status read discovering it before the final metadata check.
            f.Capture!.EndpointDisconnected = true;
        }
        f.Noise.StopScheduled(before.SessionId!.Value);
        await Until(() => !f.Noise.CaptureBusy);
        var final = f.Noise.Snapshot();
        Assert.Equal(reason, final.Estimate!.Reason);
        Assert.Equal("Invalidated", final.Estimate.State); Assert.Null(final.Estimate.CurrentDbA);
        Assert.Equal(before.Estimate!.Summary, final.Estimate.Summary);
        var statistics = f.Noise.FinishedStudyReport(before.SessionId.Value)!;
        Assert.True(statistics.Complete); Assert.Equal(reason, statistics.EstimateReason);
        var uploaded = transport.Reports().Single()!.AsObject(); NpepNoiseProtocol.Validate("report", uploaded);
        Assert.Equal(reason, uploaded["estimate"]!["reason"]!.GetValue<string>());
        Assert.Null(f.State.InvalidationReason); // Runtime unavailability is not permanent profile invalidation.
        Assert.Equal(profile, f.State.Profile);
        f.NormalReferenceUnavailable = false;
        await StartFormal(f); FormalFrames(f, 60);
        var recovered = f.Noise.Snapshot().Estimate!;
        Assert.Equal("Active", recovered.State); Assert.Null(recovered.Reason);
        Assert.Equal(60, recovered.CurrentDbA!.Value, 6);
        Assert.Equal(profile, f.State.Profile);
        await StopFormal(f);
    }

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
