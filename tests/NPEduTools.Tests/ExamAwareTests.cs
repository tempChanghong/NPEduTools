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
    }
    private static HostRequest Request(string capability) => new(1, Guid.NewGuid(), capability);
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
        return (client, hello);
    }
    private static Task Send(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing,
        long sequence = 1, string version = "1.5.2", bool? registered = true, bool badProof = false)
    {
        string payload = JsonSerializer.Serialize(new ExamAwareSample("ExamAware", version, "win32", true, registered), Protocol.Json);
        return Protocol.WriteAsync(client.GetStream(), new ExamAwareFrame(1, "status", sequence, payload,
            badProof ? new string('0', 64) : ExamAwareService.Sign(pairing.Key, $"peer\n{hello.Nonce}\n{sequence}\n{payload}")), default);
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
    [Theory]
    [InlineData("examaware.quit")]
    [InlineData("examaware.autostart.set")]
    public void MutationCapabilitiesAreNotExposed(string capability) => Assert.Equal("UnknownCapability", Protocol.Validate(Request(capability)));
    [Fact]
    public void ExamAwareRequestsRejectUnrelatedParameters()
    {
        Assert.Equal("UnexpectedParameters", Protocol.Validate(Request("examaware.start") with { ExecutablePath = "x" }));
        Assert.Equal("UnexpectedParameters", Protocol.Validate(Request("examaware.status") with { ObserveMs = 1 }));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
