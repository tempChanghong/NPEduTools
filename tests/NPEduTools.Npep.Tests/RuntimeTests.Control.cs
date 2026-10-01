using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public async Task PairingGrantsControlPauseStopsExecutionAndUnpairRevokes()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        await using var runtime = new NpepRuntime(() => new(dir.Path, server.Api), "test", Sample);
        Assert.False(runtime.ControlPolicy().CanEnable);
        await Command(runtime, "inspect"); await Command(runtime, "pair", server);
        await Command(runtime, "poll"); await Command(runtime, "confirm", server);
        await Until(() => runtime.ControlPolicy().CanEnable);
        var policy = runtime.ControlPolicy();
        runtime.SetControlConsent(new("consent", policy.Revision, true, policy.Scope));
        var enabled = runtime.ControlPolicy(); Assert.True(enabled.Allowed);
        await Command(runtime, "pause");
        var paused = runtime.ControlPolicy();
        Assert.True(paused.Allowed); Assert.False(paused.CanEnable);
        Assert.NotEqual(enabled.ControlEpoch, paused.ControlEpoch);
        Assert.Equal(enabled.ConsentId, paused.ConsentId);
        Assert.Throws<NpepException>(() => runtime.SetControlConsent(new("consent", paused.Revision, false, paused.Scope)));
        Assert.True(runtime.ControlPolicy().Allowed);
        await Command(runtime, "resume"); await Until(() => runtime.ControlPolicy().CanEnable);
        policy = runtime.ControlPolicy();
        runtime.SetControlConsent(new("consent", policy.Revision, true, policy.Scope));
        await Command(runtime, "unpair");
        Assert.False(runtime.ControlPolicy().Allowed); Assert.Null(runtime.ControlPolicy().Scope);
        Assert.DoesNotContain(server.Requests, x => x.Path.Contains("runtime-control"));
    }
}
