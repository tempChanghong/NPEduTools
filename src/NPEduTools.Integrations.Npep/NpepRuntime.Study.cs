using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private Task _studyLoop = Task.CompletedTask;
    private INpepStudySettings? _study;
    private long _studySequence, _studyExecutionSequence, _studyResumeSequence;
    private sealed record StudyRetry(string Context, long Started, TimeSpan Delay);
    private StudyRetry? _studyReadRetry, _studyExchangeRetry, _studyResumeRetry;
    private void InitializeStudy(INpepStudySettings? study)
    {
        _study = study;
        if (study is null || _device is null) return;
        InitializeStudySeating(study);
        InitializeStudyHomework(study);
        InitializeStudyPresentation(study);
        InitializeStudyPhotos(study);
        InitializeStudyExit(study);
        InitializeSchoolManagement();
        _studyLoop = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                            await StudyCycleAsync(study, _lifetime.Token);
                        else if (Snapshot() is { State: "UNPAIRED" or "SUSPENDED" } or { ReportingPaused: true } || Snapshot().Busy && !ContinuingStudyResume) study.BindStudy(null);
                        else study.StudyFailed(StudyConnectionFailureCode());
                    }
                    catch (Exception e) when (Handled(e) || e is InvalidDataException)
                    { study.StudyFailed(e is NpepException n ? n.Code : "STUDY_OFFLINE"); }
                    await Task.Delay(TimeSpan.FromSeconds(3), _lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            finally { study.BindStudy(null); }
        });
    }
    private async Task<JsonObject> StudyContextAsync(CancellationToken token)
    {
        var context = await _device!.ControlContextAsync(Guid.Empty, token);
        context.Remove("controlEpoch"); return context;
    }
    private async Task RequireStudyContextAsync(JsonObject context, CancellationToken token)
    {
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
            throw new NpepException(StudyContextLossCode());
        JsonObject current;
        try { current = await StudyContextAsync(token); }
        catch (NpepException e) when (e.Code == "DEVICE_SUSPENDED" && Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
        { throw new NpepException(StudyContextLossCode()); }
        if (!JsonNode.DeepEquals(context, current))
            throw new NpepException("SESSION_SUPERSEDED");
        if (Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
            throw new NpepException(StudyContextLossCode());
    }
    private string StudyContextLossCode()
    {
        lock (_sync) return _state.State == "ACTIVE" && !_state.ReportingPaused && (!_state.Busy || _continuingStudyResume)
            ? StudyConnectionFailureCode() : "SESSION_SUPERSEDED";
    }
    private string StudyConnectionFailureCode()
    {
        lock (_sync) return _state.Error is "AUTH_INVALID" or "CREDENTIAL_EXPIRED" or "INSTANCE_MISMATCH" or
            "BINDING_CHANGED" or "SESSION_SUPERSEDED" or "DEVICE_SUSPENDED" ? _state.Error : "STUDY_OFFLINE";
    }
    internal async Task StudyCycleAsync(INpepStudySettings study, CancellationToken token)
    {
        long connectionRevision = Snapshot().Revision;
        var context = await StudyContextAsync(token);
        string contextKey = context.ToJsonString();
        if (StudyRetryPending(_studyReadRetry, contextKey)) return;
        study.BindStudy(context["identity"]!.ToJsonString());
        JsonObject configuration;
        try { configuration = await ReadStudyConfigurationAsync(context, token); }
        catch (Exception e) when (!token.IsCancellationRequested && TransientStudyExchangeFailure(e))
        {
            _studyReadRetry = NewStudyRetry(contextKey, e);
            throw;
        }
        _studyReadRetry = null;
        await RequireStudyContextAsync(context, token);
        if (!JsonNode.DeepEquals(context["identity"], configuration["identity"])) throw new NpepException("SESSION_SUPERSEDED");
        study.ApplyStudy(configuration);
        if (StudyRetryPending(_studyExchangeRetry, contextKey)) return;
        try
        {
            await ExchangeStudyExecutionAsync(study, context, contextKey, configuration, connectionRevision, token);
            _studyExchangeRetry = null;
        }
        catch (Exception e) when (!token.IsCancellationRequested && TransientStudyExchangeFailure(e))
        {
            // The configuration GET succeeded. A temporary downstream outage does not
            // revoke that download, renew its age, or manufacture an execution grant.
            // Recheck pause/binding/session changes before retaining the fresh policy.
            await RequireStudyContextAsync(context, token);
            if (e is NpepException { RetryAfterSeconds: not null } or NpepException { Code: "RATE_LIMITED" })
                _studyExchangeRetry = NewStudyRetry(contextKey, e);
        }
    }

    private async Task<JsonObject> ReadStudyConfigurationAsync(JsonObject context, CancellationToken token)
    {
        try { return await _device!.StudyAsync(null, token); }
        catch (Exception e) when (!token.IsCancellationRequested && TransientStudyExchangeFailure(e) && !IsTlsFailure(e) &&
            e is not NpepException { Code: "RATE_LIMITED" } && e is not NpepException { RetryAfterSeconds: not null })
        {
            // Only the read gets a quick retry. Release the device gate before waiting and recheck the original session.
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            using var retry = CancellationTokenSource.CreateLinkedTokenSource(token);
            retry.CancelAfter(TimeSpan.FromSeconds(3));
            await RequireStudyContextAsync(context, retry.Token);
            return await _device!.StudyAsync(null, retry.Token);
        }
    }

    private bool StudyRetryPending(StudyRetry? retry, string context) =>
        retry is not null && retry.Context == context && _receiptTime.GetElapsedTime(retry.Started) < retry.Delay;

    private StudyRetry NewStudyRetry(string context, Exception error) =>
        new(context, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds((error as NpepException)?.RetryAfterSeconds ?? 6));

    private static bool TransientStudyExchangeFailure(Exception error) => error is HttpRequestException or OperationCanceledException
        || error is NpepException { Code: "TEMPORARILY_UNAVAILABLE" or "RATE_LIMITED" };

    private async Task ExchangeStudyExecutionAsync(INpepStudySettings study, JsonObject context, string contextKey,
        JsonObject configuration, long connectionRevision, CancellationToken token)
    {
        var status = study.ObserveStudy();
        if (status is null) return;
        var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(), ["sequence"] = ++_studySequence, ["status"] = status };
        var reply = await _device!.StudyAsync(body, token);
        await RequireStudyContextAsync(context, token);
        if (!reply["confirmed"]!.GetValue<bool>() || reply.Text("version") != configuration.Text("version")) throw new NpepException("POLICY_CHANGED");
        if (study.SupportsExecution && configuration["effective"]!.AsObject().Text("source") != "None")
        {
            var execution = study.ObserveStudyExecution();
            if (execution is null) throw new NpepException("STUDY_NOT_APPLIED");
            var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
            var grant = await _device.StudyExecutionAsync(new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
                ["sequence"] = ++_studyExecutionSequence, ["version"] = configuration["version"]!.DeepClone(), ["status"] = execution }, token);
            await RequireStudyContextAsync(context, token);
            study.ConfirmStudyExecution(configuration, grant, System.Diagnostics.Stopwatch.GetElapsedTime(sentAt));
            if (study.SupportsResume && !StudyRetryPending(_studyResumeRetry, contextKey))
                await ExchangeStudyResumeAsync(study, context, contextKey, configuration, connectionRevision, token);
        }
    }

    private async Task ExchangeStudyResumeAsync(INpepStudySettings study, JsonObject context, string contextKey,
        JsonObject configuration, long connectionRevision, CancellationToken token)
    {
        bool IsOriginalConnection() => _state is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false } &&
            _state.Revision == connectionRevision && HasFreshStatusReceipt() && !token.IsCancellationRequested;

        void RequireOriginalConnection()
        {
            lock (_sync)
                if (IsOriginalConnection()) return;
            token.ThrowIfCancellationRequested();
            throw new NpepException("SESSION_SUPERSEDED");
        }
        RequireOriginalConnection();
        var sentAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var body = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.Copy(),
            ["sequence"] = ++_studyResumeSequence, ["version"] = configuration["version"]!.DeepClone(),
            ["receipt"] = study.ObserveStudyResumeReceipt() };
        JsonObject resume;
        try { resume = await _device!.StudyResumeAsync(context, body, RequireOriginalConnection, token); }
        catch (NpepException e) when (e.Code == "STUDY_RESUME_UNSUPPORTED")
        {
            RequireOriginalConnection();
            // Old servers still support policy, capture and display.
            _studyResumeRetry = new(contextKey, _receiptTime.GetTimestamp(), TimeSpan.FromSeconds(60));
            return;
        }
        catch (NpepException e) when (e.Status == 401)
        {
            if (Snapshot().Revision == connectionRevision)
                Publish("STOPPED", "设备授权已失效，学校自习继续同步已停止。", e.Code, requireSuspended: true);
            throw;
        }
        catch (Exception e) when (!token.IsCancellationRequested && (TransientStudyExchangeFailure(e) ||
            e is NpepException { Status: 429 or >= 500 } or NpepException { Code: "INVALID_RESPONSE" }))
        {
            RequireOriginalConnection();
            // The command exchange has its own server bucket. Keep policy and execution receipts running.
            if (e is NpepException { RetryAfterSeconds: not null } or NpepException { Status: 429 })
                _studyResumeRetry = NewStudyRetry(contextKey, e);
            return;
        }

        bool current;
        lock (_sync)
        {
            current = IsOriginalConnection();
            if (current && resume["confirmed"]!.GetValue<bool>() && resume.Text("version") == configuration.Text("version") &&
                resume["command"] is JsonObject command)
                study.ApplyStudyResume(configuration, command, System.Diagnostics.Stopwatch.GetElapsedTime(sentAt));
        }
        if (!current)
        {
            token.ThrowIfCancellationRequested();
            throw new NpepException("SESSION_SUPERSEDED");
        }
        _studyResumeRetry = null;
    }
}
