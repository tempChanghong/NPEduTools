namespace NPEduTools.Host;

/// <summary>
/// N3 local execution kernel. Local IPC exposes inspection and local resolution only.
/// Network execution requires a current school binding and transport authorization.
/// </summary>
public sealed class RemoteExamExecutor(IRemoteExamStore store, RuntimeOperationGate gate, IRemoteExamActions actions)
{
    public RemoteExamDocument State => store.State;

    public void RecoverUncertain(RemoteExamIntent intent)
    {
        using var reservation = gate.TryReserveSwitch() ?? throw new RemoteExamException("OPERATION_BUSY");
        RequireStorage();
        if (State.Operations!.Any(x => x.Intent.OperationId == intent.OperationId)) return;
        Save(new(intent, "UNKNOWN", "Check", DateTimeOffset.UtcNow, Reason: "HOST_INTERRUPTED"), paused: true);
    }

    /// <summary>Inspect an unresolved local operation without resolving it or enabling a new run.</summary>
    public async Task<RemoteExamObservation> InspectLocallyAsync(CancellationToken token = default)
    {
        using var reservation = gate.TryReserveSwitch() ?? throw new RemoteExamException("OPERATION_BUSY");
        RequireStorage();
        using var recording = await actions.ReserveIdleRecordingAsync(token);
        var observed = await actions.InspectAsync(token);
        Check(observed, new(Guid.NewGuid(), State.Revision, observed.ModeRevision));
        return observed;
    }

    /// <summary>Local read-only preflight. Never grants consent, writes the ledger or starts software.</summary>
    public async Task<RemoteExamObservation> PreflightAsync(CancellationToken token = default, bool switchMode = false)
    {
        using var reservation = gate.TryReserveSwitch() ?? throw new RemoteExamException("OPERATION_BUSY");
        RequireStorage();
        if (State.Operations!.Any(RemoteExamStore.Unresolved)) throw new RemoteExamException("RECOVERY_REQUIRED");
        using var recording = await actions.ReserveIdleRecordingAsync(token);
        var observed = await actions.InspectAsync(token);
        Check(observed, new(Guid.NewGuid(), State.Revision, observed.ModeRevision));
        if (switchMode) await actions.ValidateModeAsync(token);
        return observed;
    }

