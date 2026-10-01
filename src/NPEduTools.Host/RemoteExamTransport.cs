using System.Text.Json.Nodes;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

public sealed class RemoteExamTransport(RemoteExamExecutor executor, IRemoteExamActions actions,
    RecordingService recording, ClassroomModeService classroom) : INpepRuntimeControl
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _rejections = new();
    private static long ConfigurationRevision(RemoteExamConfiguration config)
        => checked(config.ClassIslandRevision + config.ExamAwareRevision);
    private static RemoteExamIntent Intent(JsonObject op) => new(Guid.Parse(op.Text("operationId")), op.Number("expectedRuntimeRevision"),
        op.Number("expectedModeRevision"), op.Text("scope") == "EXAM_MODE", op.Text("target") == "DAILY" ? "Daily" : "Exam");

    public async Task<JsonObject> ObserveAsync(CancellationToken token)
    {
        var state = executor.State;
        var running = state.Operations?.LastOrDefault(x => RemoteExamStore.InProgress(x.Outcome));
        string rec = recording.State.Phase switch { "Idle" or "Saved" => "IDLE", "Recording" => "RECORDING", "Paused" => "PAUSED", "Starting" => "STARTING", "Saving" => "FINALIZING", _ => "UNKNOWN" };
        var result = new JsonObject { ["runtimeMode"] = "UNKNOWN", ["runtimePhase"] = "UNKNOWN", ["runtimeRevision"] = state.Revision,
            ["modeRevision"] = classroom.Snapshot.Revision, ["configurationRevision"] = 0, ["remoteExamPause"] = state.AutomaticPaused,
            ["recording"] = rec, ["desktop"] = "UNKNOWN", ["noticeOpen"] = false,
            ["operationId"] = state.PauseOperationId?.ToString("D"), ["observedAt"] = NpepRuntimeProtocol.UtcNow() };
        try
        {
            var observed = await actions.InspectAsync(token);
            bool exam = observed.ExamAwareReady && observed.ClassIslandStopped && observed.StartupReady == true && observed.ClassroomMode == "Exam";
            bool daily = observed.ExamAwareStopped && observed.ClassIslandReady && observed.DailyStartupReady == true && observed.ClassroomMode == "Daily" && !state.AutomaticPaused;
            result["runtimeMode"] = exam ? "EXAM" : daily ? "DAILY" : "OTHER";
            result["runtimePhase"] = running is not null ? "SWITCHING" : exam ? "EXAM_READY" : "IDLE";
            result["desktop"] = observed.DesktopAvailable ? "INTERACTIVE" : "LOCKED";
            result["configurationRevision"] = ConfigurationRevision(observed.Configuration);
        }
        catch (RemoteExamException ex) { result["runtimePhase"] = "UNKNOWN"; result["reasonCode"] = ex.Code; }
        if (running is not null) { result["runtimePhase"] = "SWITCHING"; result["step"] = running.Step; }
        return result;
    }

    public async Task ExecuteAsync(JsonObject operation, Func<bool, CancellationToken, Task> authorize, CancellationToken token)
    {
        if (operation.Text("scope") != "EXAM_MODE" || operation.Text("target") is not ("EXAM" or "DAILY")) throw new NpepException("POLICY_CHANGED");
        var intent = Intent(operation);
        try { await executor.RunAsync(intent, new Authorization(async (first, ct) =>
        {
            // Intent is a target mode, not a command bound to an obsolete UI snapshot.
            try { await authorize(first, ct); }
            catch (NpepException ex) { throw new RemoteExamException(ex.Code); }
        }), token); }
        catch (RemoteExamException error)
        {
            if (_rejections.Count >= 128) _rejections.Clear();
            _rejections[intent.OperationId] = error.Code;
            throw;
        }
    }

    public Task RecoverAsync(JsonObject operation, CancellationToken token)
    { token.ThrowIfCancellationRequested(); executor.RecoverUncertain(Intent(operation)); return Task.CompletedTask; }

    public Task<JsonObject> ResultAsync(Guid operationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var entry = executor.State.Operations?.SingleOrDefault(x => x.Intent.OperationId == operationId);
        string outcome = entry?.Outcome ?? "REJECTED";
        bool daily = entry?.Intent.Target == "Daily";
        if (RemoteExamStore.InProgress(outcome)) outcome = "UNKNOWN";
        var evidence = new JsonObject { ["examAware"] = outcome == "SUCCEEDED" ? "READY" : "UNKNOWN",
            ["classIsland"] = entry?.Observed is { } ci ? ci.ClassIslandStopped ? "EXITED" : "RUNNING" : outcome == "SUCCEEDED" ? "EXITED" : "UNKNOWN",
            ["remoteExamPause"] = !(daily && outcome == "SUCCEEDED") && (entry?.PauseEstablished == true || outcome == "UNKNOWN" || outcome == "REJECTED" && executor.State.AutomaticPaused),
            ["startup"] = entry?.Intent.SwitchMode != true ? "NOT_REQUESTED" : outcome == "SUCCEEDED" ? daily ? "DAILY_MODE_APPLIED" : "EXAM_MODE_APPLIED" :
                entry.PauseEstablished ? "UNKNOWN" : "NOT_REQUESTED",
            ["sideEffects"] = outcome == "REJECTED" ? "NONE" : outcome == "SUCCEEDED" ? "APPLIED" : "POSSIBLE",
            ["alreadySatisfied"] = entry?.AlreadySatisfied == true, ["observedAt"] = (entry?.UpdatedAt ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            ["configurationRevision"] = entry?.Configuration is { } config ? ConfigurationRevision(config) : 0 };
        evidence["examAware"] = entry?.Observed is { } ea ? ea.ExamAwareStopped ? "EXITED" : ea.ExamAwareReady ? "READY" : "NOT_READY" : outcome == "SUCCEEDED" ? "READY" : "UNKNOWN";
        if (daily && entry?.Observed?.ClassIslandReady == true) evidence["classIsland"] = "READY";
        string? reason = entry?.Reason ?? (_rejections.TryGetValue(operationId, out var rejected) ? rejected : null);
        // Network enums are deliberately bounded; never publish paths or local diagnostics.
        if (reason is not null && reason is not ("AUTH_REVOKED" or "EXPIRED" or "RECORDING_BUSY" or "OPERATION_BUSY" or "STATE_CHANGED" or "CONFIGURATION_DRIFT" or
            "EXAMAWARE_NOT_READY" or "DESKTOP_UNAVAILABLE" or "RECOVERY_REQUIRED" or "STORAGE_UNAVAILABLE" or "CONTROL_DISABLED" or "POLICY_CHANGED" or
            "CLASSISLAND_TASK_REQUIRED" or "STARTUP_NOT_READY" or "MODE_CONTROL_UNAVAILABLE" or "HOST_NOT_ELEVATED" or
            "RECORDING_SAVE_FAILED" or "RECORDING_SAVE_TIMEOUT" or "CLASSISLAND_CONFIGURATION_REQUIRED" or "EXAMAWARE_CONFIGURATION_REQUIRED" or "INVALID_LOCAL_EXECUTABLE" or
            "EXAMAWARE_PRESENTING" or "EXAMAWARE_EXIT_FAILED" or "CLASSISLAND_NOT_READY" or
            "HISTORY_FULL" or "CLASSISLAND_EXECUTABLE_INVALID" or "EXAMAWARE_EXECUTABLE_INVALID" or "EXAMAWARE_EXECUTABLE_UNREADABLE" or
            "EXAMAWARE_IDENTITY_UNAVAILABLE" or "CLASSISLAND_IDENTITY_UNAVAILABLE" or "CLASSISLAND_EXIT_UNAVAILABLE" or "UAC_CANCELLED")) reason = "UNKNOWN_RESULT";
        return Task.FromResult(new JsonObject { ["state"] = outcome, ["reasonCode"] = reason,
            ["step"] = entry?.Step switch { "PauseRecording" => "PAUSE_RECORDING", "PrepareExam" => "PREPARE_EXAM", "SetStartup" => "SET_STARTUP", "CloseClassIsland" => "CLOSE_CLASSISLAND", "CloseExam" => "CLOSE_EXAM", "StartClassIsland" => "START_CLASSISLAND", "Verify" => "VERIFY", _ => null }, ["evidence"] = evidence });
    }
    public bool LocallyEnded(Guid operationId) => executor.State.Operations?.Any(x => x.Intent.OperationId == operationId && x.LocallyEndedAt is not null) == true;
    public bool HasOperation(Guid operationId) => executor.State.Operations?.Any(x => x.Intent.OperationId == operationId) == true;
    private sealed class Authorization(Func<bool, CancellationToken, Task> check) : IRemoteExamAuthorization
    { public Task CheckAsync(bool firstEffect, CancellationToken token) => check(firstEffect, token); }
}
