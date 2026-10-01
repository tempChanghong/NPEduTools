using System.Text.Json;
using NPEduTools.Contracts;
using NPEduTools.Host;

namespace NPEduTools.Tests;

public sealed class RemoteExamTests
{
    private sealed class Authorization : IRemoteExamAuthorization
    {
        public string? Failure;
        public bool PermitExpired;
        public Action? BeforeFirstEffect;
        public Task CheckAsync(bool firstEffect, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Failure is { } code) throw new RemoteExamException(code);
            if (firstEffect && PermitExpired) throw new RemoteExamException("EXPIRED");
            if (firstEffect) BeforeFirstEffect?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Actions : IRemoteExamActions
    {
        public List<string> Calls = [];
        public RemoteExamObservation Observation = new(new("ClassIsland.exe", 2, "ExamAware.exe", 3), 4, false, false);
        public string? Failure;
        public bool RecordingBusy, Reserved;
        public Func<string, Task>? BeforeAction;
        public Func<RemoteExamDocument>? State;
        public int SavedRecordings;
        public Task<IDisposable> FinishRecordingAndReserveAsync(CancellationToken token)
        {
            if (RecordingBusy) { SavedRecordings++; RecordingBusy = false; }
            return ReserveIdleRecordingAsync(token);
        }
        public Task ValidateModeAsync(CancellationToken token) => Task.CompletedTask;
        public async Task<long> EnterExamModeAsync(RemoteExamConfiguration expected, long revision, Func<Task> authorize,
            Action<string> progress, CancellationToken token)
        {
            await authorize();
            if (!Observation.ClassIslandStopped) await CloseClassIslandAsync(expected, authorize, token);
            Observation = Observation with { ClassroomMode = "Exam", StartupReady = true, ModeRevision = revision + 1 };
            return Observation.ModeRevision;
        }
        public Task<IDisposable> ReserveIdleRecordingAsync(CancellationToken token)
        {
            if (RecordingBusy || Reserved) throw new RemoteExamException("RECORDING_BUSY");
            Reserved = true;
            return Task.FromResult<IDisposable>(new Release(() => Reserved = false));
        }
        public Task<RemoteExamObservation> InspectAsync(CancellationToken token) => Task.FromResult(Observation);
        private async Task Act(string step, Func<Task> beforeDispatch)
        {
            Assert.True(Reserved);
            Assert.True(State!().AutomaticPaused);
            Assert.True(Assert.Single(State().Operations!, x => x.Intent.OperationId == State().PauseOperationId).PauseEstablished);
            if (BeforeAction is not null) await BeforeAction(step);
            await beforeDispatch();
            Calls.Add(step);
            if (Failure == step) throw new RemoteExamException("UAC_CANCELLED");
        }
        public async Task PrepareExamAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token)
        { await Act("prepare", beforeDispatch); Observation = Observation with { ExamAwareReady = true }; }
        public async Task CloseClassIslandAsync(RemoteExamConfiguration expected, Func<Task> beforeDispatch, CancellationToken token)
        {
            Assert.True(Observation.ExamAwareReady);
            await Act("close", beforeDispatch);
            Observation = Observation with { ClassIslandStopped = true };
        }
        private sealed class Release(Action release) : IDisposable { public void Dispose() => release(); }
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath = Path.Combine(Path.GetTempPath(), "NPEduTools-N3-" + Guid.NewGuid());
        public RemoteExamStore Store;
        public Actions Actions = new();
        public Authorization Authorization = new();
        public RuntimeOperationGate Gate = new();
        public RemoteExamExecutor Executor;
        public Fixture()
        {
            Store = new(DirectoryPath);
            Actions.State = () => Store.State;
            Executor = new(Store, Gate, Actions);
        }
        public RemoteExamIntent Intent() => new(Guid.NewGuid(), Store.State.Revision, Actions.Observation.ModeRevision);
        public Task<RemoteExamEntry> Run(RemoteExamIntent? intent = null) => Executor.RunAsync(intent ?? Intent(), Authorization);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    [Fact]
    public async Task PreflightDoesNotWritePauseHistoryOrRunActions()
    {
        using var f = new Fixture();
        var before = f.Store.State;
        var observed = await f.Executor.PreflightAsync();
        Assert.Equal(f.Actions.Observation, observed);
        Assert.Equal(before, f.Store.State);
        Assert.Empty(f.Actions.Calls);
        Assert.False(f.Actions.Reserved);
        Assert.False(File.Exists(Path.Combine(f.DirectoryPath, "remote-exam-runtime.json")));
        using var next = f.Gate.TryEnterMutation(); Assert.NotNull(next);
    }

