using System.Diagnostics;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private NpepControlPolicyStore? _planPolicy;
    private NpepPlanJournal? _planJournal;
    private INpepExamPlans? _plans;
    private Task _planLoop = Task.CompletedTask;
    private string? _planError;
    private long _planSequence;

    public RemoteExamPolicy PlanPolicy()
    {
        lock (_sync)
        {
            var p = _planPolicy?.State;
            string? error = _planPolicy?.Error ?? _planError ?? (p is null ? "POLICY_STORE_UNAVAILABLE" : null);
            bool connected = !_disposed && Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false };
            return new(p?.Revision ?? 0, error is null && p?.Allowed == true, p?.Scope, p?.ConsentId ?? Guid.Empty,
                p?.ControlEpoch ?? Guid.Empty, error is null && connected && p?.Scope is not null,
                error is not null ? "考试方案通道不可用：" + error : p?.Allowed == true ? "配对已授权考试方案投递；开始放映仍由学校明确请求，不替换现有放映。" : "配对后自动开放学校考试方案投递。",
                error, _controlBinding);
        }
    }

    public void SetPlanConsent(RemoteExamCommand c)
    {
        lock (_sync)
        {
            if (c.Action != "plan-consent" || !RemoteExamContract.Valid(c)) throw new NpepException("INVALID_REQUEST");
            var p = PlanPolicy();
            if (p.Error is not null) throw new NpepException(p.Error);
            if (c.Allowed == true && !p.CanEnable) throw new NpepException("CONTROL_OFFLINE");
            if (c.Allowed != true) throw new NpepException("INVALID_REQUEST");
            _planPolicy!.Synchronize(p.Scope);
        }
    }

    private void InitializePlans(INpepExamPlans? plans)
    {
        _plans = plans;
        if (plans is null || _device is null) return;
        try { _planJournal = new(_device.DataDirectory); }
        catch (Exception e) when (StorageError(e) || e is InvalidDataException) { _planError = "JOURNAL_UNAVAILABLE"; }
        _planLoop = Task.Run(async () =>
        {
            while (!_lifetime.IsCancellationRequested)
            {
                try
                {
                    if (Snapshot() is { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false, Busy: false })
                        await PlanCycleAsync(_lifetime.Token);
                }
                catch (Exception e) when (Handled(e) || e is InvalidDataException)
                {
                    // Transport failures retry only observation / result delivery. The journal fences effects.
                    if (e is IOException or UnauthorizedAccessException or InvalidDataException) _planError = "JOURNAL_UNAVAILABLE";
                }
                try { await Task.Delay(TimeSpan.FromSeconds(10), _lifetime.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    internal async Task PlanCycleAsync(CancellationToken token)
    {
        var policy = PlanPolicy();
        var context = await _device!.ControlContextAsync(policy.ControlEpoch, token);
        var status = _plans!.Observe();
        status["enabled"] = policy.Allowed;
        status["consentId"] = policy.ConsentId.ToString("D"); status["policyRevision"] = policy.Revision;
        await _device.PlanAsync("device/exam-plan-status", "ack", new JsonObject
        {
            ["requestId"] = NpepProtocol.Id(), ["context"] = context.DeepClone(), ["sequence"] = ++_planSequence, ["status"] = status
        }, "reportRequest", token);
        if (!policy.Allowed || !policy.CanEnable || _planJournal is null) return;
        var poll = await _device.PlanAsync("device/exam-plans", "poll", null, null, token);
        if (poll["item"] is not JsonObject op || !JsonNode.DeepEquals(context, op["context"])) return;
        if (op.Text("state") == "PREPARED") return;
        bool start = op.Text("state") is "START_REQUESTED" or "START_AUTHORIZED";
        if (!start && op.Text("state") != "QUEUED") return;
        string stage = start ? "start" : "prepare";
        var journal = _planJournal.State;
        bool recovery = journal?.Text("operationId") == op.Text("operationId") && journal.Text("stage") == stage;
        JsonObject result;
        if (recovery && journal!["result"] is JsonObject saved) result = (JsonObject)saved.DeepClone();
        else
        {
            journal = new JsonObject { ["version"] = 1, ["operationId"] = op.Text("operationId"), ["stage"] = stage,
                ["context"] = context.DeepClone(), ["result"] = null };
            _planJournal.Save(journal);
            result = new JsonObject { ["requestId"] = NpepProtocol.Id(), ["context"] = context.DeepClone(), ["stage"] = stage,
                ["state"] = "UNKNOWN", ["summary"] = null, ["sessionId"] = null, ["reasonCode"] = "UNKNOWN_RESULT", ["grantId"] = op["grant"]?["grantId"]?.DeepClone() };
            // START_AUTHORIZED without our fresh grant is also recovery, even with a new/empty journal.
            if (!recovery && op.Text("state") != "START_AUTHORIZED")
            {
                void Authorize()
                {
                    var current = PlanPolicy();
                    if (!current.Allowed || !current.CanEnable || current.Revision != op.Number("policyRevision") ||
                        current.ConsentId.ToString("D") != op.Text("consentId") || current.ControlEpoch != policy.ControlEpoch)
                        throw new NpepException("POLICY_CHANGED");
                }
                try
                {
                    Authorize();
                    Guid commandId = Guid.Parse(op.Text("operationId"));
                    long began = Stopwatch.GetTimestamp(); double seconds = 25;
                    if (start)
                    {
                        var grant = await _device.PlanAsync($"device/exam-plans/{op.Text("operationId")}/grant", "grantResponse",
                            new() { ["requestId"] = NpepProtocol.Id(), ["context"] = context.DeepClone() }, "deviceRequest", token);
                        if (((JsonObject)grant["grant"]!).Text("operationId") != op.Text("operationId")) throw new NpepException("INVALID_RESPONSE");
                        result["grantId"] = grant["grant"]!["grantId"]!.DeepClone();
                        commandId = Guid.Parse(result.Text("grantId"));
                        seconds = (DateTimeOffset.Parse(((JsonObject)grant["grant"]!).Text("startNotAfter")) - DateTimeOffset.Parse(grant.Text("serverTime"))).TotalSeconds;
                    }
                    var latest = await _device.ControlContextAsync(policy.ControlEpoch, token);
                    if (!JsonNode.DeepEquals(latest, context)) throw new NpepException("SESSION_SUPERSEDED");
                    var outcome = await _plans.ExecuteAsync(op, start, commandId, () =>
                    {
                        Authorize();
                        if (Stopwatch.GetElapsedTime(began).TotalSeconds >= seconds) throw new NpepException("EXPIRED");
                    }, token);
                    foreach (string field in new[] { "state", "summary", "sessionId", "reasonCode" }) result[field] = outcome[field]?.DeepClone();
                }
                catch (NpepException e)
                {
                    // A network failure during grant may already have authorized a start; never claim no effect.
                    result["reasonCode"] = e.Code is "POLICY_CHANGED" or "SESSION_SUPERSEDED" or "EXPIRED" or "INITIATOR_NO_LONGER_AUTHORIZED" or
                        "STATE_CHANGED" or "CONTROL_DISABLED" or "PLAN_EXPIRED" or "PLAYER_BUSY" or "PLAYER_UNKNOWN" ? e.Code : "UNKNOWN_RESULT";
                }
                catch (Exception e) when (Handled(e)) { }
            }
            journal["result"] = result.DeepClone(); _planJournal.Save(journal);
        }
        await _device.PlanAsync($"device/exam-plans/{op.Text("operationId")}/result", "ack", result, "resultRequest", token);
    }
}
