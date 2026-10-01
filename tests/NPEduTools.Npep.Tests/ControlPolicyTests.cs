using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class ControlPolicyTests
{
    private const string ScopeA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ScopeB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void LegacyPolicyRequiresFreshBindingThenUsesPairingAuthority()
    {
        using var dir = new TestDirectory();
        var legacy = new ControlPolicyDocument(1, 7, true, ScopeA, Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(dir.Path);
        string path = Path.Combine(dir.Path, "runtime-control-policy.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(legacy, Protocol.Json));
        var migrated = new NpepControlPolicyStore(dir.Path);
        Assert.Null(migrated.Error);
        Assert.False(migrated.State.Allowed);
        Assert.Equal(3, migrated.State.Version);
        Assert.True(migrated.State.Revision > legacy.Revision);
        Assert.NotEqual(legacy.ConsentId, migrated.State.ConsentId);
        Assert.NotEqual(legacy.ControlEpoch, migrated.State.ControlEpoch);
        Assert.False(new NpepControlPolicyStore(dir.Path).State.Allowed);
        migrated.Synchronize(ScopeA);
        Assert.True(new NpepControlPolicyStore(dir.Path).State.Allowed);
    }

    [Fact]
    public void ConsentSurvivesRestartButEpochAndRevisionAdvance()
    {
        using var dir = new TestDirectory();
        var store = new NpepControlPolicyStore(dir.Path);
        Assert.False(store.State.Allowed);
        store.Synchronize(ScopeA);
        store.Set(true, store.State.Revision, ScopeA);
        var before = store.State;
        var restarted = new NpepControlPolicyStore(dir.Path);
        restarted.Synchronize(ScopeA);
        Assert.True(restarted.State.Allowed);
        Assert.Equal(before.ConsentId, restarted.State.ConsentId);
        Assert.NotEqual(before.ControlEpoch, restarted.State.ControlEpoch);
        Assert.True(restarted.State.Revision > before.Revision);
    }

    [Fact]
    public void ScopeChangeAndDisableReenableNeverReviveOldConsent()
    {
        using var dir = new TestDirectory(); var store = new NpepControlPolicyStore(dir.Path);
        store.Synchronize(ScopeA); store.Set(true, store.State.Revision, ScopeA);
        var original = store.State;
        store.Set(false, original.Revision, ScopeA);
        var disabled = store.State;
        store.Set(true, disabled.Revision, ScopeA);
        Assert.NotEqual(original.ConsentId, store.State.ConsentId);
        Assert.NotEqual(disabled.ConsentId, store.State.ConsentId);
        store.Synchronize(ScopeB);
        Assert.True(store.State.Allowed);
        Assert.Equal("POLICY_CHANGED", Assert.Throws<NpepException>(() => store.Set(true, original.Revision, ScopeA)).Code);
        store.Synchronize(ScopeA);
        Assert.True(store.State.Allowed);
        store.Synchronize(null); Assert.False(store.State.Allowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrInterruptedWriteFailsClosedAndPreservesEvidence(bool pending)
    {
        using var dir = new TestDirectory(); var store = new NpepControlPolicyStore(dir.Path);
        store.Synchronize(ScopeA); store.Set(true, store.State.Revision, ScopeA);
        string path = Path.Combine(dir.Path, "runtime-control-policy.json") + (pending ? ".pending" : "");
        File.WriteAllText(path, "interrupted");
        var restarted = new NpepControlPolicyStore(dir.Path);
        Assert.Equal("POLICY_STORE_UNAVAILABLE", restarted.Error);
        Assert.False(restarted.State.Allowed);
        Assert.Throws<NpepException>(() => restarted.Set(true, restarted.State.Revision, ScopeA));
        Assert.Equal("interrupted", File.ReadAllText(path));
    }

    [Fact]
    public void LocalProtocolDoesNotExposeExecutionOrAcceptMixedParameters()
    {
        var consent = new HostRequest(1, Guid.NewGuid(), "remoteexam.command", RemoteExam: new("consent", 3, true, ScopeA));
        Assert.Null(Protocol.Validate(consent));
        Assert.Null(Protocol.Validate(consent with { RemoteExam = new("end-local", 3, OperationId: Guid.NewGuid()) }));
        Assert.NotNull(Protocol.Validate(consent with { RemoteExam = new("run", 3) }));
        Assert.NotNull(Protocol.Validate(consent with { RemoteExam = new("consent", 3, true) }));
        Assert.NotNull(Protocol.Validate(consent with { RemoteExam = new("end-local", 3, Allowed: true, OperationId: Guid.NewGuid()) }));
        Assert.NotNull(Protocol.Validate(consent with { ExecutablePath = "other.exe" }));
        Assert.NotNull(Protocol.Validate(consent with { Capability = "remoteexam.status" }));
        Assert.NotNull(Protocol.Validate(consent with { ObserveMs = 1 }));
        Assert.NotNull(Protocol.Validate(consent with { Capability = "remoteexam.run", RemoteExam = null }));
    }
}
