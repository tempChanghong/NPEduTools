using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class ExamAwareTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NPEduTools.ExamAwareTests", Guid.NewGuid().ToString("N"));
    private sealed class Target : IExamAwareTarget
    {
        public int Starts;
        public string? Link;
        public string Validate(string path) => path;
        public void Open(string path, string? link) { Starts++; Link = link; }
        public readonly Observed Process = new();
        public IExamAwareProcess Capture(int processId, string path) => Process;
    }
    private sealed class Observed : IExamAwareProcess { public bool HasExited { get; set; } public void Dispose() { } }
    private static HostRequest Request(string capability) => new(1, Guid.NewGuid(), capability,
        ExpectedRevision: capability == "examaware.autostart.set" ? 1 : null);
    private static Task<HostResponse> Configure(ExamAwareService service, long revision = 0) =>
        service.HandleAsync(Request("examaware.config.set") with { ExecutablePath = @"C:\中文 目录\ExamAware.exe", ExpectedRevision = revision });
    private static async Task<ExamAwarePairing> Pairing(ExamAwareService service) =>
        (await service.HandleAsync(Request("examaware.pairing.get"))).ExamAwarePairing!;
    private static async Task Wait(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task<(TcpClient Client, ExamAwareHello Hello)> Connect(ExamAwarePairing pairing)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, pairing.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var hello = await Protocol.ReadAsync<ExamAwareHello>(client.GetStream(), timeout.Token);
        Assert.True(ExamAwareService.Verify(pairing.Key, "host\n" + hello.Nonce, hello.Proof));
        string nonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await Protocol.WriteAsync(client.GetStream(), new ExamAwareHello(2, "challenge", nonce,
            ExamAwareService.Sign(pairing.Key, $"peer-auth\n{hello.Nonce}\n{nonce}")), timeout.Token);
        var ready = await Protocol.ReadAsync<ExamAwareHello>(client.GetStream(), timeout.Token);
        Assert.True(ExamAwareService.Verify(pairing.Key, $"host-auth\n{hello.Nonce}\n{nonce}", ready.Proof));
        return (client, hello with { Proof = nonce }); // Test helper stores the peer nonce for subsequent frames.
    }
    private static Task Send(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing,
        long sequence = 1, string version = "1.5.2", bool? registered = true, bool badProof = false, bool canSetAutoStart = false)
    {
        string payload = JsonSerializer.Serialize(new ExamAwareSample("ExamAware", version, "win32", true, registered, CanSetAutoStart: canSetAutoStart), Protocol.Json);
        return Protocol.WriteAsync(client.GetStream(), new ExamAwareFrame(2, "status", sequence, payload,
            badProof ? new string('0', 64) : ExamAwareService.Sign(pairing.Key, ExamAwareService.FrameText("peer", hello.Nonce, hello.Proof, sequence, "status", payload))), default);
    }
    [Fact]
    public async Task ReadOnlyStatusDoesNotLaunchOrExposeCredentials()
    {
        var target = new Target();
        await using var service = new ExamAwareService(_directory, target);
        var result = await service.HandleAsync(Request("examaware.status"));
        Assert.Null(result.ExamAwarePairing);
        Assert.Null(result.ExamAware!.AutoStartRegistered);
        Assert.Equal("Disconnected", result.ExamAware.BridgeState);
        Assert.Equal(0, target.Starts);
    }
    [Fact]
    public async Task LaunchIsDurablyDeduplicatedAndDoesNotClaimBridgeReadiness()
    {
        var target = new Target();
        var request = Request("examaware.start");
        ExamAwarePairing first;
        await using (var service = new ExamAwareService(_directory, target))
        {
            Assert.Equal("PathMissing", (await service.HandleAsync(request)).ErrorCode);
            Assert.Equal("Succeeded", (await Configure(service)).Outcome);
            Assert.Equal("RevisionConflict", (await Configure(service)).ErrorCode);
            first = await Pairing(service);
            var result = await service.HandleAsync(request);
            Assert.Equal("Accepted", result.Outcome);
            Assert.Equal("Disconnected", result.ExamAware!.BridgeState);
            Assert.Equal("AlreadyAccepted", (await service.HandleAsync(request)).ErrorCode);
            Assert.Equal("LaunchBusy", (await service.HandleAsync(Request("examaware.start"))).ErrorCode);
        }
        await using var restarted = new ExamAwareService(_directory, target);
        Assert.Equal(first, await Pairing(restarted));
        Assert.Equal("AlreadyAccepted", (await restarted.HandleAsync(request)).ErrorCode);
        Assert.Equal(1, target.Starts);
    }
    [Theory]
    [InlineData("examaware.settings", "examaware://settings/basic")]
    [InlineData("examaware.plugins", "examaware://settings/plugins")]
    public async Task OnlyAllowlistedLinksArePassed(string capability, string expected)
    {
        var target = new Target();
        await using var service = new ExamAwareService(_directory, target);
        await Configure(service);
        Assert.Equal("Accepted", (await service.HandleAsync(Request(capability))).Outcome);
        Assert.Equal(expected, target.Link);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AuthenticatedSnapshotAndDisconnectPreserveUnknown(bool? registered)
    {
        await using var service = new ExamAwareService(_directory, new Target());
        var pairing = await Pairing(service);
        var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing, registered: registered);
            await Wait(() => service.Snapshot().BridgeState == "Connected");
            Assert.Equal(registered, service.Snapshot().AutoStartRegistered);
        }
        await Wait(() => service.Snapshot().BridgeState == "Disconnected");
        Assert.Null(service.Snapshot().AutoStartRegistered);
        var (reconnected, nextHello) = await Connect(pairing);
        using (reconnected)
        {
            Assert.NotEqual(hello.Nonce, nextHello.Nonce);
            await Send(reconnected, nextHello, pairing);
            await Wait(() => service.Snapshot().BridgeState == "Connected");
        }
    }
    [Fact]
    public async Task InvalidProofCannotClaimConnectionAndReplayClosesPeer()
    {
        await using var service = new ExamAwareService(_directory, new Target());
        var pairing = await Pairing(service);
        var (bad, badHello) = await Connect(pairing);
        using (bad)
        {
            await Send(bad, badHello, pairing, badProof: true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Assert.Equal(0, await bad.GetStream().ReadAsync(new byte[1], timeout.Token));
            Assert.Equal("Disconnected", service.Snapshot().BridgeState);
        }
        var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing);
            await Wait(() => service.Snapshot().BridgeState == "Connected");
            await Send(client, hello, pairing); // Repeated sequence 1 must close this connection.
            await Wait(() => service.Snapshot().BridgeState == "Disconnected");
        }
    }
    [Fact]
    public async Task UnsupportedVersionDoesNotReportAutostartAsKnown()
    {
        await using var service = new ExamAwareService(_directory, new Target());
        var pairing = await Pairing(service);
        var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing, version: "1.4.3");
            await Wait(() => service.Snapshot().BridgeState == "UnsupportedVersion");
            Assert.Null(service.Snapshot().AutoStartRegistered);
        }
    }
    [Fact]
    public async Task OccupiedPortDoesNotSilentlyChangePairingAndRecoversOnRestart()
    {
        ExamAwarePairing pairing;
        await using (var service = new ExamAwareService(_directory, new Target())) pairing = await Pairing(service);
        using (var blocker = new TcpListener(IPAddress.Loopback, pairing.Port))
        {
            blocker.Server.ExclusiveAddressUse = true; blocker.Start();
            await using var blocked = new ExamAwareService(_directory, new Target());
            Assert.Equal("Unavailable", blocked.Snapshot().BridgeState);
        }
        await using var recovered = new ExamAwareService(_directory, new Target());
        Assert.Equal(pairing, await Pairing(recovered));
    }
    [Fact]
    public async Task CorruptStoreIsPreservedAndDoesNotReplaceCredentials()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "examaware.json");
        await File.WriteAllTextAsync(path, "broken");
        await using var service = new ExamAwareService(_directory, new Target());
        Assert.Equal("Unavailable", service.Snapshot().BridgeState);
        Assert.Equal("Rejected", (await service.HandleAsync(Request("examaware.pairing.get"))).Outcome);
        Assert.Equal("broken", await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void AutoStartRequiresExplicitBooleanAndRejectsParametersOnOtherCommands()
    {
        Assert.Equal("InvalidAutoStartParameters", Protocol.Validate(Request("examaware.autostart.set")));
        Assert.Null(Protocol.Validate(Request("examaware.autostart.set") with { AutoStartEnabled = false }));
        Assert.Null(Protocol.Validate(Request("examaware.autostart.set") with { AutoStartEnabled = true }));
        Assert.Equal("InvalidAutoStartParameters", Protocol.Validate(Request("examaware.quit") with { AutoStartEnabled = true }));
        Assert.Equal("InvalidConfiguration", Protocol.Validate(Request("examaware.autostart.set") with { AutoStartEnabled = true, ExpectedRevision = null }));
    }

    private static Task AutoStartReply(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing,
        Guid id, string state, bool? registered = null, long sequence = 2)
    {
        string payload = JsonSerializer.Serialize(new ExamAwareAutoStartAck(id, state, registered), Protocol.Json);
        return Protocol.WriteAsync(client.GetStream(), new ExamAwareFrame(2, "autostart.reply", sequence, payload,
            ExamAwareService.Sign(pairing.Key, ExamAwareService.FrameText("peer", hello.Nonce, hello.Proof, sequence, "autostart.reply", payload))), default);
    }
    [Fact]
    public async Task AutoStartRequiresPathConnectionAndNewPluginCapability()
    {
        await using var service = new ExamAwareService(_directory, new Target());
        var request = Request("examaware.autostart.set") with { AutoStartEnabled = true };
        Assert.Equal("PathMissing", (await service.HandleAsync(request)).ErrorCode);
        await Configure(service);
        Assert.Equal("RevisionConflict", (await service.HandleAsync(request with { ExpectedRevision = 0 })).ErrorCode);
        Assert.Equal("BridgeDisconnected", (await service.HandleAsync(request)).ErrorCode);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing); await Wait(() => service.Snapshot().BridgeState == "Connected");
            Assert.False(service.Snapshot().CanSetAutoStart);
            Assert.Equal("BridgeUpgradeRequired", (await service.HandleAsync(request)).ErrorCode);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoStartReadbackConfirmsBothValuesAndDurablyRejectsDuplicate(bool enabled)
    {
        var request = Request("examaware.autostart.set") with { AutoStartEnabled = enabled };
        await using (var service = new ExamAwareService(_directory, new Target()))
        {
            await Configure(service);
            var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
            using (client)
            {
                await Send(client, hello, pairing, registered: !enabled, canSetAutoStart: true);
                await Wait(() => service.Snapshot().CanSetAutoStart);
                Assert.Equal("Accepted", (await service.HandleAsync(request)).Outcome);
                Assert.Equal(!enabled, service.Snapshot().AutoStartRegistered); // Never optimistically change it.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var frame = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), deadline.Token);
                var command = JsonSerializer.Deserialize<ExamAwareAutoStartCommand>(frame.Payload, Protocol.Json)!;
                Assert.Equal("autostart.set", command.Action); Assert.Equal(enabled, command.Enabled);
                Assert.Equal(3000, command.ExpiresAt - command.IssuedAt);
                Assert.True(ExamAwareService.Verify(pairing.Key, ExamAwareService.FrameText("host", hello.Nonce, hello.Proof, 1, "command", frame.Payload), frame.Proof));
                foreach (string capability in new[] { "examaware.quit", "examaware.start", "examaware.pairing.reset", "examaware.autostart.set" })
                    Assert.Equal("AutoStartBusy", (await service.HandleAsync(Request(capability) with { AutoStartEnabled = capability == "examaware.autostart.set" ? enabled : null })).ErrorCode);
                await AutoStartReply(client, hello, pairing, request.RequestId, "Succeeded", enabled);
                await Wait(() => service.Snapshot().AutoStartChange?.State == "Succeeded");
                Assert.Equal(enabled, service.Snapshot().AutoStartRegistered);
                Assert.Equal(enabled, service.Snapshot().AutoStartChange!.Registered);
                Assert.Equal("AlreadyAccepted", (await service.HandleAsync(request)).ErrorCode);
            }
        }
        await using var restarted = new ExamAwareService(_directory, new Target());
        Assert.Equal("AlreadyAccepted", (await restarted.HandleAsync(request)).ErrorCode);
        Assert.Null(restarted.Snapshot().AutoStartChange);
    }
    [Theory]
    [InlineData("Denied", null, "Denied")]
    [InlineData("Failed", null, "Failed")]
    [InlineData("Expired", null, "Expired")]
    [InlineData("Unconfirmed", null, "Unconfirmed")]
    [InlineData("Mismatch", false, "Mismatch")]
    [InlineData("Succeeded", false, "Unconfirmed")]
    public async Task AutoStartNeverClaimsSuccessWithoutMatchingReadback(string reply, bool? registered, string expected)
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing, canSetAutoStart: true); await Wait(() => service.Snapshot().CanSetAutoStart);
            var request = Request("examaware.autostart.set") with { AutoStartEnabled = true };
            await service.HandleAsync(request);
            await AutoStartReply(client, hello, pairing, request.RequestId, reply, registered);
            await Wait(() => service.Snapshot().AutoStartChange?.State == expected);
        }
    }
    [Fact]
    public async Task AutoStartDisconnectIsUncertainAndDoesNotReplayOnReconnect()
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        await Send(client, hello, pairing, canSetAutoStart: true); await Wait(() => service.Snapshot().CanSetAutoStart);
        var request = Request("examaware.autostart.set") with { AutoStartEnabled = true };
        await service.HandleAsync(request); client.Dispose();
        await Wait(() => service.Snapshot().AutoStartChange?.State == "Unconfirmed");
        var (next, greeting) = await Connect(pairing);
        using (next)
        {
            await Send(next, greeting, pairing, canSetAutoStart: true); await Wait(() => service.Snapshot().CanSetAutoStart);
            await AutoStartReply(next, greeting, pairing, request.RequestId, "Succeeded", true);
            await Task.Delay(100);
            Assert.Equal("Unconfirmed", service.Snapshot().AutoStartChange!.State);
            Assert.Equal("AlreadyAccepted", (await service.HandleAsync(request)).ErrorCode);
            Assert.False(next.GetStream().DataAvailable);
        }
    }
    [Fact]
    public void ExamAwareRequestsRejectUnrelatedParameters()
    {
        Assert.Equal("UnexpectedParameters", Protocol.Validate(Request("examaware.start") with { ExecutablePath = "x" }));
        Assert.Equal("UnexpectedParameters", Protocol.Validate(Request("examaware.status") with { ObserveMs = 1 }));
    }
    private static async Task Reply(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing, Guid id, string state)
    {
        string payload = JsonSerializer.Serialize(new ExamAwareQuitAck(id, state), Protocol.Json);
        await Protocol.WriteAsync(client.GetStream(), new ExamAwareFrame(2, "reply", 2, payload,
            ExamAwareService.Sign(pairing.Key, ExamAwareService.FrameText("peer", hello.Nonce, hello.Proof, 2, "reply", payload))), default);
    }
    [Fact]
    public async Task QuitRequiresConfiguredAndConnectedPeer()
    {
        await using var service = new ExamAwareService(_directory, new Target());
        Assert.Equal("PathMissing", (await service.HandleAsync(Request("examaware.quit"))).ErrorCode);
        await Configure(service);
        Assert.Equal("BridgeDisconnected", (await service.HandleAsync(Request("examaware.quit"))).ErrorCode);
    }
    [Fact]
    public async Task QuitAcknowledgementAndDisconnectDoNotConfirmProcessExit()
    {
        var target = new Target();
        var quit = Request("examaware.quit");
        await using (var service = new ExamAwareService(_directory, target))
        {
            await Configure(service);
            var pairing = await Pairing(service);
            var (client, hello) = await Connect(pairing);
            using (client)
            {
                await Send(client, hello, pairing); await Wait(() => service.Snapshot().BridgeState == "Connected");
                Assert.Equal("Accepted", (await service.HandleAsync(quit)).Outcome);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var command = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), deadline.Token);
                Assert.True(ExamAwareService.Verify(pairing.Key, ExamAwareService.FrameText("host", hello.Nonce, hello.Proof, 1, "command", command.Payload), command.Proof));
                var payload = JsonSerializer.Deserialize<ExamAwareQuitCommand>(command.Payload, Protocol.Json)!;
                Assert.Equal(quit.RequestId, payload.RequestId); Assert.Equal(3000, payload.ExpiresAt - payload.IssuedAt);
                await Reply(client, hello, pairing, quit.RequestId, "Accepted");
                await Wait(() => service.Snapshot().Quit?.State == "AwaitingExit");
                Assert.Equal("QuitBusy", (await service.HandleAsync(Request("examaware.start"))).ErrorCode);
                Assert.Equal("AlreadyAccepted", (await service.HandleAsync(quit)).ErrorCode);
            }
            await Wait(() => service.Snapshot().BridgeState == "Disconnected");
            Assert.NotEqual("Exited", service.Snapshot().Quit!.State);
            target.Process.HasExited = true;
            await Wait(() => service.Snapshot().Quit?.State == "Exited");
            Assert.Equal(0, target.Starts);
        }
        await using var restarted = new ExamAwareService(_directory, target);
        Assert.Equal("AlreadyAccepted", (await restarted.HandleAsync(quit)).ErrorCode);
        Assert.Null(restarted.Snapshot().Quit); // No automatic replay after Host restart.
    }
    [Theory]
    [InlineData("Denied")]
    [InlineData("Expired")]
    [InlineData("Failed")]
    public async Task NegativeQuitReplyDoesNotClaimExit(string result)
    {
        await using var service = new ExamAwareService(_directory, new Target());
        await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing); await Wait(() => service.Snapshot().BridgeState == "Connected");
            var quit = Request("examaware.quit"); await service.HandleAsync(quit);
            await Reply(client, hello, pairing, quit.RequestId, result);
            await Wait(() => service.Snapshot().Quit?.State == result);
        }
    }
    [Fact]
    public async Task PairingRevocationClosesPeerAndRejectsOldCredential()
    {
        await using var service = new ExamAwareService(_directory, new Target());
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing); await Wait(() => service.Snapshot().BridgeState == "Connected");
            var reset = Request("examaware.pairing.reset");
            Assert.Equal("Succeeded", (await service.HandleAsync(reset)).Outcome);
            Assert.Equal("AlreadyAccepted", (await service.HandleAsync(reset)).ErrorCode);
            Assert.Equal("Disconnected", service.Snapshot().BridgeState);
            var next = await Pairing(service);
            Assert.Equal(pairing.Port, next.Port); Assert.NotEqual(pairing.Key, next.Key);
            using var old = new TcpClient(); await old.ConnectAsync(IPAddress.Loopback, next.Port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var greeting = await Protocol.ReadAsync<ExamAwareHello>(old.GetStream(), timeout.Token);
            Assert.False(ExamAwareService.Verify(pairing.Key, "host\n" + greeting.Nonce, greeting.Proof));
        }
    }
    [Fact]
    public void ProcessObservationRejectsDifferentPath()
    {
        var target = new ExamAwareTarget();
        Assert.Throws<NPEduTools.Core.LaunchTargetException>(() => target.Capture(Environment.ProcessId, @"C:\different\ExamAware.exe"));
        using var process = target.Capture(Environment.ProcessId, Environment.ProcessPath!);
        Assert.False(process.HasExited);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