    [Fact]
    public async Task PriorityExamSavesRecordingIgnoresNoticeAndUsesCurrentModeRevision()
    {
        using var f = new Fixture(); var intent = f.Intent() with { SwitchMode = true };
        f.Actions.RecordingBusy = true;
        f.Actions.Observation = f.Actions.Observation with { NoticeOpen = true, ModeRevision = 99, RecoveryRequired = true };
        var result = await f.Run(intent);
        Assert.Equal("SUCCEEDED", result.Outcome);
        Assert.Equal(1, f.Actions.SavedRecordings);
        Assert.Equal(new[] { "prepare", "close" }, f.Actions.Calls);
        Assert.Equal("Exam", f.Actions.Observation.ClassroomMode);
        var count = f.Actions.Calls.Count;
        var repeat = await f.Run(f.Intent() with { SwitchMode = true });
        Assert.Equal("SUCCEEDED", repeat.Outcome); Assert.True(repeat.AlreadySatisfied);
        Assert.Equal(count, f.Actions.Calls.Count);
    }

    [Fact]
    public async Task InterruptedHistoryDoesNotBlockFreshPriorityRequestAndIsNotReplayed()
    {
        using var f = new Fixture(); var old = f.Intent() with { SwitchMode = true };
        f.Store.Save(f.Store.State with { AutomaticPaused = true, PauseOperationId = old.OperationId,
            Operations = [new(old, "UNKNOWN", "Check", DateTimeOffset.UtcNow, Reason: "HOST_INTERRUPTED")] });
        var fresh = f.Intent() with { SwitchMode = true };
        Assert.Equal("SUCCEEDED", (await f.Run(fresh)).Outcome);
        var history = f.Store.State.Operations!.First();
        Assert.Equal("UNKNOWN", history.Outcome); Assert.Equal(fresh.OperationId, history.SupersededBy);
        Assert.NotNull(history.ResolvedAt); Assert.Equal(fresh.OperationId, f.Store.State.PauseOperationId);
        Assert.Equal("UNKNOWN", (await f.Run(old)).Outcome);
        Assert.Equal(new[] { "prepare", "close" }, f.Actions.Calls);
    }

    [Fact]
    public async Task LocalInspectionOfPartialResultDoesNotResolveOrReplayIt()
    {
        using var f = new Fixture();
        f.Actions.Failure = "close";
        var entry = await f.Run();
        Assert.Equal("PARTIAL", entry.Outcome);
        var before = f.Store.State; int calls = f.Actions.Calls.Count;
        await f.Executor.PreflightAsync();
        await f.Executor.InspectLocallyAsync();
        Assert.Equal(before, f.Store.State); Assert.Equal(calls, f.Actions.Calls.Count);
        await Assert.ThrowsAsync<RemoteExamException>(() => f.Executor.EndLocallyAsync(entry.Intent.OperationId, before.Revision - 1));
        Assert.True(f.Store.State.AutomaticPaused);
        await f.Executor.EndLocallyAsync(entry.Intent.OperationId, before.Revision);
        Assert.False(f.Store.State.AutomaticPaused);
        Assert.Equal("PARTIAL", Assert.Single(f.Store.State.Operations!).Outcome);
        Assert.Equal(calls, f.Actions.Calls.Count);
    }

    [Fact]
    public async Task StandardHostRejectsBeforePausingOrLaunching()
    {
        using var f = new Fixture();
        f.Actions.Observation = f.Actions.Observation with { HostElevated = false };
        Assert.Equal("HOST_NOT_ELEVATED", (await Assert.ThrowsAsync<RemoteExamException>(() => f.Executor.PreflightAsync())).Code);
        Assert.Empty(f.Store.State.Operations!);
        var result = await f.Run();
        Assert.Equal("REJECTED", result.Outcome);
        Assert.Equal("HOST_NOT_ELEVATED", result.Reason);
        Assert.False(f.Store.State.AutomaticPaused);
        Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public void OnlyReadOnlyPreflightIsExposedByProtocol()
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "remoteexam.preflight");
        Assert.Null(Protocol.Validate(request));
        Assert.NotNull(Protocol.Validate(request with { ExecutablePath = @"C:\other.exe" }));
        Assert.NotNull(Protocol.Validate(request with { OperationId = Guid.NewGuid() }));
        Assert.NotNull(Protocol.Validate(request with { ObserveMs = 1 }));
        Assert.Equal("UnknownCapability", Protocol.Validate(request with { Capability = "remoteexam.run" }));
    }

