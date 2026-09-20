using System.Text;
using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed class DeviceTests
{
    [Fact]
    public async Task LocalApprovalIsRequiredAndCredentialsAreNotPrintedOrStoredInPlaintext()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using var device = new NpepDevice(dir.Path, server.Api);
        await server.PrepareAsync(device);
        Assert.Equal("LOCAL_CONFIRMATION_REQUIRED", (await Assert.ThrowsAsync<NpepException>(() => device.ConfirmAsync(NpepProtocol.Id()))).Code);
        Assert.False(server.Active);
        await device.ConfirmAsync(server.Approval.Text("approvalId"));
        Assert.Equal("ACTIVE", device.View().Text("state"));
        foreach (var secret in new[] { server.Create!.Text("pairingSecret"), server.Confirm!.Text("deviceSecret") })
        {
            Assert.DoesNotContain(secret, device.View().ToJsonString());
            Assert.DoesNotContain(secret, Encoding.UTF8.GetString(File.ReadAllBytes(System.IO.Path.Combine(dir.Path, "npep.credentials.dpapi"))));
        }
        Assert.NotEqual(server.Create!.Text("pairingSecret"), server.Confirm!.Text("deviceSecret"));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmationLossRecoversAcrossRestartWithoutChangingCandidate(bool committed)
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseConfirmBeforeCommit = !committed, LoseConfirmAfterCommit = committed };
        using (var device = new NpepDevice(dir.Path, server.Api))
        {
            await server.PrepareAsync(device);
            await Assert.ThrowsAsync<HttpRequestException>(() => device.ConfirmAsync(server.Approval.Text("approvalId")));
            Assert.Equal("CONFIRMING", device.View().Text("state"));
        }
        var candidate = server.Confirm!.Copy();
        using (var recovered = new NpepDevice(dir.Path, server.Api))
        {
            Assert.Equal("ACTIVE", (await recovered.RecoverConfirmationAsync()).Text("state"));
            Assert.True(NpepProtocol.Equal(candidate, server.Confirm));
        }
        Assert.Equal(committed ? 1 : 2, server.Requests.Count(r => r.Path.EndsWith("/confirm", StringComparison.Ordinal)));
    }
    [Fact]
    public async Task RevokedUncertainConfirmationNeverBecomesActive()
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseConfirmAfterCommit = true };
        using var device = new NpepDevice(dir.Path, server.Api);
        await server.PrepareAsync(device);
        await Assert.ThrowsAsync<HttpRequestException>(() => device.ConfirmAsync(server.Approval.Text("approvalId")));
        server.Revoked = true;
        await Assert.ThrowsAsync<NpepException>(() => device.RecoverConfirmationAsync());
        Assert.Equal("CONFIRMING", device.View().Text("state"));
    }
    [Fact]
    public async Task LostSessionReplyRetriesSameRequestWithoutReclaimingNewerSessions()
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseSession = true };
        using var device = new NpepDevice(dir.Path, server.Api); await server.ActivateAsync(device);
        await Assert.ThrowsAsync<HttpRequestException>(() => device.OpenSessionAsync());
        await device.OpenSessionAsync();
        Assert.Equal(1, server.Epoch);
        var sent = server.Requests.Where(r => r.Path == "device/sessions").ToArray();
        Assert.Equal(2, sent.Length); Assert.True(NpepProtocol.Equal(sent[0].Body, sent[1].Body));
    }
    [Fact]
    public async Task StatusTimeoutUsesNewSampleAndSequenceInsteadOfReplayingOldHeartbeat()
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseStatus = true };
        using var device = new NpepDevice(dir.Path, server.Api); await server.ActivateAsync(device); await device.OpenSessionAsync();
        var status = NpepStatusReader.Map(null, "test");
        await Assert.ThrowsAsync<HttpRequestException>(() => device.ReportAsync(status, 0));
        status["mode"] = "EXAM";
        var receipt = await device.ReportAsync(status, 1);
        Assert.Equal(2, receipt.Number("acceptedSequence"));
        var sent = server.Requests.Where(r => r.Path == "device/status").Select(r => r.Body!).ToArray();
        Assert.NotEqual(sent[0].Text("requestId"), sent[1].Text("requestId"));
        Assert.Equal("UNKNOWN", sent[0]["status"]!["mode"]!.GetValue<string>());
        Assert.Equal("EXAM", sent[1]["status"]!["mode"]!.GetValue<string>());
    }
    [Theory]
    [InlineData("revoked")]
    [InlineData("epoch")]
    [InlineData("session")]
    public async Task IdentityOrAuthorizationFailureStopsAndPersistsSuspension(string fault)
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using (var device = new NpepDevice(dir.Path, server.Api))
        {
            await server.ActivateAsync(device);
            server.Revoked = fault == "revoked"; server.ChangeEpoch = fault == "epoch"; server.SessionConflict = fault == "session";
            await Assert.ThrowsAsync<NpepException>(() => device.OpenSessionAsync());
            int calls = server.Requests.Count;
            await Assert.ThrowsAsync<NpepException>(() => device.OpenSessionAsync());
            Assert.Equal(calls, server.Requests.Count);
        }
        using var restarted = new NpepDevice(dir.Path, server.Api);
        Assert.Equal("SUSPENDED", restarted.View().Text("state"));
        Assert.True(restarted.View()["suspended"]!.GetValue<bool>());
        await Assert.ThrowsAsync<NpepException>(() => restarted.OpenSessionAsync());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpairReportsRemoteFailureHonestlyAndErasesLocalCredentials(bool fail)
    {
        using var dir = new TestDirectory(); var server = new FakeServer { FailRevoke = fail };
        using var device = new NpepDevice(dir.Path, server.Api); await server.ActivateAsync(device);
        Assert.Equal(fail ? "LOCAL_ONLY" : "REVOKED", await device.UnpairAsync());
        Assert.False(File.Exists(System.IO.Path.Combine(dir.Path, "npep.credentials.dpapi")));
        Assert.Equal("UNPAIRED", device.View().Text("state"));
        Assert.Equal(!fail, server.Revoked);
    }
    [Fact]
    public async Task UnpairRecoversAndRevokesCandidateAfterConfirmationResponseLoss()
    {
        using var dir = new TestDirectory(); var server = new FakeServer { LoseConfirmAfterCommit = true };
        using var device = new NpepDevice(dir.Path, server.Api); await server.PrepareAsync(device);
        await Assert.ThrowsAsync<HttpRequestException>(() => device.ConfirmAsync(server.Approval.Text("approvalId")));
        Assert.Equal("REVOKED", await device.UnpairAsync()); Assert.True(server.Revoked);
        Assert.DoesNotContain(server.Requests, r => r.Path.EndsWith("/cancel", StringComparison.Ordinal));
    }
    [Fact]
    public async Task CrashDuringUnpairCannotResumeReportingOnRestart()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using (var device = new NpepDevice(dir.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal)) throw new InvalidOperationException("Simulated abrupt termination");
            return server.Send(request);
        }))))
        {
            await server.ActivateAsync(device);
            await Assert.ThrowsAsync<InvalidOperationException>(() => device.UnpairAsync());
        }
        using var restarted = new NpepDevice(dir.Path, server.Api);
        Assert.Equal("UNPAIRING", restarted.View().Text("state"));
        await Assert.ThrowsAsync<NpepException>(() => restarted.OpenSessionAsync());
        Assert.Equal("REVOKED", await restarted.UnpairAsync());
    }
    [Fact]
    public async Task OldMonotonicSampleNeverReachesNetwork()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using var device = new NpepDevice(dir.Path, server.Api); await server.ActivateAsync(device); await device.OpenSessionAsync();
        long old = System.Diagnostics.Stopwatch.GetTimestamp() - 6 * System.Diagnostics.Stopwatch.Frequency;
        var sample = new NpepSample(NpepStatusReader.Map(null, "test"), old);
        Assert.Equal("SAMPLE_TOO_OLD", (await Assert.ThrowsAsync<NpepException>(() => device.ReportAsync(sample))).Code);
        Assert.DoesNotContain(server.Requests, r => r.Path == "device/status");
    }
    [Fact]
    public void VaultIsExclusiveAndRejectsCorruptionWithoutOverwritingIt()
    {
        using var dir = new TestDirectory();
        using (var vault = new NpepVault(dir.Path))
        {
            vault.Save(new() { ["secret"] = "isolated-test-secret" });
            Assert.Equal("isolated-test-secret", vault.Load()!.Text("secret"));
            Assert.Throws<IOException>(() => new NpepVault(dir.Path));
        }
        string path = System.IO.Path.Combine(dir.Path, "npep.credentials.dpapi");
        File.WriteAllText(path, "corrupt-test-only");
        Assert.Throws<NpepException>(() => new NpepDevice(dir.Path));
        Assert.Equal("corrupt-test-only", File.ReadAllText(path));
    }
}
