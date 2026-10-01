using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Npep.Tests;

public sealed partial class RuntimeTests
{
    private sealed class CancelHandler(TaskCompletionSource entered) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Cancellation expected");
        }
    }
    private static HostResponse Sample() => new(1, Guid.NewGuid(), "Succeeded", null, "", ClassroomMode: new(3, "Daily"), Recording: new("Idle", ""));
    private static async Task Until(Func<bool> test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!test()) await Task.Delay(30, deadline.Token);
    }
    private static async Task<NpepState> Command(NpepRuntime runtime, string action, FakeServer? server = null)
    {
        var c = new NpepCommand(action, runtime.Snapshot().Revision,
            Origin: action is "inspect" or "pair" ? "https://npep.test" : null,
            DeviceName: action == "pair" ? "test screen" : null,
            ServerInstanceId: action == "pair" ? server!.Info.Text("serverInstanceId") : null,
            DeploymentEpoch: action == "pair" ? server!.Info.Text("deploymentEpoch") : null,
            ApprovalId: action == "confirm" ? server!.Approval.Text("approvalId") : null);
        var reply = runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: c));
        Assert.Equal("Accepted", reply.Outcome);
        await Until(() => !runtime.Snapshot().Busy);
        return runtime.Snapshot();
    }
    [Fact]
    public async Task UnpairedHostNeverUsesNetworkAndPairRequiresInspectedIdentity()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        await using var runtime = new NpepRuntime(() => new(dir.Path, server.Api), "test", Sample);
        await Task.Delay(600);
        Assert.Empty(server.Requests);
        Assert.Equal("LOCAL_CONFIRMATION_REQUIRED", (await Command(runtime, "pair", server)).Error);
        Assert.Empty(server.Requests);
        await Command(runtime, "inspect");
        Assert.Equal("PENDING", (await Command(runtime, "pair", server)).State);
        Assert.DoesNotContain(server.Requests, r => r.Path == "device/status");
    }
    [Fact]
    public async Task RuntimeReportsAfterConfirmationAndPauseSurvivesRestart()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        await using (var runtime = new NpepRuntime(() => new(dir.Path, server.Api), "test", Sample))
        {
            await Command(runtime, "inspect"); await Command(runtime, "pair", server);
            await Command(runtime, "poll");
            Assert.False(server.Active);
            Assert.Equal("ACTIVE", (await Command(runtime, "confirm", server)).State);
            await Until(() => runtime.Snapshot().Connection == "ONLINE");
            Assert.NotNull(runtime.Snapshot().LastReceivedAt);
            Assert.True((await Command(runtime, "pause")).ReportingPaused);
        }
        int count = server.Requests.Count;
        await using var restarted = new NpepRuntime(() => new(dir.Path, server.Api), "test", Sample);
        await Task.Delay(600);
        Assert.Equal(count, server.Requests.Count);
        Assert.True(restarted.Snapshot().ReportingPaused);
        server.SessionRequest = null; // A new Host run may establish a new fenced session.
        await Command(restarted, "resume");
        await Until(() => restarted.Snapshot().Connection == "ONLINE");
        Assert.Equal("UNPAIRED", (await Command(restarted, "unpair")).State);
        Assert.True(server.Revoked);
    }
    [Fact]
    public async Task BusyNetworkDoesNotBlockStatusOrDuplicateCommands()
    {
        using var dir = new TestDirectory(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource(); var server = new FakeServer();
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(async request =>
        { entered.SetResult(); await release.Task; return await server.Send(request); }))), "test", Sample);
        var request = new HostRequest(1, Guid.NewGuid(), "npep.command", Npep: new("inspect", runtime.Snapshot().Revision, "https://npep.test"));
        try
        {
            Assert.Equal("Accepted", runtime.Handle(request).Outcome);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(runtime.Snapshot().Busy);
            Assert.Equal("Accepted", runtime.Handle(request).Outcome);
            Assert.Equal("Succeeded", runtime.Handle(new(1, Guid.NewGuid(), "npep.status")).Outcome);
            Assert.Equal("Rejected", runtime.Handle(request with { RequestId = Guid.NewGuid() }).Outcome);
        }
        finally { release.SetResult(); }
        await Until(() => !runtime.Snapshot().Busy);
        Assert.Single(server.Requests);
    }
    [Fact]
    public async Task RevokedDeviceStopsBackgroundReportingWithoutReclaimingSession()
    {
        using var dir = new TestDirectory(); var server = new FakeServer();
        using (var device = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(device);
        server.Revoked = true;
        await using var runtime = new NpepRuntime(() => new(dir.Path, server.Api), "test", Sample);
        await Until(() => runtime.Snapshot().State == "SUSPENDED");
        int calls = server.Requests.Count;
        await Task.Delay(600);
        Assert.Equal(calls, server.Requests.Count);
        Assert.Equal("STOPPED", runtime.Snapshot().Connection);
    }
    [Fact]
    public async Task ShutdownCancelsNetworkAndReleasesCredentialLease()
    {
        using var dir = new TestDirectory(); var entered = new TaskCompletionSource();
        var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new CancelHandler(entered))), "test", Sample);
        try
        {
            runtime.Handle(new(1, Guid.NewGuid(), "npep.command", Npep: new("inspect", runtime.Snapshot().Revision, "https://npep.test")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
        using var reopened = new NpepDevice(dir.Path);
        Assert.Equal("UNPAIRED", reopened.View().Text("state"));
    }
    [Fact]
    public async Task CertificateFailureRemainsVisibleAndRetriesWithoutClaimingTrust()
    {
        using var dir = new TestDirectory(); int requests = 0;
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(_ =>
        {
            Interlocked.Increment(ref requests);
            throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private certificate details");
        }))), "test", Sample);
        Assert.Equal("TLS_VALIDATION_FAILED", (await Command(runtime, "inspect")).Error);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        await Until(() => Volatile.Read(ref requests) >= 2);
        Assert.Equal("TLS_VALIDATION_FAILED", runtime.Snapshot().Error);
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Assert.DoesNotContain("private", runtime.Snapshot().Message);
        Assert.Null(runtime.Snapshot().Server);
    }

    [Fact]
    public async Task CertificateRecoveryAutomaticallyCompletesTheOriginalInspection()
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); int requests = 0;
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (Interlocked.Increment(ref requests) == 1)
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private certificate details");
            return server.Send(request);
        }))), "test", Sample);
        await Command(runtime, "inspect");
        await Until(() => runtime.Snapshot().Connection == "VERIFIED");
        Assert.Null(runtime.Snapshot().Error);
        Assert.NotNull(runtime.Snapshot().Server);
        Assert.Equal("UNPAIRED", runtime.Snapshot().State);
        Assert.DoesNotContain(server.Requests, r => r.Path == "pairings");
    }

    [Fact]
    public async Task ActiveDeviceRecoversFromTlsFailureWithoutManualResume()
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); int broken = 1;
        using (var device = new NpepDevice(dir.Path, server.Api)) await server.ActivateAsync(device);
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(request =>
        {
            if (Volatile.Read(ref broken) == 1)
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "private certificate details");
            return server.Send(request);
        }))), "test", Sample);
        await Until(() => runtime.Snapshot().Error == "TLS_VALIDATION_FAILED");
        Assert.Equal("OFFLINE", runtime.Snapshot().Connection);
        Assert.Equal("ACTIVE", runtime.Snapshot().State);
        Interlocked.Exchange(ref broken, 0);
        await Until(() => runtime.Snapshot().Connection == "ONLINE");
        Assert.Null(runtime.Snapshot().Error);
        Assert.NotNull(runtime.Snapshot().LastReceivedAt);
    }
    [Fact]
    public async Task TlsRetryResumesTheSamePairingCandidate()
    {
        using var dir = new TestDirectory(); var server = new FakeServer(); int attempts = 0;
        await using var runtime = new NpepRuntime(() => new(dir.Path, origin => new NpepApi(origin, new Handler(async request =>
        {
            var response = await server.Send(request);
            if (request.RequestUri!.AbsolutePath.EndsWith("/pairings", StringComparison.Ordinal) &&
                Interlocked.Increment(ref attempts) == 1)
            {
                response.Dispose();
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "test TLS interruption");
            }
            return response;
        }))), "test", Sample);
        await Command(runtime, "inspect");
        Assert.Equal("CREATING", (await Command(runtime, "pair", server)).State);
        await Until(() => runtime.Snapshot().State == "PENDING");
        var creates = server.Requests.Where(r => r.Path == "pairings").ToArray();
        Assert.Equal(2, creates.Length);
        Assert.True(NpepProtocol.Equal(creates[0].Body, creates[1].Body));
        Assert.False(server.Active); // Approval and local confirmation remain required.
    }

    [Fact]
    public async Task InvalidStoreDoesNotDisableOtherHostCapabilities()
    {
        await using var runtime = new NpepRuntime(() => throw new IOException("private path"), "test", Sample);
        Assert.Equal("STORE_UNAVAILABLE", runtime.Snapshot().State);
        Assert.DoesNotContain("private", runtime.Snapshot().Message);
        Assert.Equal("Succeeded", runtime.Handle(new(1, Guid.NewGuid(), "npep.status")).Outcome);
    }
    [Theory]
    [InlineData("mode", null)]
    [InlineData("inspect", "ftp://school.test")]
    [InlineData("inspect", "https://user:secret@school.test")]
    [InlineData("inspect", "https://school.test/path")]
    public void LocalContractRejectsControlCommandsAndUnsafeOrigins(string action, string? origin)
        => Assert.NotNull(Protocol.Validate(new(1, Guid.NewGuid(), "npep.command", Npep: new(action, 0, origin))));

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://school.test")]
    [InlineData("https://school.test")]
    public void LocalContractAllowsInspectAndPairOverBothTransports(string origin)
    {
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "npep.command", Npep: new("inspect", 0, origin))));
        Assert.Null(Protocol.Validate(new(1, Guid.NewGuid(), "npep.command", Npep: new("pair", 0, origin, "测试设备", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")))));
    }
}
