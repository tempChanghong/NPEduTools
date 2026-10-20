using NPEduTools.Integrations.Npep;

namespace NPEduTools.Tests;

public sealed partial class NoiseScheduleHostTests
{
    [Fact]
    public async Task Disconnect_retry_does_not_replace_the_session_journal_before_completion_finishes()
    {
        await using var f = new Fixture(); await f.Start();
        var original = f.Noise.Snapshot().SessionId;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Noise.Completed += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        try
        {
            f.Captures[0].EndpointDisconnected = true; f.Captures[0].Break(); await Until(() => entered.IsSet);
            f.Tick();
            var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(f.Path, "noise-schedule-state.json")))!;
            Assert.Equal(original, journal["ownedId"]!.GetValue<Guid>());
            Assert.Single(f.Captures); Assert.Equal(0, f.Enumerations);
        }
        finally { release.Set(); await Until(() => !f.Noise.CaptureBusy); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Confirmed_device_disconnect_waits_then_resumes_same_authorized_window(bool study)
    {
        await using var f = new Fixture();
        if (study) { var config = StudyConfig(f); f.Scheduler.SelectStudy(config, f.Scope); f.Scheduler.ConfirmStudyExecution(config, Grant(config)); }
        await f.Start(); var original = f.Noise.Snapshot().SessionId;
        f.MicrophonePresent = false; f.Captures[0].EndpointDisconnected = true; f.Captures[0].Break();
        await Until(() => !f.Noise.CaptureBusy); f.Tick();
        for (int i = 0; i < 30; i++) f.Tick(100);
        Assert.Single(f.Captures); Assert.Equal(0, f.Enumerations);
        for (int i = 0; i < 30; i++) f.Tick(100);
        Assert.Single(f.Captures); Assert.Equal(1, f.Enumerations);
        Assert.Equal("MICROPHONE_DISCONNECTED", f.Scheduler.Observe().Text("reason"));
        NpepNoiseScheduleProtocol.Validate("status", f.Scheduler.Observe());
        f.MicrophonePresent = true;
        for (int i = 0; i < 50; i++) f.Tick(100);
        await Until(() => { f.Scheduler.Tick(); return f.Noise.Snapshot().State == "Active"; });
        Assert.Equal(2, f.Captures.Count); Assert.NotEqual(original, f.Noise.Snapshot().SessionId);
        f.Captures[^1].Sample(); f.Tick();
        Assert.Equal("WINDOW_ACTIVE", f.Scheduler.Observe().Text("reason"));
    }

    [Theory]
    [InlineData("revoked")] [InlineData("ended")] [InlineData("skipped")]
    public async Task Reconnected_device_never_overrides_revocation_end_or_explicit_stop(string change)
    {
        await using var f = new Fixture(); await f.Start();
        f.Captures[0].EndpointDisconnected = true; f.Captures[0].Break(); await Until(() => !f.Noise.CaptureBusy); f.Tick();
        if (change == "revoked") f.Scheduler.Bind(f.Scope, false);
        else if (change == "ended") { f.MicrophonePresent = false; while (f.Time.Ms < 3600000) f.Tick(500); f.MicrophonePresent = true; }
        else Assert.True(f.Noise.ManualStopping!(f.Noise.Snapshot().SessionId!.Value));
        for (int i = 0; i < 150; i++) f.Tick(100);
        Assert.Single(f.Captures);
    }
}