    public async Task<RemoteExamEntry> RunAsync(RemoteExamIntent intent, IRemoteExamAuthorization authorization,
        CancellationToken token = default)
    {
        if (intent.OperationId == Guid.Empty || intent.ExpectedRuntimeRevision < 0 || intent.ExpectedModeRevision < 0 ||
            intent.Target is not ("Exam" or "Daily") || intent.Target == "Daily" && !intent.SwitchMode)
            throw new RemoteExamException("INVALID_REQUEST");
        using var reservation = await gate.ReservePrioritySwitchAsync(token);
        RequireStorage();
        var previous = State.Operations!.SingleOrDefault(x => x.Intent.OperationId == intent.OperationId);
        if (previous is not null)
        {
            if (previous.Intent != intent) throw new RemoteExamException("REQUEST_CONFLICT");
            return previous; // Includes resolved history; never replay an old operation.
        }
        if (!intent.SwitchMode && State.Revision != intent.ExpectedRuntimeRevision) throw new RemoteExamException("STATE_CHANGED");
        if (State.Operations!.Length == RemoteExamStore.Capacity) throw new RemoteExamException("HISTORY_FULL");
        var entry = new RemoteExamEntry(intent, "CHECKING", "Check", DateTimeOffset.UtcNow);
        Save(entry);
        try
        {
            await authorization.CheckAsync(false, token);
            using var idleRecording = intent.SwitchMode ? null : await actions.ReserveIdleRecordingAsync(token);
            var before = await actions.InspectAsync(token);
            var executionIntent = intent.SwitchMode ? intent with { ExpectedModeRevision = before.ModeRevision } : intent;
            Check(before, executionIntent);
            entry = entry with { Configuration = before.Configuration };
            await authorization.CheckAsync(true, token);
            entry = entry with { Outcome = "RUNNING", Step = "PauseRecording", PauseEstablished = true };
            Save(entry, paused: true);
            using var recording = intent.SwitchMode
                ? await actions.FinishRecordingAndReserveAsync(token)
                : null;

            async Task BeforeDispatch()
            {
                await authorization.CheckAsync(false, token);
                var current = await actions.InspectAsync(token);
                Check(current, executionIntent, before.Configuration);
            }

            bool daily = intent.Target == "Daily";
            if (!before.ExamAwareReady && !(daily && before.ExamAwareStopped && before.DailyStartupReady == true))
            {
                entry = entry with { Step = "PrepareExam" }; Save(entry);
                await BeforeDispatch();
                await actions.PrepareExamAsync(before.Configuration, BeforeDispatch, token);
            }
            long finalModeRevision = executionIntent.ExpectedModeRevision;
            if (intent.SwitchMode)
            {
                await BeforeDispatch();
                await actions.ValidateModeAsync(token);
                finalModeRevision = daily
                    ? await actions.EnterDailyModeAsync(before.Configuration, executionIntent.ExpectedModeRevision,
                        () => authorization.CheckAsync(false, token), step => { entry = entry with { Step = step }; Save(entry); }, token)
                    : await actions.EnterExamModeAsync(before.Configuration, executionIntent.ExpectedModeRevision,
                        () => authorization.CheckAsync(false, token), step => { entry = entry with { Step = step }; Save(entry); }, token);
            }
            else
            {
                await BeforeDispatch();
                var ready = await actions.InspectAsync(token);
                Check(ready, executionIntent, before.Configuration);
                if (!ready.ExamAwareReady) throw new RemoteExamException("EXAMAWARE_NOT_READY");
                if (!ready.ClassIslandStopped)
                {
                    entry = entry with { Step = "CloseClassIsland" }; Save(entry);
                    await actions.CloseClassIslandAsync(before.Configuration, async () =>
                    {
                        await BeforeDispatch();
                        var immediate = await actions.InspectAsync(token);
                        Check(immediate, executionIntent, before.Configuration);
                        if (!immediate.ExamAwareReady) throw new RemoteExamException("EXAMAWARE_NOT_READY");
                    }, token);
                }
            }
            entry = entry with { Step = "Verify" }; Save(entry);
            await authorization.CheckAsync(false, token);
            var after = await actions.InspectAsync(token);
            Check(after, executionIntent with { ExpectedModeRevision = finalModeRevision }, before.Configuration);
            if (daily ? !after.ExamAwareStopped || !after.ClassIslandReady || after.DailyStartupReady != true
                : !after.ExamAwareReady || !after.ClassIslandStopped) throw new RemoteExamException("TARGET_NOT_READY");
            if (intent.SwitchMode && after.ClassroomMode != intent.Target) throw new RemoteExamException("TARGET_NOT_READY");
            entry = entry with { Outcome = "SUCCEEDED", Observed = after,
                AlreadySatisfied = daily ? before.ExamAwareStopped && before.ClassIslandReady &&
                    before.DailyStartupReady == true && before.ClassroomMode == "Daily" :
                    before.ExamAwareReady && before.ClassIslandStopped &&
                    (!intent.SwitchMode || before.StartupReady == true && before.ClassroomMode == "Exam") };
            // Clear the independent pause only after the entire Daily transition is verified.
            Save(entry, paused: daily ? false : null, resolved: true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Keep the pause and actual observations. A fresh request can reconcile missing steps.
            entry = entry with
            {
                Outcome = entry.PauseEstablished ? "PARTIAL" : "REJECTED",
                Reason = error is RemoteExamException known ? known.Code : error is OperationCanceledException
                    ? "AUTH_REVOKED" : error is IOException or UnauthorizedAccessException ? "STORAGE_UNAVAILABLE" : "ACTION_FAILED"
            };
            if (State.StorageError is not null) throw new RemoteExamException("STORAGE_UNAVAILABLE");
            try { entry = entry with { Observed = await actions.InspectAsync(CancellationToken.None) }; }
            catch (Exception observationError) when (observationError is not OutOfMemoryException) { }
            Save(entry, resolved: true);
        }
        return State.Operations!.Single(x => x.Intent.OperationId == intent.OperationId);
    }

    /// <summary>
    /// Local-only resolution. Requires a new read-only inspection and idle reservation; preserves
    /// the original outcome. Must not be exported as a remote return-to-daily operation.
    /// </summary>
    public async Task EndLocallyAsync(Guid operationId, long expectedRevision, CancellationToken token = default)
    {
        using var reservation = gate.TryReserveSwitch() ?? throw new RemoteExamException("OPERATION_BUSY");
        RequireStorage();
        if (expectedRevision != State.Revision) throw new RemoteExamException("STATE_CHANGED");
        var entry = State.Operations!.SingleOrDefault(x => x.Intent.OperationId == operationId)
            ?? throw new RemoteExamException("OPERATION_NOT_FOUND");
        if (entry.LocallyEndedAt is not null) return;
        if (State.PauseOperationId != operationId || !State.AutomaticPaused)
            throw new RemoteExamException("RESOLUTION_NOT_REQUIRED");
        using var recording = await actions.ReserveIdleRecordingAsync(token);
        var current = await actions.InspectAsync(token);
        // Configuration changes require explicit local diagnosis, not a silent clear of the old intent.
        Check(current, entry.Intent with { ExpectedModeRevision = current.ModeRevision, SwitchMode = false }, entry.Configuration);
        if (entry.Intent.SwitchMode && current.ClassroomMode == "Exam") throw new RemoteExamException("DAILY_MODE_REQUIRED");
        var now = DateTimeOffset.UtcNow;
        Save(entry with { ResolvedAt = entry.ResolvedAt ?? now, LocallyEndedAt = now }, paused: false, updateTime: false);
    }

    private void RequireStorage()
    {
        if (State.StorageError is not null) throw new RemoteExamException("STORAGE_UNAVAILABLE");
    }

    internal static void Check(RemoteExamObservation value, RemoteExamIntent intent, RemoteExamConfiguration? expected = null)
    {
        if (!value.HostElevated) throw new RemoteExamException("HOST_NOT_ELEVATED");
        if (!value.DesktopAvailable) throw new RemoteExamException("DESKTOP_UNAVAILABLE");
        if (value.PendingActions) throw new RemoteExamException("OPERATION_BUSY");
        if (value.RecoveryRequired && !intent.SwitchMode) throw new RemoteExamException("RECOVERY_REQUIRED");
        if (value.ModeRevision != intent.ExpectedModeRevision) throw new RemoteExamException("STATE_CHANGED");
        if (expected is not null && value.Configuration != expected) throw new RemoteExamException("CONFIGURATION_DRIFT");
    }

    private void Save(RemoteExamEntry entry, bool? paused = null, bool updateTime = true, bool resolved = false)
    {
        if (updateTime) entry = entry with { UpdatedAt = DateTimeOffset.UtcNow };
        if (resolved) entry = entry with { ResolvedAt = entry.UpdatedAt };
        var entries = State.Operations!;
        // A fresh, serialized request supersedes uncertain history; it never replays the old intent.
        if (!entries.Any(x => x.Intent.OperationId == entry.Intent.OperationId))
            entries = entries.Select(x => RemoteExamStore.Unresolved(x) ||
                    x.Intent.OperationId == State.PauseOperationId && x.Outcome is "PARTIAL" or "UNKNOWN"
                ? x with { Outcome = RemoteExamStore.InProgress(x.Outcome) ? "UNKNOWN" : x.Outcome,
                    ResolvedAt = DateTimeOffset.UtcNow, SupersededBy = entry.Intent.OperationId } : x).ToArray();
        try
        {
            store.Save(State with
            {
                AutomaticPaused = paused ?? State.AutomaticPaused,
                PauseOperationId = paused == true ? entry.Intent.OperationId : paused == false ? null : State.PauseOperationId,
                Operations = entries.Any(x => x.Intent.OperationId == entry.Intent.OperationId)
                    ? entries.Select(x => x.Intent.OperationId == entry.Intent.OperationId ? entry : x).ToArray()
                    : [.. entries, entry]
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new RemoteExamException("STORAGE_UNAVAILABLE"); }
    }
}
