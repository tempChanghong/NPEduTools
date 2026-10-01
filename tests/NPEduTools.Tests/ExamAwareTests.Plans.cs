using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ExamAwareTests
{
    private const string PlanJson = "{\"examName\":\"期中考试\",\"message\":\"请保持安静\",\"examInfos\":[{\"name\":\"语文\",\"start\":\"2026-09-28T09:00:00\",\"end\":\"2026-09-28T11:00:00\",\"alertTime\":15}]}";
    private static HostRequest PlanRequest(string action = "prepare", Guid? preparation = null) => Request("examaware.plan") with {
        ExpectedRevision = 1, ExamPlan = new(action, action == "prepare" ? Convert.ToBase64String(Encoding.UTF8.GetBytes(PlanJson)) : null, preparation) };
    private static ExamAwarePlanSummary PlanSummary() => new(Guid.NewGuid(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlanJson))).ToLowerInvariant(),
        "期中考试", "请保持安静", [new("语文", "2026-09-28T09:00:00", "2026-09-28T11:00:00", 15)]);
    private static Task PlanFrame<T>(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing, long sequence, string type, T value)
    {
        string payload = JsonSerializer.Serialize(value, Protocol.Json);
        return Protocol.WriteAsync(client.GetStream(), new ExamAwareFrame(2, type, sequence, payload,
            ExamAwareService.Sign(pairing.Key, ExamAwareService.FrameText("peer", hello.Nonce, hello.Proof, sequence, type, payload))), default);
    }
    private static Task PlanSample(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing, long sequence = 1,
        ExamAwarePlayerStatus? player = null) => PlanFrame(client, hello, pairing, sequence, "status",
            new ExamAwareSample("ExamAware", "1.5.2", "win32", true, true, CanPresent: true,
                Player: player ?? new(true, [])));
    private static async Task<ExamAwarePlanWireCommand> ReadPlanCommand(TcpClient client, ExamAwareHello hello, ExamAwarePairing pairing)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frame = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), timeout.Token);
        Assert.True(ExamAwareService.Verify(pairing.Key, ExamAwareService.FrameText("host", hello.Nonce, hello.Proof,
            frame.Sequence, frame.Type, frame.Payload), frame.Proof));
        return JsonSerializer.Deserialize<ExamAwarePlanWireCommand>(frame.Payload, Protocol.Json)!;
    }
    [Fact]
    public void PlanInputIsBoundedUtf8JsonAndExclusiveToLocalCapability()
    {
        var request = PlanRequest();
        Assert.Null(Protocol.Validate(request));
        Assert.NotNull(Protocol.Validate(request with { Capability = "examaware.status" }));
        Assert.NotNull(Protocol.Validate(request with { ExpectedRevision = null }));
        foreach (var bad in new[] { "@@@", "e30=\n", Convert.ToBase64String([0xff, 0xff]), Convert.ToBase64String(Encoding.UTF8.GetBytes("[]")), new string('A', 32772) })
            Assert.False(ExamAwarePlanContract.Valid(new ExamAwarePlanInput("prepare", bad)));
        Assert.False(ExamAwarePlanContract.Valid(new ExamAwarePlanInput("start", PreparationId: Guid.Empty)));
        Assert.False(ExamAwarePlanContract.Valid(new ExamAwarePlanInput("prepare", request.ExamPlan!.DataBase64, Guid.NewGuid())));
        Assert.False(ExamAwarePlanContract.Valid(new ExamAwarePlayerStatus(false, [new("id", "ready", "x")])));
        // Valid UTF-8 below 24 KiB can still overflow nested JSON escaping; reject before IPC.
        string expanding = "{\"message\":\"" + new string('\uFFFE', 6000) + "\"}";
        Assert.True(Encoding.UTF8.GetByteCount(expanding) < ExamAwarePlanContract.MaxBytes);
        Assert.False(ExamAwarePlanContract.Valid(new ExamAwarePlanInput("prepare", Convert.ToBase64String(Encoding.UTF8.GetBytes(expanding)))));
    }
    [Fact]
    public async Task PrepareThenExplicitStartUsesAuthenticatedFramesAndHoldsRuntimeLease()
    {
        var target = new Target(); var gate = new RuntimeOperationGate();
        await using var service = new ExamAwareService(_directory, target, gate); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await PlanSample(client, hello, pairing); await Wait(() => service.Snapshot().CanPresent);
            var request = PlanRequest();
            Assert.Equal("Accepted", (await service.HandleAsync(request)).Outcome);
            Assert.Null(gate.TryReserveSwitch());
            Assert.Equal("PlanBusy", (await service.HandleAsync(Request("examaware.start"))).ErrorCode);
            var wire = await ReadPlanCommand(client, hello, pairing);
            Assert.Equal("plan.prepare", wire.Action); Assert.Equal(request.ExamPlan!.DataBase64, wire.DataBase64);
            Assert.Null(wire.PreparationId); Assert.Equal(3000, wire.ExpiresAt - wire.IssuedAt);
            var summary = PlanSummary();
            await PlanFrame(client, hello, pairing, 2, "plan.reply", new ExamAwarePlanAck(request.RequestId, "Prepared", summary));
            await Wait(() => service.Snapshot().PlanOperation?.State == "Prepared");
            Assert.Equal(summary.PreparationId, service.Snapshot().PreparedPlan!.PreparationId);
            Assert.Equal(0, target.Starts); // Preparing never launches the application/player.
            Assert.False(client.GetStream().DataAvailable);
            var start = PlanRequest("start", summary.PreparationId);
            Assert.Equal("Accepted", (await service.HandleAsync(start)).Outcome);
            Assert.Null(service.Snapshot().PreparedPlan);
            wire = await ReadPlanCommand(client, hello, pairing);
            Assert.Equal("plan.start", wire.Action); Assert.Null(wire.DataBase64); Assert.Equal(summary.PreparationId, wire.PreparationId);
            await PlanFrame(client, hello, pairing, 3, "plan.reply", new ExamAwarePlanAck(start.RequestId, "Started", SessionId: "player-1"));
            await Wait(() => service.Snapshot().PlanOperation?.State == "Started");
            Assert.Empty(service.Snapshot().Player!.Sessions); // Accepted is not readiness.
            await PlanSample(client, hello, pairing, 4, new(true, [new("player-1", "opening", "期中考试")]));
            await Wait(() => service.Snapshot().Player!.Sessions.Length == 1);
            Assert.Equal("opening", service.Snapshot().Player!.Sessions[0].State);
            await PlanSample(client, hello, pairing, 5, new(true, [new("player-1", "ready", "期中考试")]));
            await Wait(() => service.Snapshot().Player!.Sessions[0].State == "ready");
            Assert.Equal("AlreadyAccepted", (await service.HandleAsync(start)).ErrorCode);
            using var released = gate.TryReserveSwitch(); Assert.NotNull(released);
        }
    }
    [Fact]
    public async Task PreparedDataDoesNotSurviveReconnectAndWrongRevisionCannotDispatch()
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        var summary = PlanSummary();
        using (client)
        {
            await PlanSample(client, hello, pairing); await Wait(() => service.Snapshot().CanPresent);
            var request = PlanRequest();
            Assert.Equal("RevisionConflict", (await service.HandleAsync(request with { ExpectedRevision = 0 })).ErrorCode);
            await service.HandleAsync(request); await ReadPlanCommand(client, hello, pairing);
            await PlanFrame(client, hello, pairing, 2, "plan.reply", new ExamAwarePlanAck(request.RequestId, "Prepared", summary));
            await Wait(() => service.Snapshot().PreparedPlan is not null);
        }
        await Wait(() => service.Snapshot().BridgeState == "Disconnected"); Assert.Null(service.Snapshot().PreparedPlan);
        var (next, nextHello) = await Connect(pairing);
        using (next)
        {
            await PlanSample(next, nextHello, pairing); await Wait(() => service.Snapshot().CanPresent);
            Assert.Equal("PlanExpired", (await service.HandleAsync(PlanRequest("start", summary.PreparationId))).ErrorCode);
            Assert.False(next.GetStream().DataAvailable);
        }
    }

    [Fact]
    public async Task RemotePlanChecksConsentInsideQueueAndHashBeforeStarting()
    {
        var gate = new RuntimeOperationGate();
        await using var service = new ExamAwareService(_directory, new Target(), gate); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await PlanSample(client, hello, pairing); await Wait(() => service.Snapshot().CanPresent);
            await Assert.ThrowsAsync<RemoteExamException>(() => service.RemotePlanAsync(PlanRequest(), "unused",
                () => throw new RemoteExamException("POLICY_CHANGED"), default));
            Assert.False(client.GetStream().DataAvailable);
            var summary = PlanSummary(); var prepare = PlanRequest();
            var pending = service.RemotePlanAsync(prepare, summary.Sha256, () => { }, default);
            await ReadPlanCommand(client, hello, pairing); Assert.False(pending.IsCompleted); Assert.Null(gate.TryReserveSwitch());
            await PlanFrame(client, hello, pairing, 2, "plan.reply", new ExamAwarePlanAck(prepare.RequestId, "Prepared", summary));
            Assert.Equal("Prepared", (await pending).ExamAware!.PlanOperation!.State);
            var wrong = await Assert.ThrowsAsync<RemoteExamException>(() => service.RemotePlanAsync(PlanRequest("start", summary.PreparationId), new string('0', 64), () => { }, default));
            Assert.Equal("PLAN_EXPIRED", wrong.Code); Assert.False(client.GetStream().DataAvailable);
            using var released = gate.TryReserveSwitch(); Assert.NotNull(released);
        }
    }
    [Fact]
    public async Task HashMismatchDoesNotExposePreparedPlan()
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await PlanSample(client, hello, pairing); await Wait(() => service.Snapshot().CanPresent);
            var request = PlanRequest(); await service.HandleAsync(request); await ReadPlanCommand(client, hello, pairing);
            await PlanFrame(client, hello, pairing, 2, "plan.reply", new ExamAwarePlanAck(request.RequestId, "Prepared", PlanSummary() with { Sha256 = new string('0', 64) }));
            await Wait(() => service.Snapshot().PlanOperation?.State == "Unconfirmed");
            Assert.Null(service.Snapshot().PreparedPlan);
        }
    }
    [Theory]
    [InlineData(true, "PlayerBusy")]
    [InlineData(false, "PlayerUnknown")]
    public async Task ExistingOrUnknownPlayerNeverReceivesStart(bool known, string code)
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await PlanSample(client, hello, pairing, player: new(known, known ? [new("existing", "ready", "已有考试")] : []));
            await Wait(() => service.Snapshot().CanPresent);
            var request = PlanRequest(); await service.HandleAsync(request); await ReadPlanCommand(client, hello, pairing);
            var summary = PlanSummary();
            await PlanFrame(client, hello, pairing, 2, "plan.reply", new ExamAwarePlanAck(request.RequestId, "Prepared", summary));
            await Wait(() => service.Snapshot().PreparedPlan is not null);
            Assert.Equal(code, (await service.HandleAsync(PlanRequest("start", summary.PreparationId))).ErrorCode);
            Assert.False(client.GetStream().DataAvailable);
        }
    }
    [Fact]
    public async Task OldBridgeIsConnectedButCannotPresent()
    {
        await using var service = new ExamAwareService(_directory, new Target()); await Configure(service);
        var pairing = await Pairing(service); var (client, hello) = await Connect(pairing);
        using (client)
        {
            await Send(client, hello, pairing); await Wait(() => service.Snapshot().BridgeState == "Connected");
            Assert.Equal("BridgeUpgradeRequired", (await service.HandleAsync(PlanRequest())).ErrorCode);
            Assert.False(client.GetStream().DataAvailable);
        }
    }
}
