using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ExamAwareTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteExamStartupUnderExclusiveLeaseWaitsForBridgeReadback(bool disconnect)
    {
        var gate = new RuntimeOperationGate();
        await using var exam = new ExamAwareService(_directory, new Target(), gate);
        await Configure(exam);
        var pairing = await Pairing(exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing, registered: false, canSetAutoStart: true);
        await Wait(() => exam.Snapshot().CanSetAutoStart);
        Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            exam.SetStartupUnderRuntimeLeaseAsync(1, true, () => Task.CompletedTask))).Code);
        using var lease = gate.TryReserveSwitch(); Assert.NotNull(lease);
        // Inspection takes the same command semaphore: proves the callback cannot deadlock it.
        var pending = exam.SetStartupUnderRuntimeLeaseAsync(1, true,
            () => exam.VerifyConnectedProcessAsync(@"C:\中文 目录\ExamAware.exe", 1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var frame = await Protocol.ReadAsync<ExamAwareFrame>(client.GetStream(), timeout.Token);
        var command = System.Text.Json.JsonSerializer.Deserialize<ExamAwareAutoStartCommand>(frame.Payload, Protocol.Json)!;
        Assert.False(pending.IsCompleted);
        Assert.Null(gate.TryEnterMutation());
        if (disconnect)
        {
            client.Dispose();
            Assert.Equal("STARTUP_NOT_READY", (await Assert.ThrowsAsync<RemoteExamException>(() => pending.WaitAsync(timeout.Token))).Code);
        }
        else
        {
            await AutoStartReply(client, hello, pairing, command.RequestId, "Succeeded", true);
            await pending.WaitAsync(timeout.Token);
            Assert.True(exam.Snapshot().AutoStartRegistered);
        }
    }

    // Real stores, executor, local mode service and ExamAware TCP bridge; OS startup writes are simulated.
    private sealed class RemoteModeEffects : IClassroomModeEffects, IRemoteClassroomModeEffects
    {
        public ClassroomStartupSnapshot Actual = new(@"C:\Test\ClassIsland.exe", 1, true,
            @"C:\中文 目录\ExamAware.exe", 1, false);
        public readonly List<string> Writes = [];
        public Action? AfterWrite;
        public bool FailDisable;
        public bool TaskMissing;
        public int Pauses;
        public bool DailyReady, FailDailyStart;
        public Action? OnDailyStart;
        public int DailyStarts;
        public Task<ClassroomStartupSnapshot> ObserveDailyStartupAsync(CancellationToken token) => Task.FromResult(Actual);
        public Task<bool> ClassIslandReadyAsync(CancellationToken token) => Task.FromResult(DailyReady);
        public async Task StartClassIslandRemoteAsync(ClassroomStartupSnapshot expected, Func<Task> check, CancellationToken token)
        {
            await check();
            if (FailDailyStart) throw new RemoteExamException("CLASSISLAND_NOT_READY");
            if (!DailyReady) { DailyStarts++; OnDailyStart?.Invoke(); }
            DailyReady = true;
        }
        public Task<ClassroomStartupSnapshot> ObserveAsync(bool connect) => Task.FromResult(Actual);
        public Task PauseRecordingAsync() { Pauses++; return Task.CompletedTask; }
        public Task ValidateRemoteAsync(CancellationToken token) => Task.CompletedTask;
        public Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled)
        {
            if (!enabled && FailDisable) throw new RemoteExamException("STARTUP_NOT_READY", "ClassIsland 管理员任务：fixture 拒绝关闭。");
            Actual = Actual with { ClassIslandEnabled = enabled };
            Writes.Add("ClassIsland=" + enabled); AfterWrite?.Invoke(); return Task.CompletedTask;
        }
        public Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled)
        {
            Actual = Actual with { ExamAwareEnabled = enabled };
            Writes.Add("ExamAware=" + enabled); AfterWrite?.Invoke(); return Task.CompletedTask;
        }
        public async Task SetClassIslandRemoteAsync(ClassroomStartupSnapshot expected, bool enabled, Func<Task> check)
        { await check(); await SetClassIslandAsync(expected, enabled); }
        public async Task SetExamAwareRemoteAsync(ClassroomStartupSnapshot expected, bool enabled, Func<Task> check)
        { await check(); await SetExamAwareAsync(expected, enabled); }
    }

    [Fact]
    public async Task RemoteExamModeCommitsStartupAndModeSurvivesRestartAndRequiresLocalDailyBeforeRelease()
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        var result = await f.RunAsync(true);
        Assert.Equal("SUCCEEDED", result.Outcome);
        Assert.Equal(new[] { "ExamAware=True", "ClassIsland=False" }, f.ModeEffects.Writes);
        Assert.Equal(1, f.Platform.Closes);
        Assert.Equal(0, f.ModeEffects.Pauses); // Remote must never call the local stop-recording routine.
        Assert.Equal("Exam", f.Classroom.Snapshot.Mode);
        Assert.True(f.Classroom.Snapshot.AutomaticPaused);
        Assert.Null(f.Classroom.Snapshot.Recovery);
        var restarted = new ClassroomModeStore(_directory);
        Assert.Equal("Exam", restarted.State.Mode);
        Assert.Equal("Idle", restarted.State.Phase);
        Assert.Equal(result, await f.Executor.RunAsync(result.Intent, f.Authorization));
        Assert.Equal(2, f.ModeEffects.Writes.Count);
        Assert.Equal("DAILY_MODE_REQUIRED", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision))).Code);

        var local = f.Classroom.Handle(new(Protocol.Version, Guid.NewGuid(), "classroom.set",
            ExpectedRevision: f.Classroom.Snapshot.Revision, ClassroomMode: new("Daily")));
        Assert.Equal("Accepted", local.Outcome);
        await Wait(() => !f.Classroom.Busy && !f.Gate.Switching);
        Assert.Equal("Daily", f.Classroom.Snapshot.Mode);
        Assert.True(f.Store.State.AutomaticPaused); // Daily alone cannot silently clear N3 protection.
        await f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision);
        Assert.False(f.Store.State.AutomaticPaused);
        Assert.Equal("SUCCEEDED", f.Store.State.Operations!.Single().Outcome);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("revoked")]
    [InlineData("close")]
    public async Task RemoteExamModeFailureRetainsOriginalBaselineUntilExplicitLocalRestore(string failure)
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        var baseline = f.ModeEffects.Actual;
        f.ModeEffects.FailDisable = failure == "startup";
        if (failure == "revoked") f.ModeEffects.AfterWrite = () => f.Authorization.Revoked = () => true;
        if (failure == "close") f.Platform.OnClose = () => throw new RemoteExamException("CLASSISLAND_EXIT_UNAVAILABLE");
        var result = await f.RunAsync(true);
        Assert.Equal("PARTIAL", result.Outcome);
        Assert.Equal("Incomplete", f.Classroom.Snapshot.Phase);
        if (failure == "startup") Assert.Contains("fixture 拒绝关闭", f.Classroom.Snapshot.Message);
        Assert.Equal(baseline, f.Classroom.Snapshot.Recovery!.Startup);
        Assert.Equal(0, f.Platform.Closes);
        Assert.Equal(0, f.ModeEffects.Pauses);
        Assert.True(f.Store.State.AutomaticPaused);
        Assert.Equal(baseline, new ClassroomModeStore(_directory).State.Recovery!.Startup);
        var count = f.ModeEffects.Writes.Count;
        Assert.Equal(result, await f.Executor.RunAsync(result.Intent, f.Authorization));
        Assert.Equal(count, f.ModeEffects.Writes.Count);
        Assert.Equal("RECOVERY_REQUIRED", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision))).Code);

        f.ModeEffects.FailDisable = false; f.ModeEffects.AfterWrite = null;
        var local = f.Classroom.Handle(new(Protocol.Version, Guid.NewGuid(), "classroom.restore",
            ExpectedRevision: f.Classroom.Snapshot.Revision));
        Assert.Equal("Accepted", local.Outcome);
        await Wait(() => !f.Classroom.Busy);
        Assert.Equal(baseline, f.ModeEffects.Actual);
        Assert.Null(f.Classroom.Snapshot.Recovery);
        await f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision);
        Assert.False(f.Store.State.AutomaticPaused);
        Assert.Equal("PARTIAL", f.Store.State.Operations!.Single().Outcome);
    }

    [Fact]
    public async Task RemoteExamModeMissingTaskAlreadySatisfiesDisabledStartup()
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync(); f.ModeEffects.TaskMissing = true;
        f.ModeEffects.Actual = f.ModeEffects.Actual with { ClassIslandEnabled = false };
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        await f.Executor.PreflightAsync(switchMode: true);
        var result = await f.RunAsync(true);
        Assert.Equal("SUCCEEDED", result.Outcome);
        Assert.True(f.Store.State.AutomaticPaused);
        Assert.Equal(new[] { "ExamAware=True" }, f.ModeEffects.Writes);
    }

    [Fact]
    public async Task RemoteExamModeRestartAfterOneWriteKeepsBothPausesAndDoesNotReplay()
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        string crash = Path.Combine(_directory, "crash-snapshot");
        f.ModeEffects.AfterWrite = () =>
        {
            Directory.CreateDirectory(crash);
            // Capture exactly the durable state a new Host would see at this boundary.
            foreach (string path in Directory.GetFiles(_directory, "*.json"))
                File.Copy(path, Path.Combine(crash, Path.GetFileName(path)));
            throw new RemoteExamException("HOST_INTERRUPTED");
        };
        await f.RunAsync(true);
        var modes = new ClassroomModeStore(crash);
        var remote = new RemoteExamStore(crash);
        Assert.Equal("Incomplete", modes.State.Phase);
        Assert.NotNull(modes.State.Recovery);
        Assert.True(modes.State.AutomaticPaused);
        Assert.True(remote.State.AutomaticPaused);
        Assert.Equal("UNKNOWN", remote.State.Operations!.Single().Outcome);
        Assert.True(remote.State.Operations!.Single().Intent.SwitchMode);
        Assert.Single(f.ModeEffects.Writes);
        Assert.Equal(0, f.Platform.Closes);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("close")]
    public async Task RemoteExamModeFreshRequestResumesPartialWithoutLocalRestore(string failure)
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        f.ModeEffects.FailDisable = failure == "startup";
        if (failure == "close") f.Platform.OnClose = () => throw new RemoteExamException("CLASSISLAND_EXIT_UNAVAILABLE");
        var first = await f.RunAsync(true);
        Assert.Equal("PARTIAL", first.Outcome);
        f.ModeEffects.FailDisable = false; f.Platform.OnClose = null;
        var second = await f.RunAsync(true);
        Assert.Equal("SUCCEEDED", second.Outcome); Assert.Equal("Exam", f.Classroom.Snapshot.Mode);
        Assert.Null(f.Classroom.Snapshot.Recovery); Assert.Equal(1, f.Platform.Closes);
        Assert.Equal(1, f.ModeEffects.Writes.Count(x => x == "ExamAware=True"));
        Assert.Equal("PARTIAL", f.Store.State.Operations!.First().Outcome);
        Assert.Equal(second.Intent.OperationId, f.Store.State.PauseOperationId);
    }

    [Fact]
    public async Task RemoteExamTransportReportsStartupDriftAndKeepsHistoricalReceiptAfterLocalEnd()
    {
        await using var f = new RemoteFixture(_directory, true);
        await f.ConfigureAsync();
        var pairing = await Pairing(f.Exam); var (client, hello) = await Connect(pairing);
        using var peer = client;
        await Send(client, hello, pairing); await Wait(() => f.Exam.Snapshot().BridgeState == "Connected");
        f.Platform.ExamRunning = true;
        var entry = await f.RunAsync(true);
        var actions = new RemoteExamActions(f.Launch, f.Exam, f.Recording, f.Classroom, f.Platform);
        var transport = new RemoteExamTransport(f.Executor, actions, f.Recording, f.Classroom);
        Assert.Equal("EXAM", (await transport.ObserveAsync(default))["runtimeMode"]!.GetValue<string>());
        f.ModeEffects.Actual = f.ModeEffects.Actual with { ClassIslandEnabled = true };
        Assert.Equal("OTHER", (await transport.ObserveAsync(default))["runtimeMode"]!.GetValue<string>());
        Assert.Equal("SUCCEEDED", (await f.RunAsync(true)).Outcome);
        Assert.False(f.ModeEffects.Actual.ClassIslandEnabled);
        // A late result must describe its own completion, not today's cleared pause.
        f.Store.Save(f.Store.State with { AutomaticPaused = false, PauseOperationId = null });
        var result = await transport.ResultAsync(entry.Intent.OperationId, default);
        Assert.Equal("READY", result["evidence"]!["examAware"]!.GetValue<string>());
        Assert.True(result["evidence"]!["remoteExamPause"]!.GetValue<bool>());
    }
}
