using System.Text.Json.Nodes;
using NPEduTools.Host;
using NPEduTools.Integrations.Npep;
namespace NPEduTools.Tests;
public sealed partial class NoiseScheduleHostTests
{
    [Fact]
    public async Task Completed_school_report_keeps_capture_context_statistics_and_survives_restart()
    {
        await using var f = new Fixture(); var config = StudyConfig(f);
        var transport = new NoiseTransport(f.Noise, System.IO.Path.Combine(f.Path,"outbox"));
        transport.Bind(f.Scope);
        await f.Scheduler.DisposeAsync(); f.Scheduler = f.NewScheduler(transport.OwnStudySession);
        Select(f,config); await f.Start(); f.Tick(); f.Captures[0].Sample(); f.Tick();
        Assert.Equal("Accepted", f.Noise.Handle(f.Command("stop")).Outcome);
        await Until(()=>transport.Reports().Count==1);
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