    [Fact]
    public async Task StartsDestinationBeforeClosingSourceAndNeverTouchesStartupMode()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.DirectoryPath);
        string legacy = Path.Combine(f.DirectoryPath, "classroom-mode.json");
        File.WriteAllText(legacy, "untouched legacy configuration");
        var result = await f.Run();
        Assert.Equal("SUCCEEDED", result.Outcome);
        Assert.Equal(new[] { "prepare", "close" }, f.Actions.Calls);
        Assert.True(f.Store.State.AutomaticPaused);
        Assert.False(f.Actions.Reserved);
        Assert.Equal("untouched legacy configuration", File.ReadAllText(legacy));
        Assert.Equal(new[] { "classroom-mode.json", "remote-exam-runtime.json" },
            Directory.GetFiles(f.DirectoryPath).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public async Task AlreadySatisfiedDoesNotRestartOrQuitAgain()
    {
        using var f = new Fixture();
        f.Actions.Observation = f.Actions.Observation with { ExamAwareReady = true, ClassIslandStopped = true };
        Assert.Equal("SUCCEEDED", (await f.Run()).Outcome);
        Assert.Empty(f.Actions.Calls);
        Assert.True(f.Store.State.AutomaticPaused);
    }

    [Theory]
    [InlineData("RECORDING_BUSY")]
    [InlineData("DESKTOP_UNAVAILABLE")]
    [InlineData("RECOVERY_REQUIRED")]
    [InlineData("STATE_CHANGED")]
    [InlineData("AUTH_REVOKED")]
    [InlineData("EXPIRED")]
    [InlineData("OPERATION_BUSY")]
    public async Task FailedPreconditionsHaveNoProcessOrPauseEffects(string code)
    {
        using var f = new Fixture(); var intent = f.Intent();
        switch (code)
        {
            case "RECORDING_BUSY": f.Actions.RecordingBusy = true; break;
            case "NOTICE_OPEN": f.Actions.Observation = f.Actions.Observation with { NoticeOpen = true }; break;
            case "DESKTOP_UNAVAILABLE": f.Actions.Observation = f.Actions.Observation with { DesktopAvailable = false }; break;
            case "RECOVERY_REQUIRED": f.Actions.Observation = f.Actions.Observation with { RecoveryRequired = true }; break;
            case "STATE_CHANGED": f.Actions.Observation = f.Actions.Observation with { ModeRevision = 9 }; break;
            case "AUTH_REVOKED": f.Authorization.Failure = code; break;
            case "EXPIRED": f.Authorization.PermitExpired = true; break;
            case "OPERATION_BUSY": f.Actions.Observation = f.Actions.Observation with { PendingActions = true }; break;
        }
        var result = await f.Run(intent);
        Assert.Equal("REJECTED", result.Outcome); Assert.Equal(code, result.Reason);
        Assert.Empty(f.Actions.Calls); Assert.False(f.Store.State.AutomaticPaused); Assert.False(f.Actions.Reserved);
    }

    [Theory]
    [InlineData("prepare", 1)]
    [InlineData("close", 2)]
    public async Task PartialFailureKeepsPauseAndNeverRollsBack(string step, int calls)
    {
        using var f = new Fixture(); f.Actions.Failure = step;
        var result = await f.Run();
        Assert.Equal("PARTIAL", result.Outcome); Assert.Equal("UAC_CANCELLED", result.Reason);
        Assert.Equal(calls, f.Actions.Calls.Count); Assert.True(f.Store.State.AutomaticPaused);
        f.Actions.Failure = null;
        Assert.Equal("SUCCEEDED", (await f.Run()).Outcome);
        Assert.Equal(2, f.Store.State.Operations!.Length);
    }

    [Theory]
    [InlineData("AUTH_REVOKED")]
    [InlineData("CONFIGURATION_DRIFT")]
    [InlineData("EXAMAWARE_NOT_READY")]
    public async Task RechecksImmediatelyAfterSimulatedUacBeforeDispatch(string reason)
    {
        using var f = new Fixture();
        f.Actions.BeforeAction = step =>
        {
            if (step == "close")
            {
                if (reason == "AUTH_REVOKED") f.Authorization.Failure = reason;
                if (reason == "CONFIGURATION_DRIFT") f.Actions.Observation = f.Actions.Observation with
                { Configuration = f.Actions.Observation.Configuration with { ClassIslandRevision = 99 } };
                if (reason == "EXAMAWARE_NOT_READY") f.Actions.Observation = f.Actions.Observation with { ExamAwareReady = false };
            }
            return Task.CompletedTask;
        };
        var result = await f.Run();
        Assert.Equal("PARTIAL", result.Outcome); Assert.Equal(reason, result.Reason);
        Assert.Equal(new[] { "prepare" }, f.Actions.Calls);
    }

    [Fact]
    public async Task ExactReplayReturnsStoredOutcomeAndChangedRequestIsRejected()
    {
        using var f = new Fixture(); var intent = f.Intent();
        var first = await f.Run(intent); f.Actions.Calls.Clear();
        Assert.Equal(first, await f.Run(intent)); Assert.Empty(f.Actions.Calls);
        Assert.Equal("REQUEST_CONFLICT", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Run(intent with { ExpectedModeRevision = 99 }))).Code);
        var restarted = new RemoteExamExecutor(new RemoteExamStore(f.DirectoryPath), new(), f.Actions);
        Assert.Equal(first, await restarted.RunAsync(intent, f.Authorization)); Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public async Task InFlightSwitchRejectsConcurrentSwitchAndMutationsWithoutBlockingReads()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Actions.BeforeAction = async step => { if (step == "prepare") { entered.SetResult(); await proceed.Task; } };
        var pending = f.Run();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("RUNNING", Assert.Single(f.Executor.State.Operations!).Outcome);
            Assert.Null(f.Gate.TryEnterMutation());
            Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<RemoteExamException>(() => f.Run())).Code);
        }
        finally { proceed.SetResult(); }
        Assert.Equal("SUCCEEDED", (await pending).Outcome);
        using var mutation = f.Gate.TryEnterMutation(); Assert.NotNull(mutation);
    }

    [Fact]
    public async Task ExistingMutationPreventsSwitchAndDoubleDisposeCannotReleaseOtherOwner()
    {
        using var f = new Fixture();
        using var first = f.Gate.TryEnterMutation();
        using var second = f.Gate.TryEnterMutation();
        first!.Dispose(); first.Dispose();
        var pending = f.Run();
        Assert.False(pending.IsCompleted); Assert.Null(f.Gate.TryEnterMutation());
        Assert.Empty(f.Store.State.Operations!);
        second!.Dispose();
        Assert.Null(f.Gate.TryReserveSwitch()); // A new local switch cannot overtake the waiting remote request.
        Assert.Equal("SUCCEEDED", (await pending).Outcome);
    }

    [Fact]
    public async Task RestartMarksUnfinishedUnknownAndDoesNotReplayActions()
    {
        using var f = new Fixture(); var intent = f.Intent();
        f.Store.Save(f.Store.State with { Operations = [new(intent, "CHECKING", "Check", DateTimeOffset.UtcNow)] });
        var restarted = new RemoteExamStore(f.DirectoryPath);
        Assert.True(restarted.State.AutomaticPaused);
        Assert.Equal("UNKNOWN", Assert.Single(restarted.State.Operations!).Outcome);
        var executor = new RemoteExamExecutor(restarted, new(), f.Actions);
        Assert.Equal("UNKNOWN", (await executor.RunAsync(intent, f.Authorization)).Outcome);
        Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public async Task LocalResolutionPreservesFailureAndHistoryAndOnlyRemovesOwnPause()
    {
        using var f = new Fixture(); f.Actions.Failure = "close";
        var intent = f.Intent(); var failed = await f.Run(intent); f.Actions.Calls.Clear();
        await f.Executor.EndLocallyAsync(intent.OperationId, f.Store.State.Revision);
        var resolved = Assert.Single(f.Store.State.Operations!);
        Assert.Equal(failed.Outcome, resolved.Outcome); Assert.Equal(failed.Reason, resolved.Reason);
        Assert.NotNull(resolved.ResolvedAt); Assert.False(f.Store.State.AutomaticPaused); Assert.Empty(f.Actions.Calls);
        Assert.Equal(resolved, await f.Run(intent)); Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public async Task LocalResolutionRequiresFreshRevisionAndIdleRecorder()
    {
        using var f = new Fixture(); var result = await f.Run();
        Assert.Equal("STATE_CHANGED", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Executor.EndLocallyAsync(result.Intent.OperationId, 0))).Code);
        f.Actions.RecordingBusy = true;
        Assert.Equal("RECORDING_BUSY", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision))).Code);
        Assert.True(f.Store.State.AutomaticPaused); Assert.Null(Assert.Single(f.Store.State.Operations!).LocallyEndedAt);
    }

    [Fact]
    public async Task SuccessfulTaskReleasesOperationSlotButKeepsPauseUntilExplicitLocalEnd()
    {
        using var f = new Fixture();
        var first = await f.Run();
        Assert.NotNull(first.ResolvedAt); Assert.True(f.Store.State.AutomaticPaused);
        f.Actions.Calls.Clear();
        var second = await f.Run();
        Assert.Equal("SUCCEEDED", second.Outcome); Assert.NotNull(second.ResolvedAt);
        Assert.Empty(f.Actions.Calls); Assert.Equal(2, f.Store.State.Operations!.Length);
        Assert.Equal(second.Intent.OperationId, f.Store.State.PauseOperationId);
        await f.Executor.EndLocallyAsync(second.Intent.OperationId, f.Store.State.Revision);
        Assert.False(f.Store.State.AutomaticPaused); Assert.Null(f.Store.State.PauseOperationId);
        Assert.NotNull(f.Store.State.Operations!.Last().LocallyEndedAt);
        Assert.All(f.Store.State.Operations!, x => Assert.Equal("SUCCEEDED", x.Outcome));
    }

    [Fact]
    public async Task RejectedNewRequestCannotClearPauseOfPreviousSuccessfulOperation()
    {
        using var f = new Fixture(); var first = await f.Run();
        f.Authorization.Failure = "AUTH_REVOKED";
        Assert.Equal("REJECTED", (await f.Run()).Outcome);
        Assert.True(f.Store.State.AutomaticPaused); Assert.Equal(first.Intent.OperationId, f.Store.State.PauseOperationId);
    }

    [Fact]
    public async Task LocalResolutionCannotClearProtectionWhileExternalActionStillPending()
    {
        using var f = new Fixture(); f.Actions.Failure = "close";
        var result = await f.Run();
        f.Actions.Observation = f.Actions.Observation with { PendingActions = true };
        Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<RemoteExamException>(() =>
            f.Executor.EndLocallyAsync(result.Intent.OperationId, f.Store.State.Revision))).Code);
        Assert.True(f.Store.State.AutomaticPaused); Assert.Null(Assert.Single(f.Store.State.Operations!).LocallyEndedAt);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("pending")]
    public async Task UnreadableOrAmbiguousJournalFailsClosedWithoutOverwritingEvidence(string kind)
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.DirectoryPath);
        string path = Path.Combine(f.DirectoryPath, "remote-exam-runtime.json" + (kind == "pending" ? ".pending" : ""));
        File.WriteAllText(path, "broken");
        var store = new RemoteExamStore(f.DirectoryPath);
        var executor = new RemoteExamExecutor(store, new(), f.Actions);
        Assert.True(store.State.AutomaticPaused);
        Assert.Equal("STORAGE_UNAVAILABLE", (await Assert.ThrowsAsync<RemoteExamException>(() => executor.RunAsync(f.Intent(), f.Authorization))).Code);
        Assert.Equal("broken", File.ReadAllText(path)); Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public async Task FailedFirstWritePreventsAnyExternalAction()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "remote-exam-runtime.json.pending"));
        Assert.Equal("STORAGE_UNAVAILABLE", (await Assert.ThrowsAsync<RemoteExamException>(() => f.Run())).Code);
        Assert.True(f.Store.State.AutomaticPaused); Assert.Empty(f.Actions.Calls);
    }

    [Fact]
    public async Task FailedPauseWritePreventsProcessDispatch()
    {
        using var f = new Fixture();
        f.Authorization.BeforeFirstEffect = () => Directory.CreateDirectory(
            Path.Combine(f.DirectoryPath, "remote-exam-runtime.json.pending"));
        Assert.Equal("STORAGE_UNAVAILABLE", (await Assert.ThrowsAsync<RemoteExamException>(() => f.Run())).Code);
        Assert.True(f.Store.State.AutomaticPaused);
        Assert.Empty(f.Actions.Calls);
        var restarted = new RemoteExamStore(f.DirectoryPath);
        Assert.True(restarted.State.AutomaticPaused); Assert.Equal("STORAGE_UNAVAILABLE", restarted.State.StorageError);
    }

    [Theory]
    [InlineData("missingPause")]
    [InlineData("duplicateOperation")]
    [InlineData("unknownVersion")]
    public void SemanticallyInvalidJournalPreservesOriginalAndFailsClosed(string kind)
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.DirectoryPath);
        var entry = new RemoteExamEntry(f.Intent(), "UNKNOWN", "Check", DateTimeOffset.UtcNow);
        var document = new RemoteExamDocument(AutomaticPaused: kind != "missingPause", Operations: [entry]);
        if (kind == "duplicateOperation") document = document with { Operations = [entry, entry] };
        if (kind == "unknownVersion") document = document with { Version = 900 };
        string content = JsonSerializer.Serialize(document, Protocol.Json);
        string path = Path.Combine(f.DirectoryPath, "remote-exam-runtime.json"); File.WriteAllText(path, content);
        var restarted = new RemoteExamStore(f.DirectoryPath);
        Assert.True(restarted.State.AutomaticPaused); Assert.NotNull(restarted.State.StorageError);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("Idle", false, true)]
    [InlineData("Saved", false, true)]
    [InlineData("Failed", false, true)]
    [InlineData("Idle", true, false)]
    [InlineData("Saved", true, false)]
    [InlineData("Starting", false, false)]
    [InlineData("Recording", false, false)]
    [InlineData("Paused", false, false)]
    [InlineData("Pausing", false, false)]
    [InlineData("Saving", false, false)]
    [InlineData("Unknown", false, false)]
    public void RecorderReservationRejectsAllActiveOrUnknownStates(string phase, bool alive, bool expected) =>
        Assert.Equal(expected, RecordingService.CanReserveForRuntime(new(phase, "fixture"), alive));

    [Fact]
    public async Task RealRecorderReservationBlocksManualStartWithoutStartingWorkerAndKeepsStatusAvailable()
    {
        using var f = new Fixture();
        await using var recorder = new RecordingService("NPEduTools.Test.n3." + Guid.NewGuid(), f.DirectoryPath,
            () => SchoolClockFrame.Unavailable("fixture"));
        using var lease = await recorder.ReserveIdleForRuntimeAsync();
        var response = await recorder.HandleAsync(new(1, Guid.NewGuid(), "recording.command", Recording:
            new("start", new("fixture", f.DirectoryPath), ClientId: Guid.NewGuid())));
        Assert.Equal("RuntimeOperationBusy", response.ErrorCode);
        Assert.Equal("Idle", recorder.State.Phase);
        Assert.Equal("Succeeded", (await recorder.HandleAsync(new(1, Guid.NewGuid(), "recording.status"))).Outcome);
        await Assert.ThrowsAsync<RemoteExamException>(() => recorder.ReserveIdleForRuntimeAsync());
        lease.Dispose(); lease.Dispose();
        using var next = await recorder.ReserveIdleForRuntimeAsync(); Assert.NotNull(next);
    }

    [Fact]
    public async Task RemotePauseIsUnionWithLegacyPauseAndDoesNotEnableOrRewriteRecorder()
    {
        using var f = new Fixture(); bool local = false, remote = true;
        await using var recorder = new RecordingService("NPEduTools.Test.n3." + Guid.NewGuid(), f.DirectoryPath,
            () => SchoolClockFrame.Unavailable("fixture"), () => local, () => remote);
        Assert.True(recorder.Automatic.SuspendedByMode); Assert.Contains("远程考试", recorder.Automatic.Message);
        local = true; remote = false; Assert.True(recorder.Automatic.SuspendedByMode);
        local = false; Assert.False(recorder.Automatic.SuspendedByMode);
        Assert.False(recorder.Automatic.Enabled); Assert.Empty(recorder.Automatic.Recent); Assert.Equal("Idle", recorder.State.Phase);
    }

    [Fact]
    public async Task JournalCapacityNeverSilentlyDropsReplayProtection()
    {
        using var f = new Fixture();
        var now = DateTimeOffset.UtcNow;
        f.Store.Save(f.Store.State with { Operations = Enumerable.Range(0, RemoteExamStore.Capacity)
            .Select(i => new RemoteExamEntry(new(Guid.NewGuid(), 0, 0), "REJECTED", "Check", now)).ToArray() });
        Assert.Equal("HISTORY_FULL", (await Assert.ThrowsAsync<RemoteExamException>(() => f.Run())).Code);
        Assert.Empty(f.Actions.Calls);
        Assert.Equal(RemoteExamStore.Capacity, new RemoteExamStore(f.DirectoryPath).State.Operations!.Length);
    }
}
