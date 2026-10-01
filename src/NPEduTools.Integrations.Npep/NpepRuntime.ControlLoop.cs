using System.Diagnostics;
using System.Text.Json.Nodes;

namespace NPEduTools.Integrations.Npep;

public sealed partial class NpepRuntime
{
    private INpepRuntimeControl? _control;
    private NpepControlJournal? _controlJournal;
    private Task _controlLoop = Task.CompletedTask;
    private CancellationTokenSource? _controlCancellation;
    private string _controlMessage = "此后台尚未接入执行器。";
    private string? _controlContextKey;
    private long _controlSequence;

    private void InitializeControl(INpepRuntimeControl? control)
    {
        _control = control;
        if (control is null || _device is null) return;
        try { _controlJournal = new(_device.DataDirectory); }
        catch (Exception e) when (Handled(e)) { _controlMessage = "N3 投递账本不可用，执行已禁用；原文件已保留。"; return; }
        _controlMessage = "正在等待学校 N3 通道。";
        _controlLoop = Task.Run(ControlLoopAsync);
    }

    private async Task ControlLoopAsync()
    {
        double delay = 1;
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(delay), _lifetime.Token); }
            catch (OperationCanceledException) { break; }
            delay = 10;
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                if (_state.Busy || _blocked || Snapshot() is not { State: "ACTIVE", Connection: "ONLINE", ReportingPaused: false }) continue;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _controlCancellation = cancellation;
            }
            using (cancellation)
            try { await ControlCycleAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { delay = 1; }
            catch (Exception e) when (Handled(e) || e is InvalidOperationException or OverflowException)
            {
                string code = IsTlsFailure(e) ? "TLS_VALIDATION_FAILED" : e is NpepException n ? n.Code : "TEMPORARILY_UNAVAILABLE";
                lock (_sync) _controlMessage = code == "RUNTIME_UNSUPPORTED" ? "学校服务尚未支持 N3，请先更新后端。"
                    : $"N3 通道暂不可用（{code}），将自动重试；不会重放已接收操作。";
                if (e is NpepException { RetryAfterSeconds: { } retry }) delay = Math.Max(delay, retry);
                if (_device!.View().Text("state") == "SUSPENDED") Publish("STOPPED", "学校授权已失效，已停止互联。", code);
            }
            finally { lock (_sync) _controlCancellation = null; }
        }
    }

    private async Task ControlCycleAsync(CancellationToken token)
    {
        var policy = ControlPolicy();
        if (policy.Error is not null || policy.Scope is null) return;
        var context = await _device!.ControlContextAsync(policy.ControlEpoch, token);
        string key = context.ToJsonString();
        if (_controlContextKey != key) { _controlContextKey = key; _controlSequence = 0; }
        var body = NpepProtocol.Body(); body["context"] = context.Copy();
        body["policy"] = new JsonObject { ["consentId"] = policy.ConsentId.ToString("D"), ["policyRevision"] = policy.Revision,
            ["enabled"] = policy.Allowed, ["supported"] = true, ["pairedExamControl"] = true, ["remoteDailyControl"] = true };
        await _device.ControlAsync("device/runtime-control-policy", "policyResponse", body, "policyRequest", token);
        async Task ReportStatus()
        {
            long sampledAt = Stopwatch.GetTimestamp();
            var status = await _control!.ObserveAsync(token);
            status["consentId"] = policy.ConsentId.ToString("D"); status["policyRevision"] = policy.Revision;
            var report = NpepProtocol.Body(); report["context"] = context.Copy(); report["sequence"] = checked(++_controlSequence);
            report["sampleAgeMs"] = (int)Math.Min(60000, Stopwatch.GetElapsedTime(sampledAt).TotalMilliseconds); report["status"] = status;
            await _device.ControlAsync("device/runtime-status", "statusResponse", report, "statusRequest", token);
        }
        await ReportStatus();
        var polled = await _device.ControlAsync("device/runtime-operations", "deviceOperations", null, null, token);
        var items = (JsonArray)polled["items"]!;
        foreach (var pair in _controlJournal!.Entries.ToArray())
        {
            var item = (JsonObject)pair.Value!.DeepClone();
            if (item.Text("scope") != policy.Scope) continue;
            var remote = items.OfType<JsonObject>().SingleOrDefault(x => x.Text("operationId") == pair.Key);
            if (remote?["grant"] is JsonObject g && item["grant"] is null) { item["grant"] = g.Copy(); _controlJournal.Save(pair.Key, item); }
            await FlushControlResultAsync(pair.Key, item, context, token);
        }
        foreach (var op in items.OfType<JsonObject>())
        {
            string id = op.Text("operationId");
            if (_controlJournal.Entries.ContainsKey(id)) continue;
            if (!NpepProtocol.Equal(op["identity"], context["identity"])) throw new NpepException("AUTH_INVALID");
            var item = new JsonObject { ["scope"] = policy.Scope, ["operation"] = op.Copy(), ["grant"] = op["grant"]?.DeepClone() };
            _controlJournal.Save(id, item); // Before calling even the read-only execution kernel.
            if (op["grant"] is null)
            {
                long grantReceived = 0; double remainingMs = 0;
                async Task Authorize(bool first, CancellationToken ct)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = ControlPolicy();
                    if (!current.Allowed || !current.CanEnable) throw new NpepException("CONTROL_DISABLED");
                    if (current.Scope != policy.Scope || current.Revision != policy.Revision || current.ControlEpoch != policy.ControlEpoch ||
                        op.Text("consentId") != current.ConsentId.ToString("D") || op.Number("policyRevision") != current.Revision || op.Text("controlEpoch") != current.ControlEpoch.ToString("D")) throw new NpepException("POLICY_CHANGED");
                    if (!NpepProtocol.Equal(context, await _device.ControlContextAsync(current.ControlEpoch, ct))) throw new NpepException("SESSION_SUPERSEDED");
                    if (!first) return;
                    var start = NpepProtocol.Body(); start["context"] = context.Copy();
                    foreach (var field in new[] { "consentId", "policyRevision", "expectedRuntimeRevision", "expectedModeRevision", "expectedConfigurationRevision" }) start[field] = op[field]!.DeepClone();
                    long sent = Stopwatch.GetTimestamp();
                    var reply = await _device.ControlAsync($"device/runtime-operations/{id}/start", "startResponse", start, "startRequest", ct);
                    var grant = (JsonObject)reply["grant"]!;
                    if (grant.Text("operationId") != id) throw new NpepException("INVALID_RESPONSE");
                    foreach (var field in new[] { "identity", "runId", "sessionId", "statusEpoch", "controlEpoch" })
                        if (!NpepProtocol.Equal(grant[field], context[field])) throw new NpepException("INVALID_RESPONSE");
                    foreach (var field in new[] { "consentId", "policyRevision", "expectedRuntimeRevision", "expectedModeRevision", "expectedConfigurationRevision" })
                        if (!NpepProtocol.Equal(grant[field], op[field])) throw new NpepException("INVALID_RESPONSE");
                    remainingMs = (DateTimeOffset.Parse(grant.Text("startNotAfter")) - DateTimeOffset.Parse(reply.Text("serverTime"))).TotalMilliseconds - Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                    grantReceived = Stopwatch.GetTimestamp(); item["grant"] = grant.Copy(); _controlJournal.Save(id, item);
                    await Authorize(false, ct);
                    if (remainingMs <= Stopwatch.GetElapsedTime(grantReceived).TotalMilliseconds) throw new NpepException("EXPIRED");
                }
                try
                {
                    var executing = _control!.ExecuteAsync(op.Copy(), Authorize, token);
                    while (!executing.IsCompleted)
                    {
                        await Task.WhenAny(executing, Task.Delay(2000, token));
                        if (executing.IsCompleted) break;
                        try { await ReportStatus(); }
                        catch (Exception e) when (Handled(e)) { /* Execution retains ownership; status reporting is best effort. */ }
                        if (token.IsCancellationRequested) break;
                    }
                    await executing;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { /* The durable local kernel/outbox owns recovery; never call ExecuteAsync again. */ }
                // A lost start response may already have committed a grant. Fetch it before classifying recovery.
                polled = await _device.ControlAsync("device/runtime-operations", "deviceOperations", null, null, token);
                var remote = ((JsonArray)polled["items"]!).OfType<JsonObject>().SingleOrDefault(x => x.Text("operationId") == id);
                if (remote?["grant"] is JsonObject g) { item["grant"] = g.Copy(); _controlJournal.Save(id, item); }
            }
            await FlushControlResultAsync(id, item, context, token);
        }
        lock (_sync) _controlMessage = "N3 已连接，学校可查看许可和执行回执。";
    }

    private async Task FlushControlResultAsync(string id, JsonObject item, JsonObject context, CancellationToken token)
    {
        var operationId = Guid.Parse(id);
        var op = (JsonObject)item["operation"]!;
        if (item["event"] is not JsonObject)
        {
            if (item["grant"] is not null && !_control!.HasOperation(operationId)) await _control.RecoverAsync(op, token);
            var result = await _control!.ResultAsync(operationId, token);
            if (item["grant"] is null && result.Text("state") == "UNKNOWN") ((JsonObject)result["evidence"]!)["sideEffects"] = "NONE";
            JsonObject? execution = null;
            if (item["grant"] is JsonObject grant)
            {
                execution = new(); foreach (var field in new[] { "runId", "sessionId", "statusEpoch", "controlEpoch", "grantId" }) execution[field] = grant[field]!.DeepClone();
            }
            result["eventId"] = NpepProtocol.Id(); result["operationId"] = id; result["sequence"] = op.Number("lastEventSequence") + 1;
            result["occurredAt"] = NpepRuntimeProtocol.UtcNow(); result["execution"] = execution;
            item["event"] = result; _controlJournal!.Save(id, item);
        }
        var evt = (JsonObject)item["event"]!;
        if (item["ack"]?.GetValue<bool>() != true)
        {
            var body = NpepProtocol.Body(); body["context"] = context.Copy(); body["events"] = new JsonArray(evt.DeepClone());
            var reply = await _device!.ControlAsync("device/runtime-operation-events", "eventsResponse", body, "eventsRequest", token);
            var ack = ((JsonArray)reply["results"]!).OfType<JsonObject>().Single();
            if (ack.Text("eventId") != evt.Text("eventId") || ack.Text("status") == "REJECTED" || ack.Number("acceptedSequence") != evt.Number("sequence")) throw new NpepException("INVALID_RESPONSE");
            item["ack"] = true; _controlJournal!.Save(id, item);
        }
        if (item["resolved"]?.GetValue<bool>() != true && _control!.LocallyEnded(operationId))
        {
            if (item["resolution"] is not JsonObject)
            {
                var evidence = (JsonObject)evt["evidence"]!.DeepClone(); evidence["remoteExamPause"] = false; evidence["observedAt"] = NpepRuntimeProtocol.UtcNow();
                item["resolution"] = new JsonObject { ["resolutionId"] = NpepProtocol.Id(), ["expectedLastEventSequence"] = evt["sequence"]!.DeepClone(),
                    ["kind"] = "LOCAL_END", ["noPendingActions"] = true, ["evidence"] = evidence, ["occurredAt"] = NpepRuntimeProtocol.UtcNow() };
                _controlJournal!.Save(id, item);
            }
            var body = ((JsonObject)item["resolution"]!).Copy(); body["requestId"] = NpepProtocol.Id(); body["context"] = context.Copy();
            await _device!.ControlAsync($"device/runtime-operations/{id}/resolve", "operation", body, "resolveRequest", token);
            item["resolved"] = true; _controlJournal!.Save(id, item);
        }
    }
}
