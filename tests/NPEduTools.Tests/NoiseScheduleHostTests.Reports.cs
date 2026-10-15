using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Tests;
public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData(6000, false)] [InlineData(6000, true)]
    [InlineData(120000, false)] [InlineData(120000, true)]
    public async Task Delayed_report_collector_keeps_the_final_scheduled_capture_online_or_offline(int delayMs, bool offline)
    {
        await using var f = new Fixture(); f.Base = new(2026, 10, 1, 19, 59, 50, TimeSpan.Zero);
        var config = StudyConfig(f); long sequence = 0; var connection = Guid.NewGuid(); var bridge = Guid.NewGuid();
        await using var study = new StudySettingsService(() => new(connection, bridge, ++sequence, 0,
            f.Base.AddMilliseconds(f.Time.Ms), 0, "Advancing", "synthetic"), () => false, f.Time, false, f.Scheduler);
        study.BindStudy(f.Scope); study.ApplyStudy(config); study.ConfirmStudyExecution(config, Grant(config));
        await f.Start(); f.Tick(); study.Tick(); f.Tick(); study.Tick();
        f.Captures[0].Sample();
        await using var report = new StudyReportService(study.DisplaySnapshot, f.Noise.StudyReportSnapshot, f.Time, false,
            finishedStatistics: f.Noise.FinishedStudyReport);
        Assert.Equal("Monitoring", report.Snapshot().State);
        var session = f.Noise.Snapshot().SessionId;
        if (offline) study.StudyFailed("STUDY_OFFLINE");
        // Keep the real scheduler and school-clock observer advancing while the
        // report collector misses the end boundary. No clock jump or new capture.
        while (f.Time.Ms < 10000 + delayMs)
        {
            f.Tick(); f.Captures[0].Sample();
            if (offline) study.Tick(); else study.ApplyStudy(config);
        }
        await Until(() => !f.Noise.CaptureBusy);
        var final = report.Snapshot(); var capture = f.Noise.StudyReportSnapshot()!;
        Assert.True(final.Available); Assert.True(final.Data!.Complete);
        Assert.Equal((305000 - delayMs) / 1000d, final.RemainingSeconds, 6);
        Assert.Equal(capture.Raw.SampledSeconds, final.Data.SampledSeconds, 6);
        Assert.Equal(capture.Frames, final.Data.Frames);
        Assert.Equal(session, capture.SessionId); Assert.Single(f.Captures); Assert.True(f.Captures[0].Disposed);
        Assert.Equal("Stopped", f.Noise.Snapshot().State);
    }

    [Fact]
    public async Task Completed_school_report_keeps_capture_context_statistics_and_survives_restart()
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        var transport = new NoiseTransport(f.Noise, System.IO.Path.Combine(f.Path,"outbox"));
        transport.Bind(f.Scope);
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(transport.OwnStudySession);
        Select(f,config); await f.Start(); f.Tick(); f.Captures[0].Sample(); f.Tick();
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(()=>f.Noise.Snapshot().State=="Stopped");
        await Until(()=>transport.Reports().Count==1);
        Assert.Null(transport.Observe()["uploadError"]);
        var r=transport.Reports()[0]!.AsObject(); NpepNoiseProtocol.Validate("report",r);
        Assert.Equal("Daily",r["study"]!["mode"]!.GetValue<string>());
        Assert.Equal(config["scope"]!["classId"]!.GetValue<string>(),r["study"]!["scope"]!["classId"]!.GetValue<string>());
        Assert.Equal("dBFS",r["study"]!["statistics"]!["raw"]!["unit"]!.GetValue<string>());
        var bad=r.DeepClone().AsObject();bad["study"]!["clockSource"]="Windows";
        Assert.Throws<NpepException>(()=>NpepNoiseProtocol.Validate("report",bad));
        var restored = new NoiseTransport(f.Noise,System.IO.Path.Combine(f.Path,"outbox"));
        Assert.True(JsonNode.DeepEquals(r,restored.Reports()[0]));
    }
}
