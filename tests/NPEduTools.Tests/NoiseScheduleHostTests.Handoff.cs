using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Theory]
    [InlineData("legacy", false)] [InlineData("study", false)] [InlineData("manual", false)]
    [InlineData("legacy", true)] [InlineData("study", true)] [InlineData("manual", true)]
    public async Task Reconfirmed_schedule_waits_for_stopped_session_callbacks_before_replacing_durable_ownership(string owner, bool endsWhileWaiting)
    {
        await using var f = new Fixture();
        JsonObject? config = owner != "legacy" ? StudyConfig(f) : null;
        if (owner == "manual")
        {
            Assert.Equal("Accepted", f.Noise.StartManualStudy(Guid.NewGuid(), "synthetic", 10800).Outcome);
            await Until(() => f.Noise.Snapshot().State == "Active");
        }
        else
        {
            if (config is not null) Select(f, config);
            await f.Start();
        }
        var original = f.Noise.Snapshot().SessionId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        Action<NoiseState> completion = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        f.Noise.Completed += completion;
        try
        {
            // A temporarily withdrawn execution can be confirmed again while its
            // terminal report callback is still running. Stopped is not yet released.
            f.Scheduler.Bind(f.Scope, false);
            if (owner == "manual") Assert.True(f.Noise.StopManualStudy(original!.Value, "UserEnded"));
            await Until(() => entered.IsSet);
            Assert.Equal("Stopped", f.Noise.Snapshot().State); Assert.True(f.Noise.CaptureBusy);
            string path = Path.Combine(f.Path, "noise-schedule-state.json");
            string saved = File.ReadAllText(path);
            f.Scheduler.Bind(f.Scope, true);
            if (config is null) f.Scheduler.Confirm(f.Policy());
            else if (owner == "manual") Select(f, config);
            else f.Scheduler.ConfirmStudyExecution(config, Grant(config));
            // A newly selected school policy must first satisfy the existing
            // advancing-clock validation; keep the callback pending through that.
            for (int i = 0; i < 6; i++) f.Tick(100);
            Assert.Equal(saved, File.ReadAllText(path));
            Assert.Equal(original, f.Noise.Snapshot().SessionId); Assert.Single(f.Captures);
            var status = f.Scheduler.Observe();
            Assert.Equal("CAPTURE_BUSY", status.Text("reason")); Assert.Equal("None", status.Text("owner"));
            NpepNoiseScheduleProtocol.Validate("status", status);
            if (endsWhileWaiting) { f.Time.Ms = 3600000; f.Tick(); }
        }
        finally { release.Set(); await Until(() => !f.Noise.CaptureBusy); f.Noise.Completed -= completion; }
        f.Tick();
        if (endsWhileWaiting)
        {
            // The deliberate one-hour observation gap also needs fresh advancing
            // school-clock samples; it is not permission to bypass clock validation.
            f.Tick(); f.Tick();
            Assert.Single(f.Captures); Assert.Equal("OUTSIDE_WINDOW", f.Scheduler.Observe().Text("reason"));
        }
        else
        {
            await Until(() => { f.Scheduler.Tick(); return f.Noise.Snapshot().State == "Active"; });
            Assert.Equal(2, f.Captures.Count); Assert.NotEqual(original, f.Noise.Snapshot().SessionId);
            var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Path, "noise-schedule-state.json")))!;
            Assert.Equal(f.Noise.Snapshot().SessionId, journal["ownedId"]!.GetValue<Guid>());
        }
    }
}
