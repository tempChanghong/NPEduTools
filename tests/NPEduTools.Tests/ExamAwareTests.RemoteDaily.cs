using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ExamAwareTests
{
    [Theory]
    [InlineData("presenting")]
    [InlineData("denied")]
    [InlineData("revoked")]
    public async Task RemoteDailyNeverClaimsSuccessWhenExitIsBlockedOrAuthorityRevoked(string blocker)
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await PlanSample(client, hello, pairing, player: new(true,
            blocker == "presenting" ? [new("active", "ready", "考试")] : []));
        await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        if (blocker == "revoked") f.ModeEffects.AfterWrite = () => f.Authorization.Revoked = () => true;
        f.ModeEffects.Actual = f.ModeEffects.Actual with { ClassIslandEnabled = false, ExamAwareEnabled = true };
        var pending = f.Executor.RunAsync(new(Guid.NewGuid(), 0, f.Classroom.Snapshot.Revision, true, "Daily"), f.Authorization);
        if (blocker == "denied")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var frame = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), timeout.Token);
            var command = System.Text.Json.JsonSerializer.Deserialize<ExamAwareQuitCommand>(frame.Payload, Protocol.Json)!;
            await Reply(client, hello, pairing, command.RequestId, "Denied");
        }
        var result = await pending;
        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal(blocker switch { "presenting" => "EXAMAWARE_PRESENTING", "revoked" => "AUTH_REVOKED", _ => "EXAMAWARE_EXIT_FAILED" }, result.Reason);
        Assert.True(f.Store.State.AutomaticPaused); Assert.True(f.Classroom.Snapshot.AutomaticPaused);
        Assert.Equal(0, f.ModeEffects.DailyStarts); Assert.False(f.Target.Process.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteDailyWaitsForRealBridgeQuitAndReadinessThenClearsBothPauses(bool failFirstStart)
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        Assert.Equal("SUCCEEDED", (await f.RunAsync(true)).Outcome);
        var examEntry = f.Store.State.Operations!.Single();
        f.ModeEffects.FailDailyStart = failFirstStart;
        f.ModeEffects.OnDailyStart = () => f.Platform.Process = new("Administrator", 124, 457);
        RemoteExamIntent Intent() => new(Guid.NewGuid(), f.Store.State.Revision, f.Classroom.Snapshot.Revision, true, "Daily");
        var intent = Intent();
        var pending = f.Executor.RunAsync(intent, f.Authorization);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frame = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), timeout.Token);
        var command = System.Text.Json.JsonSerializer.Deserialize<ExamAwareQuitCommand>(frame.Payload, Protocol.Json)!;
        Assert.Equal("quit", command.Action);
        Assert.True(f.Store.State.AutomaticPaused);
        Assert.True(f.Classroom.Snapshot.AutomaticPaused);
        Assert.Equal(0, f.ModeEffects.DailyStarts);
        f.Target.Process.HasExited = true; f.Platform.ExamRunning = false; client.Dispose();
        var result = await pending.WaitAsync(timeout.Token);
        if (failFirstStart)
        {
            Assert.Equal("PARTIAL", result.Outcome); Assert.Equal("CLASSISLAND_NOT_READY", result.Reason);
            Assert.True(f.Store.State.AutomaticPaused); Assert.True(f.Classroom.Snapshot.AutomaticPaused);
            f.ModeEffects.FailDailyStart = false;
            result = await f.Executor.RunAsync(Intent(), f.Authorization);
        }
        Assert.Equal("SUCCEEDED", result.Outcome);
        Assert.Equal("Daily", f.Classroom.Snapshot.Mode);
        Assert.False(f.Store.State.AutomaticPaused); Assert.False(f.Classroom.Snapshot.AutomaticPaused);
        Assert.Null(f.Store.State.PauseOperationId);
        Assert.Equal(1, f.ModeEffects.DailyStarts); Assert.Equal(0, f.Target.Starts);
        var transport = new RemoteExamTransport(f.Executor,
            new RemoteExamActions(f.Launch, f.Exam, f.Recording, f.Classroom, f.Platform), f.Recording, f.Classroom);
        var receipt = await transport.ResultAsync(result.Intent.OperationId, default);
        Assert.Equal("DAILY_MODE_APPLIED", receipt["evidence"]!["startup"]!.GetValue<string>());
        Assert.False(receipt["evidence"]!["remoteExamPause"]!.GetValue<bool>());
        Assert.Equal("DAILY", (await transport.ObserveAsync(default))["runtimeMode"]!.GetValue<string>());
        Assert.True((await transport.ResultAsync(examEntry.Intent.OperationId, default))["evidence"]!["remoteExamPause"]!.GetValue<bool>());
        var repeated = await f.Executor.RunAsync(Intent(), f.Authorization);
        Assert.True(repeated.AlreadySatisfied); Assert.Equal("SUCCEEDED", repeated.Outcome);
        Assert.Equal(1, f.ModeEffects.DailyStarts); Assert.Equal(0, f.Target.Starts);
        var restarted = new RemoteExamStore(_directory);
        Assert.False(restarted.State.AutomaticPaused);
        Assert.Equal("Daily", new ClassroomModeStore(_directory).State.Mode);
        Assert.Equal(result, await f.Executor.RunAsync(result.Intent, f.Authorization));
        // A delayed duplicate of the preceding Exam must remain historical after Daily,
        // including when a new Host reconstructs its pause ledger.
        var restartedExecutor = new RemoteExamExecutor(restarted, new(),
            new RemoteExamActions(f.Launch, f.Exam, f.Recording, f.Classroom, f.Platform));
        var writes = f.ModeEffects.Writes.Count;
        Assert.Equal(examEntry, await restartedExecutor.RunAsync(examEntry.Intent, f.Authorization));
        Assert.False(restarted.State.AutomaticPaused);
        Assert.Equal("Daily", f.Classroom.Snapshot.Mode);
        Assert.Equal(writes, f.ModeEffects.Writes.Count);
        Assert.Equal(1, f.ModeEffects.DailyStarts);
        Assert.Equal(0, f.Target.Starts);
    }
}
