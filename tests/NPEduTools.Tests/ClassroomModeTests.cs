using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed partial class ClassroomModeTests
{
    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "NPEduTools-modes-" + Guid.NewGuid().ToString("N"));
        public ClassroomModeStore Store { get; }
        public Effects Effects { get; }
        public ClassroomModeService Service { get; }
        public Fixture()
        {
            Store = new(DirectoryPath); Effects = new(Store);
            Service = new(Store, Effects);
        }
        public HostResponse Send(string capability = "classroom.set", string? target = "Exam", Guid? id = null, bool running = false) =>
            Service.Handle(new(1, id ?? Guid.NewGuid(), capability,
                ExpectedRevision: capability is "classroom.set" or "classroom.restore" or "classroom.retry" ? Store.State.Revision : null,
                ClassroomMode: capability is "classroom.set" or "classroom.retry" ? new(target!, running, running && target == "Daily") : null));
        public async Task Finish()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Service.Busy) await Task.Delay(10, deadline.Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
        }
    }
    private sealed class Effects(ClassroomModeStore store) : IClassroomModeEffects
    {
        public ClassroomStartupSnapshot Actual = new(@"C:\CI\ClassIsland.exe", 1, true, @"C:\EA\ExamAware.exe", 2, false);
        public List<string> Calls = [];
        public string? Fail;
        public bool Lie, ChangeIdentity;
        public TaskCompletionSource? PauseBlock;
        public RuntimeActions RuntimeActions = new();
        public Task RunRuntimeAsync(ClassroomRuntimeIntent intent, Action<string, string> progress) =>
            new ClassroomRuntimeCoordinator(RuntimeActions).RunAsync(intent, (step, message) =>
            {
                progress(step, message);
                Assert.True(store.State.AutomaticPaused);
                Assert.NotNull(store.State.Recovery);
            });
        public Task<ClassroomStartupSnapshot> ObserveAsync(bool connect)
        {
            Calls.Add(connect ? "connect" : "observe");
            if (Fail == "observe") throw new InvalidOperationException("bridge offline");
            return Task.FromResult(ChangeIdentity && Calls.Contains("ci:False") ? Actual with { ExamAwareRevision = 3 } : Actual);
        }
        private void Check(string call)
        {
            Calls.Add(call);
            Assert.True(store.State.AutomaticPaused);
            Assert.NotNull(store.State.Recovery);
            if (Fail == call) throw new InvalidOperationException("cancelled or lost acknowledgement");
        }
        public Task SetClassIslandAsync(ClassroomStartupSnapshot expected, bool enabled)
        {
            Check("ci:" + enabled);
            if (!Lie) Actual = Actual with { ClassIslandEnabled = enabled };
            return Task.CompletedTask;
        }
        public Task SetExamAwareAsync(ClassroomStartupSnapshot expected, bool enabled)
        {
            Check("ea:" + enabled);
            if (!Lie) Actual = Actual with { ExamAwareEnabled = enabled };
            return Task.CompletedTask;
        }
        public Task PauseRecordingAsync() { Check("pause"); return PauseBlock?.Task ?? Task.CompletedTask; }
    }

    [Fact]
    public async Task DailyExamDailyEnablesDestinationFirstAndPersistsPause()
    {
        await using var f = new Fixture();
        Assert.Equal("Accepted", f.Send().Outcome); await f.Finish();
        Assert.Equal(new[] { "connect", "pause", "ea:True", "ci:False", "observe" }, f.Effects.Calls);
        Assert.Equal("Exam", f.Store.State.Mode); Assert.True(f.Store.State.AutomaticPaused);
        Assert.True(new ClassroomModeStore(f.DirectoryPath).State.AutomaticPaused);
        f.Effects.Calls.Clear();
        Assert.Equal("Accepted", f.Send(target: "Daily").Outcome); await f.Finish();
        Assert.Equal(new[] { "connect", "pause", "ci:True", "ea:False", "observe" }, f.Effects.Calls);
        Assert.Equal("Daily", f.Store.State.Mode); Assert.False(f.Store.State.AutomaticPaused);
        Assert.True(f.Store.State.MatchesMode); Assert.Null(f.Store.State.Recovery);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("ea:True")]
    [InlineData("ci:False")]
    public async Task FailureKeepsRecoveryAndExplicitRestoreReturnsOriginalSettings(string failedCall)
    {
        await using var f = new Fixture();
        f.Effects.Fail = failedCall;
        f.Send(); await f.Finish();
        Assert.Equal("Incomplete", f.Store.State.Phase);
        Assert.Equal("Unconfigured", f.Store.State.Mode); Assert.True(f.Store.State.AutomaticPaused);
        Assert.NotNull(f.Store.State.Recovery);
        Assert.Equal("RestoreRequired", f.Send().ErrorCode);
        f.Effects.Fail = null;
        Assert.Equal("Accepted", f.Send("classroom.restore").Outcome); await f.Finish();
        Assert.Equal("Idle", f.Store.State.Phase); Assert.False(f.Store.State.AutomaticPaused);
        Assert.True(f.Effects.Actual.ClassIslandEnabled); Assert.False(f.Effects.Actual.ExamAwareEnabled);
        Assert.Null(f.Store.State.Recovery);
    }

    [Fact]
    public async Task PreflightFailureLeavesRecordingEligibilityAndStartupUntouched()
    {
        await using var f = new Fixture(); f.Effects.Fail = "observe";
        f.Send(); await f.Finish();
        Assert.False(f.Store.State.AutomaticPaused); Assert.Null(f.Store.State.Recovery);
        Assert.Equal(new[] { "connect" }, f.Effects.Calls);
    }
    [Fact]
    public async Task WrongReadbackAndIdentityChangesNeverCommit()
    {
        await using var f = new Fixture(); f.Effects.Lie = true;
        f.Send(); await f.Finish();
        Assert.Equal("Incomplete", f.Store.State.Phase); Assert.True(f.Store.State.AutomaticPaused);
        f.Effects.Lie = false; f.Send("classroom.restore"); await f.Finish();
        f.Effects.Calls.Clear(); f.Effects.ChangeIdentity = true; f.Send(); await f.Finish();
        Assert.Equal("Incomplete", f.Store.State.Phase);
        f.Effects.Actual = f.Effects.Actual with { ExamAwareRevision = 3 };
        f.Effects.Calls.Clear();
        f.Send("classroom.restore"); await f.Finish();
        Assert.Equal(new[] { "connect" }, f.Effects.Calls);
        Assert.NotNull(f.Store.State.Recovery); Assert.True(f.Store.State.AutomaticPaused);
    }
    [Fact]
    public async Task ConcurrentRequestsShutdownAndDuplicateReplayAreRejected()
    {
        await using var f = new Fixture(); f.Effects.PauseBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Guid id = Guid.NewGuid();
        f.Send(id: id);
        Assert.Equal("ClassroomBusy", f.Send().ErrorCode);
        Assert.False(f.Service.BeginShutdown());
        f.Effects.PauseBlock.SetResult(); await f.Finish();
        Assert.Equal("AlreadyHandled", f.Send(id: id).ErrorCode);
        await using var restarted = new ClassroomModeService(new(f.DirectoryPath), f.Effects);
        Assert.Equal("AlreadyHandled", restarted.Handle(new(1, id, "classroom.set", ExpectedRevision: f.Store.State.Revision,
            ClassroomMode: new("Daily"))).ErrorCode);
        Assert.True(f.Service.BeginShutdown());
        Assert.Equal("ClassroomBusy", f.Send().ErrorCode);
    }
    [Fact]
    public async Task RefreshDetectsDriftWithoutStartingProgramsOrApplyingChanges()
    {
        await using var f = new Fixture(); f.Send(); await f.Finish();
        f.Effects.Actual = f.Effects.Actual with { ClassIslandEnabled = true }; f.Effects.Calls.Clear();
        f.Send("classroom.refresh"); await f.Finish();
        Assert.Equal(new[] { "observe" }, f.Effects.Calls);
        Assert.False(f.Store.State.MatchesMode); Assert.Equal("Exam", f.Store.State.Mode); Assert.True(f.Store.State.AutomaticPaused);
    }
    [Fact]
    public async Task LostBridgeDoesNotEraseRecoveryRecord()
    {
        await using var f = new Fixture(); f.Effects.Fail = "ci:False"; f.Send(); await f.Finish();
        var recovery = f.Store.State.Recovery;
        f.Effects.Fail = "observe"; f.Send("classroom.refresh"); await f.Finish();
        Assert.Equal(recovery, f.Store.State.Recovery); Assert.Equal("Incomplete", f.Store.State.Phase);
    }
    [Theory]
    [InlineData("Checking")]
    [InlineData("Switching")]
    public async Task InterruptedOperationStartsPausedAndNeverReplays(string phase)
    {
        await using var f = new Fixture();
        f.Store.Save(new(Phase: phase, RecentRequests: [], Recovery: new("Daily", false, f.Effects.Actual)));
        var loaded = new ClassroomModeStore(f.DirectoryPath);
        Assert.Equal("Incomplete", loaded.State.Phase); Assert.True(loaded.State.AutomaticPaused);
        Assert.NotNull(loaded.State.Recovery); Assert.Empty(f.Effects.Calls);
    }
    [Fact]
    public async Task DamagedStoreIsPreservedAndCannotResumeRecording()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(f.DirectoryPath);
        string path = Path.Combine(f.DirectoryPath, "classroom-mode.json"); File.WriteAllText(path, "{broken");
        var damaged = new ClassroomModeStore(f.DirectoryPath);
        Assert.True(damaged.State.AutomaticPaused); Assert.Equal("Unavailable", damaged.State.Phase);
        Assert.Equal("{broken", File.ReadAllText(path));
        await using var service = new ClassroomModeService(damaged, f.Effects);
        Assert.Equal("ClassroomStorageUnavailable", service.Handle(new(1, Guid.NewGuid(), "classroom.set",
            ExpectedRevision: 0, ClassroomMode: new("Daily"))).ErrorCode);
    }
    [Fact]
    public async Task DiskWriteFailurePreventsExternalWrites()
    {
        await using var f = new Fixture(); Directory.CreateDirectory(f.DirectoryPath);
        Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "classroom-mode.json.pending"));
        Assert.Equal("Failed", f.Send().Outcome);
        Assert.True(f.Store.State.AutomaticPaused); Assert.Empty(f.Effects.Calls);
    }
    [Fact]
    public async Task ModePauseDoesNotRewriteRecordingPlansOrEnableDisabledRecorder()
    {
        await using var f = new Fixture();
        string pipe = "NPEduTools.Test.mode." + Guid.NewGuid();
        await using var recorder = new RecordingService(pipe, f.DirectoryPath,
            () => SchoolClockFrame.Unavailable("test"), () => f.Store.State.AutomaticPaused);
        f.Store.Save(new(Mode: "Exam", AutomaticPaused: true, RecentRequests: []));
        await recorder.PauseForClassroomModeAsync();
        Assert.True(recorder.Automatic.SuspendedByMode); Assert.False(recorder.Automatic.Enabled);
        Assert.Equal("Idle", recorder.State.Phase); Assert.Empty(recorder.Automatic.Recent);
        f.Store.Save(f.Store.State with { Mode = "Daily", AutomaticPaused = false });
        Assert.False(recorder.Automatic.SuspendedByMode); Assert.False(recorder.Automatic.Enabled);
    }
    [Fact]
    public void ModeProtocolIsStrictAndRoundTrips()
    {
        var request = new HostRequest(1, Guid.NewGuid(), "classroom.set", ExpectedRevision: 0, ClassroomMode: new("Exam"));
        Assert.Null(Protocol.Validate(request));
        Assert.Equal(request, JsonSerializer.Deserialize<HostRequest>(JsonSerializer.Serialize(request, Protocol.Json), Protocol.Json));
        Assert.NotNull(Protocol.Validate(request with { ExpectedRevision = null }));
        Assert.NotNull(Protocol.Validate(request with { ClassroomMode = new("unknown") }));
        Assert.NotNull(Protocol.Validate(request with { AutoStartEnabled = true }));
        Assert.NotNull(Protocol.Validate(request with { Capability = "host.ping" }));
        Assert.NotNull(Protocol.Validate(request with { ExecutablePath = "wrong" }));
        Assert.NotNull(Protocol.Validate(request with { ObserveMs = 1 }));
    }
}
