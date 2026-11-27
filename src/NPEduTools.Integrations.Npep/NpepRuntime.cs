using System.Diagnostics;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

/// <summary>One Host owns credentials, UI operations and reporting. Snapshots never wait on network I/O.</summary>
public sealed partial class NpepRuntime : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<HostResponse> _sample;
    private readonly string _appVersion;
    private readonly NpepDevice? _device;
    private readonly TimeProvider _receiptTime;
    private readonly Queue<Guid> _requests = new();
    private NpepState _state = new(0, "UNPAIRED", "DISABLED", "尚未连接学校服务。只有完成配对后才会上报设备状态。");
    private Task _command = Task.CompletedTask;
    private readonly Task _loop;
    private CancellationTokenSource? _network;
    private JsonObject? _verified;
    private string? _verifiedOrigin;
    private JsonObject? _catalogue;
    private long _attemptAt;
    private long? _receivedAt;
    private double _delay;
    private int _failures;
    private bool _blocked, _disposed;
    private bool _continuingStudyResume;
    private bool ContinuingStudyResume { get { lock (_sync) return _continuingStudyResume; } }
    private NpepCommand? _tlsRetryCommand;
    private readonly NpepConnectionJournal _history;
    private bool _connectionFailed;

    public NpepRuntime(string directory, string appVersion, Func<HostResponse> sample, INpepRuntimeControl? control = null, INpepExamPlans? plans = null, INpepNoise? noise = null, INpepNoiseSchedules? schedules = null, INpepStudySettings? study = null, INpepSchoolManagement? schoolManagement = null, ISchoolUpdateHost? updates = null)
        : this(() => new NpepDevice(directory), appVersion, sample, control, plans, noise, schedules, study, schoolManagement, updates: updates) { }
    internal NpepRuntime(Func<NpepDevice> factory, string appVersion, Func<HostResponse> sample, INpepRuntimeControl? control = null, INpepExamPlans? plans = null, INpepNoise? noise = null, INpepNoiseSchedules? schedules = null, INpepStudySettings? study = null, INpepSchoolManagement? schoolManagement = null, TimeProvider? receiptTime = null, ISchoolUpdateHost? updates = null)
    {
        _sample = sample; _appVersion = appVersion; _schoolManagement = schoolManagement;
        _receiptTime = receiptTime ?? TimeProvider.System;
        try
        {
            _device = factory();
            _controlPolicy = new(_device.DataDirectory);
            _planPolicy = new(_device.DataDirectory, "exam-plan-policy.json");
            _blocked = _device.View()["pairingError"] is not null;
            Publish("WAITING", _device.View().Text("state") switch
            {
                "UNPAIRED" => "尚未连接学校服务。只有完成配对后才会上报设备状态。",
                "ACTIVE" => "已读取原配对，正在检查授权并恢复连接。",
                "PENDING" => _device.View()["pairingSource"]?.GetValue<string>() == "SCREEN" ? "已恢复网页配对，正在读取学校预授权。" : "已恢复配对申请，等待管理员批准。",
                "APPROVED" => "学校已授权，请核对学校、班级和大屏后在本机确认。",
                "SUSPENDED" => "原连接已停用，请解除旧绑定并重新配对。",
                _ => "存在未完成的操作，请恢复原操作或解除绑定；不会创建新的配对。"
            });
        }
        catch (Exception e) when (StorageError(e))
        {
            _schoolManagement?.ObserveActionBinding(null);
            _state = _state with { State = "STORE_UNAVAILABLE", Connection = "DISABLED", Error = "CREDENTIAL_STORE_UNAVAILABLE", Message = "凭据无法读取或已被另一实例占用。请关闭冲突实例后重启后台；原文件已保留。" };
        }
        _history = new(_device?.DataDirectory);
        if (_device is not null) _device.Observation = item => _history.Add(item.Kind, item.Endpoint, item.Code, item.HttpStatus, item.RequestId, item.ElapsedMs, item.Retrying);
        _loop = Task.Run(LoopAsync);
        InitializeNotifications();
        InitializeControl(control);
        InitializePlans(plans);
        InitializeNoise(noise);
        InitializeNoiseSchedules(schedules);
        InitializeNoiseDisplay(noise as INpepNoiseDisplay);
        InitializeStudy(study);
        InitializeUpdates(updates);
    }

    public NpepState Snapshot()
    {
        lock (_sync)
        {
            var result = _state;
            bool statusDependsOnReceipt = result.Connection == "ONLINE" ||
                _receivedAt is not null && result is { Connection: "OFFLINE", Error: "SAMPLE_TOO_OLD" };
            if (statusDependsOnReceipt && !HasFreshStatusReceipt())
                result = result with { Connection = "OFFLINE", Error = null, Message = "最近 60 秒未收到新的上报回执；显示的是上次观测。" };
            return result with { Server = result.Server?.Copy(), Pairing = result.Pairing?.Copy(), Approval = result.Approval?.Copy(),
                Catalogue = result.State == "UNPAIRED" ? _catalogue?.Copy() : result.Catalogue?.Copy() };
        }
    }

    // Used under _sync. The display time from the server is independent of monotonic age.
    private bool HasFreshStatusReceipt() => _state.LastReceivedAt is not null && _receivedAt is { } timestamp &&
        _receiptTime.GetElapsedTime(timestamp) < TimeSpan.FromSeconds(60);

    public HostResponse Handle(HostRequest request)
    {
        if (request.Capability == "npep.diagnostics") return new(Protocol.Version, request.RequestId, "Succeeded", null,
            "脱敏互联失败历史；不代表当前执行或上传回执。", ConnectionHistory: _history.Snapshot());
        if (request.Capability == "npep.notifications") return HandleNotifications(request);
        lock (_sync)
        {
            HostResponse Reply(string outcome, string? error, string message) => new(Protocol.Version, request.RequestId, outcome, error, message, Npep: Snapshot());
            if (request.Capability == "npep.status") return Reply("Succeeded", null, "学校互联状态");
            if (_disposed || _device is null) return Reply("Rejected", "NpepUnavailable", _state.Message);
            if (_requests.Contains(request.RequestId)) return Reply("Accepted", null, "该操作已经受理，请查看状态。");
            var c = request.Npep;
            if (c is null || !NpepContract.Valid(c)) return Reply("Rejected", "InvalidNpepCommand", "互联操作参数不正确。");
            if (_state.Busy || c.Revision != _state.Revision) return Reply("Rejected", "NpepStateChanged", "状态已更新或操作仍在进行，请刷新后重试。");
            if (c.Action == "replacement-class" && (_state.State != "CREATING" ||
                _state.Error is not ("BINDING_OCCUPIED" or "MANAGEMENT_SECRET_INVALID") ||
                c.ScreenBindingId != _state.RequestedScreenBindingId || c.DeviceName != _state.RequestedDeviceName ||
                NpepApi.ValidateOrigin(c.Origin!) != _state.Origin ||
                c.ServerInstanceId != _state.Server?["serverInstanceId"]?.GetValue<string>() ||
                c.DeploymentEpoch != _state.Server?["deploymentEpoch"]?.GetValue<string>()))
                return Reply("Rejected", "NpepStateChanged", "请先核对被占用的大屏，再提交该大屏的换机申请。");
            if (c.Action == "find-class")
            {
                if (_state is not { State: "UNPAIRED", Connection: "VERIFIED" } || !MatchesVerified(c) ||
                    _catalogue?["supportsLookup"]?.GetValue<bool>() != true)
                    return Reply("Rejected", "NpepStateChanged", "请重新检查学校服务后再查找班级。");
                _catalogue = new() { ["supportsLookup"] = true, ["schoolQuery"] = c.SchoolQuery!.Trim(),
                    ["classQuery"] = c.ClassQuery!.Trim(), ["after"] = c.CatalogueAfter, ["nextCursor"] = null,
                    ["serverInstanceId"] = c.ServerInstanceId, ["deploymentEpoch"] = c.DeploymentEpoch,
                    ["items"] = new JsonArray(), ["truncated"] = false };
            }
            else
            {
                _controlPolicy?.Rotate(); // Invalidate queued control intents before pause/resume/unpair work starts.
                _planPolicy?.Rotate();
                _continuingStudyResume = c.Action == "resume" && _state.State == "ACTIVE" && !_state.ReportingPaused;
                if (!_continuingStudyResume) _study?.BindStudy(null);
                _controlCancellation?.Cancel();
                _network?.Cancel();
                _notificationNetwork?.Cancel();
                _inbox?.Status("WAITING", "正在更新互联状态，完成后重新核对通知。");
                Interlocked.Exchange(ref _notificationWake, 1);
            }
            _requests.Enqueue(request.RequestId); if (_requests.Count > 128) _requests.Dequeue();
            _state = _state with { Revision = _state.Revision + 1, Busy = true, OperationId = request.RequestId, Error = null, Message = "正在处理，请稍候…" };
            _command = Task.Run(() => CommandAsync(c));
            return Reply("Accepted", null, "已受理；网络操作在后台完成，关闭此页面不会取消操作。");
        }
    }

    private async Task CommandAsync(NpepCommand c)
    {
        await _work.WaitAsync();
        try
        {
            if (c.Action == "find-class")
            {
                await FindClassAsync(c, _lifetime.Token);
                return;
            }
            _blocked = false;
            _tlsRetryCommand = null; // An explicit user action supersedes the previous retry.
            await PerformCommandAsync(c, _lifetime.Token);
        }
        catch (Exception e) when (Handled(e))
        {
            if (IsTlsFailure(e)) _tlsRetryCommand = c;
            Failure(e);
        }
        finally
        {
            _attemptAt = Stopwatch.GetTimestamp();
            lock (_sync) { _state = _state with { Busy = false, Revision = _state.Revision + 1 }; _continuingStudyResume = false; }
            _work.Release();
        }
    }

    private bool MatchesVerified(NpepCommand c) => _verified is not null && NpepApi.ValidateOrigin(c.Origin!) == _verifiedOrigin &&
        c.ServerInstanceId == _verified.Text("serverInstanceId") && c.DeploymentEpoch == _verified.Text("deploymentEpoch");

    private async Task FindClassAsync(NpepCommand c, CancellationToken token)
    {
        try
        {
            var page = await _device!.FindClassScreensAsync(c.Origin!, _verified!, c.SchoolQuery!, c.ClassQuery!, c.CatalogueAfter, token);
            lock (_sync) _catalogue = page;
            Publish("VERIFIED", page["items"]!.AsArray().Count == 0 ? "该学校和班级下没有可选大屏。" : "请选择本教室的大屏。");
        }
        catch (NpepException e) when (e.Code == "INSTANCE_MISMATCH")
        {
            lock (_sync) { _verified = null; _verifiedOrigin = null; _catalogue = null; }
            Publish("DISABLED", "服务实例已变更，请重新检查学校服务。", e.Code);
        }
        catch (Exception e) when (Handled(e))
        {
            string code = IsTlsFailure(e) ? "TLS_VALIDATION_FAILED" : (e as NpepException)?.Code ?? "NETWORK_UNAVAILABLE";
            Publish("VERIFIED", (e as NpepException)?.Status == 429 ? "查找过于频繁，请稍后重试。" : "班级查找失败，请重试或重新检查服务。", code);
        }
    }

    private async Task PerformCommandAsync(NpepCommand c, CancellationToken token)
    {
        switch (c.Action)
        {
            case "inspect":
                if (_device!.View().Text("state") != "UNPAIRED") throw new NpepException("PAIRING_ALREADY_EXISTS");
                _verified = null; _verifiedOrigin = null; _catalogue=null;
                var info = await _device.InspectServerAsync(c.Origin!, token);
                JsonObject? preview = null;
                try
                {
                    preview = await _device.ClassCatalogueAsync(c.Origin!, token);
                    NpepDevice.RequireCatalogueIdentity(preview, info);
                }
                catch (NpepException e) when (e.Status is 404 or 426) { }
                lock (_sync) { _verified = info; _verifiedOrigin = NpepApi.ValidateOrigin(c.Origin!); _catalogue = preview; }
                Publish("VERIFIED", "服务可连接。请核对服务地址与实例标识，再输入网页配对码或创建配对申请。"); break;
            case "apply-class":
            case "password-class":
                if (!MatchesVerified(c)) throw new NpepException("LOCAL_CONFIRMATION_REQUIRED");
                var selected = (_catalogue?["items"] as JsonArray)?.OfType<JsonObject>()
                    .FirstOrDefault(row => row.Text("screenBindingId") == c.ScreenBindingId);
                if (_catalogue is not null && selected is null) throw new NpepException("LOCAL_CONFIRMATION_REQUIRED");
                await _device!.BeginClassAsync(c.Origin!, _verified!, c.DeviceName!, _appVersion, c.ScreenBindingId!, c.ManagementSecret,
                    token, selected?.Text("schoolId"), selected?.Text("classId"), selected is null ? null :
                        string.Join(" · ", new[] { "schoolName", "className", "screenName" }.Select(key => selected[key]?.GetValue<string>()).Where(name => !string.IsNullOrWhiteSpace(name))));
                await _device.PollApprovalAsync(token);await _device.ConfirmRequestedClassAsync(token);
                Publish("WAITING",c.Action=="apply-class"?"已申请指定班级；等待学校管理员批准并设置口令。":"已核对预授权口令，正在连接。");break;
            case "replacement-class":
                await _device!.RequestReplacementAsync(c, token);
                Publish("WAITING", "已提交换机申请，等待学校管理员审核。旧设备继续运行。"); break;
            case "pair":
            case "claim":
                if (_verified is null || NpepApi.ValidateOrigin(c.Origin!) != _verifiedOrigin ||
                    c.ServerInstanceId != _verified.Text("serverInstanceId") || c.DeploymentEpoch != _verified.Text("deploymentEpoch")) throw new NpepException("LOCAL_CONFIRMATION_REQUIRED");
                if (c.Action == "claim")
                {
                    await _device!.ClaimScreenPairingAsync(c.Origin!, _verified, c.DeviceName!, _appVersion, c.UserCode!, token);
                    await _device.PollApprovalAsync(token);
                    Publish("WAITING", "已读取学校预授权。请核对学校、班级与大屏，并在本机确认连接。");
                }
                else
                {
                    await _device!.BeginAsync(c.Origin!, _verified, c.DeviceName!, _appVersion, token);
                    Publish("WAITING", "请学校管理员在 NPClassworks 中输入配对码，选择对应班级的大屏。");
                }
                break;
            case "poll": await _device!.PollApprovalAsync(token); await _device.ConfirmRequestedClassAsync(token); Publish("WAITING", "审批信息已更新，请核对后在本机确认。"); break;
            case "confirm": await _device!.ConfirmAsync(c.ApprovalId!, token); Publish("CONNECTING", "配对完成，正在发送首次状态。"); break;
            case "recover": await _device!.RecoverConfirmationAsync(token); Publish("CONNECTING", "已恢复原配对，正在连接。"); break;
            case "resume-create": await _device!.ResumeCreateAsync(token); Publish("WAITING", "已恢复原配对申请。"); break;
            case "pause": await _device!.SetReportingPausedAsync(true, token); Publish("PAUSED", "已暂停互联（通知与状态）。学校端会在在线窗口结束后显示离线；配对保留。"); break;
            case "resume": await _device!.SetReportingPausedAsync(false, token); Publish("CONNECTING", "正在恢复通知接收与状态上报。"); break;
            case "unpair":
                var leaving = _device!.View();
                bool cancellingCandidate = leaving["pairingSource"]?.GetValue<string>() == "REPLACEMENT" &&
                    leaving.Text("state") is "CREATING" or "PENDING" or "APPROVED";
                string outcome = await _device!.UnpairAsync(token);
                _updates?.Unpair();
                _schoolManagement?.BindSchool(null);
                _verified = null; _verifiedOrigin = null; _catalogue = null;
                string result = outcome switch {
                    "CANCELLED" => "本机配对申请已清除。",
                    "LOCAL_ONLY" when cancellingCandidate => "本机换机申请已清除。若学校仍显示待审，请在 Console 核对此申请。",
                    "LOCAL_ONLY" => "本机已解绑，但无法确认远端撤销。请学校管理员检查这台设备的登记。",
                    _ => "已解除绑定并停止上报。"
                };
                string? cleanupError = outcome == "LOCAL_ONLY" ? (cancellingCandidate ? "REPLACEMENT_CANCEL_UNCONFIRMED" : "LOCAL_ONLY") : null;
                Publish("DISABLED", result, cleanupError); break;
        }
        _failures = 0; _delay = c.Action is "pair" or "poll" or "resume-create" ? 5 : 0;
    }

    private async Task LoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(250, _lifetime.Token); } catch (OperationCanceledException) { break; }
            if (_device is null || !await _work.WaitAsync(0)) continue;
            bool attempted = false;
            try
            {
                lock (_sync)
                {
                    if (_state.Busy || _blocked || _disposed || Stopwatch.GetElapsedTime(_attemptAt).TotalSeconds < _delay) continue;
                    _network = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                }
                var view = _device.View();
                string stage = view.Text("state");
                if (_tlsRetryCommand is { } pending)
                {
                    attempted = true;
                    // Recover the persisted candidate instead of creating another pairing/secret.
                    string action = stage switch { "CREATING" => "resume-create", "CONFIRMING" => "recover", _ => pending.Action };
                    await PerformCommandAsync(pending with { Action = action }, _network.Token);
                    _tlsRetryCommand = null;
                }
                else if (stage == "PENDING" || stage == "APPROVED" && view["pairingSource"]?.GetValue<string>() == "REPLACEMENT")
                {
                    attempted = true;
                    await _device.PollApprovalAsync(_network.Token);
                    await _device.ConfirmRequestedClassAsync(_network.Token);
                    Publish("WAITING", _device.View().Text("state") == "APPROVED" ? "学校已授权。请核对学校、班级和大屏，再点击确认连接。" : view["pairingSource"]?.GetValue<string>() == "SCREEN" ? "正在读取学校预授权…" : "等待学校管理员批准…");
                    _delay = 5; _failures = 0;
                }
                else if(stage=="APPROVED" && view["pairingSource"]?.GetValue<string>() is "CLASS_APPLY" or "CLASS_PASSWORD")
                {attempted=true;await _device.ConfirmRequestedClassAsync(_network.Token);Publish("CONNECTING","指定班级授权已确认，正在连接。");_delay=0;}
                else if (stage == "ACTIVE" && view["reportingPaused"]?.GetValue<bool>() != true)
                {
                    attempted = true;
                    await _device.OpenSessionAsync(_network.Token);
                    var receipt = await _device.ReportAsync(() =>
                    {
                        long sampledAt = Stopwatch.GetTimestamp();
                        return new NpepSample(NpepStatusReader.Map(_sample(), _appVersion), sampledAt);
                    }, _network.Token);
                    Publish("ONLINE", "学校服务已收到最新状态。连接随后台运行，主窗口收起不影响上报。", receivedAt: receipt.Text("receivedAt"));
                    _delay = 20; _failures = 0;
                }
            }
            catch (OperationCanceledException) when (_network?.IsCancellationRequested == true) { }
            catch (Exception e) when (Handled(e)) { Failure(e, background: true); }
            finally
            {
                lock (_sync) { _network?.Dispose(); _network = null; }
                if (attempted) _attemptAt = Stopwatch.GetTimestamp();
                _work.Release();
            }
        }
    }

    private void Publish(string connection, string message, string? error = null, string? receivedAt = null, bool statusSampleExpired = false,
        bool requireSuspended = false)
    {
        SynchronizeActionBinding();
        long? receiptTimestamp = receivedAt is not null ? _receiptTime.GetTimestamp() : null;
        lock (_sync)
        {
            var view = _device!.View(); string stage = view.Text("state");
            // A heartbeat received just before notification-driven suspension cannot restore an online message.
            if ((requireSuspended && stage != "SUSPENDED") || (connection == "ONLINE" && stage == "SUSPENDED")) return;
            bool paused = view["reportingPaused"]?.GetValue<bool>() == true;
            bool canReport = stage == "ACTIVE" && !paused && view["suspended"]?.GetValue<bool>() != true;
            // Discarding a local sample gives no new evidence about the school connection.
            if (statusSampleExpired && canReport && _state is { State: "ACTIVE", ReportingPaused: false, Busy: false })
            {
                if (_state.Connection == "ONLINE" && HasFreshStatusReceipt())
                    connection = "ONLINE";
                else if (_state.Connection == "OFFLINE" && _state.Error is not (null or "SAMPLE_TOO_OLD"))
                {
                    // Keep the known failure visible until the next real transport observation.
                    error = _state.Error;
                    message = _state.Message;
                }
            }
            bool receivedNewStatus = canReport && connection == "ONLINE" && receiptTimestamp is not null;
            if (receivedNewStatus) _receivedAt = receiptTimestamp;
            else if (!canReport) _receivedAt = null;
            string? lastReceivedAt = receivedNewStatus ? receivedAt : _state.LastReceivedAt;
            SynchronizeControlScope(view, connection);
            bool changed = _state.State != stage || _state.ReportingPaused != paused;
            _state = _state with
            {
                Revision = _state.Revision + (changed ? 1 : 0), State = stage,
                Connection = paused ? "PAUSED" : stage is "SUSPENDED" or "UNPAIRING" ? "STOPPED" : connection,
                Message = paused ? "已暂停互联（通知与状态），配对保留。恢复后会重新检查授权。" : message,
                Origin = view["origin"]?.GetValue<string>() ?? _verifiedOrigin, Server = view["requestedServer"] as JsonObject ?? _verified?.Copy(),
                Pairing = view["pairing"] as JsonObject, Approval = view["approval"] as JsonObject,
                ReportingPaused = paused, LastReceivedAt = stage == "UNPAIRED" ? null : lastReceivedAt, Error = error,
                PairingSource = view["pairingSource"]?.GetValue<string>(),
                Catalogue = view["replacementTarget"] is JsonObject target ? new JsonObject { ["replacementTarget"] = target.Copy() } : null
            };
            if (error is null && view["pairingError"] is JsonValue rejection)
                _state = _state with { Connection = "STOPPED", Error = rejection.GetValue<string>() };
            if (receivedNewStatus && _connectionFailed) { _history.Add("Recovered", "Status"); _connectionFailed = false; }
            if (connection is "DISABLED" or "PAUSED") _connectionFailed = false;
        }
    }
    private void Failure(Exception e, bool background = false)
    {
        bool tls = IsTlsFailure(e);
        string code = tls ? "TLS_VALIDATION_FAILED" : e is NpepException n ? n.Code : e is HttpRequestException or OperationCanceledException ? "NETWORK_UNAVAILABLE" : "CREDENTIAL_STORE_UNAVAILABLE";
        bool localSampleExpired = e is NpepException { Code: "SAMPLE_TOO_OLD", Status: 0 };
        bool unconfirmedDeviceResponse = e is NpepException { Code: "DEVICE_RESPONSE_UNCONFIRMED" };
        bool retry = localSampleExpired || unconfirmedDeviceResponse || tls || e is HttpRequestException or OperationCanceledException ||
            e is NpepException problem && !NpepDevice.TerminalConnectionError(problem) && (problem.Status is 429 or >= 500);
        _blocked = !retry;
        _connectionFailed = true;
        _history.Add("Failure", background ? "Status" : "Command", code, (e as NpepException)?.Status is > 0 and <= 599 ? ((NpepException)e).Status : null, retrying: retry);
        // A sample discarded before sending can be replaced promptly. Repeated
        // failures still back off. Only a new receipt can renew status freshness.
        int failures = _failures++;
        int backoff = localSampleExpired && failures == 0 ? 1 : Math.Min(60, 5 * (1 << Math.Min(failures, 4)));
        _delay = Math.Max(backoff, (e as NpepException)?.RetryAfterSeconds ?? 0) * (1 + Random.Shared.NextDouble() * .2);
        if (localSampleExpired)
        {
            Publish("OFFLINE", "本机状态样本已过期，本次未上报；后台会重新采样。", code,
                statusSampleExpired: background);
            return;
        }
        string stopped = code switch
        {
            "PAIRING_CANCELLED" => "原申请已取消或被学校拒绝。可以清除本机申请后重新接入。",
            "REPLACEMENT_UNSUPPORTED" => "学校服务暂不支持换机，请联系学校管理员升级 KV。",
            "PAIRING_EXPIRED" => "配对码已过期。请取消本次配对后重新创建；不会自动延长期限。",
            "PAIRING_CODE_UNAVAILABLE" => "网页配对码已使用、过期或失效。请取消本次申请，再在班级大屏网页生成新码。",
            "SCREEN_PAIRING_DISABLED" or "PREAUTHORIZATION_CHANGED" => "学校预授权已关闭或变更。请取消本次申请，联系管理员核对后重新生成配对码。",
            "AUTH_INVALID" or "CREDENTIAL_EXPIRED" => "原授权已失效，请与学校管理员核对后解除旧绑定并重新配对。",
            "INSTANCE_MISMATCH" or "BINDING_CHANGED" => "学校服务或设备归属发生变化，连接已停止。请核对归属后重新配对。",
            "SESSION_SUPERSEDED" or "REVISION_CONFLICT" => "当前会话已被替换。请检查是否有另一实例使用该配对；此实例不会自动抢回会话。",
            _ => "互联操作未完成或授权已失效，请查看错误并处理；不会自动重新配对。"
        };
        Publish(retry ? "OFFLINE" : "STOPPED", tls ? "HTTPS 证书验证失败，将持续自动重试，证书恢复后自动连接。请核对学校地址、本机系统时间及服务器证书；每次重试仍严格验证证书。" : unconfirmedDeviceResponse
            ? "未能核实学校互联回执。后台会保留原配对并自动重试。" : retry
            ? background ? "暂时无法连接学校服务，将自动重试。配对信息保留。" : "网络操作结果尚未确认。请刷新状态，恢复未完成操作或重试检查服务；原候选凭据保留。"
            : stopped, code);
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or NpepException or System.Text.Json.JsonException;
    private static bool IsTlsFailure(Exception e) => e is System.Security.Authentication.AuthenticationException or
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } ||
        e.InnerException is { } inner && IsTlsFailure(inner);
    private static bool Handled(Exception e) => StorageError(e) || e is HttpRequestException or OperationCanceledException or System.Security.Authentication.AuthenticationException;
    public async ValueTask DisposeAsync()
    {
        Task command;
        lock (_sync) { _disposed = true; _lifetime.Cancel(); _network?.Cancel(); command = _command; }
        await Task.WhenAll(_loop, command, _notificationLoop, _planLoop, _noiseLoop, _noiseScheduleLoop, _noiseDisplayLoop, _studyLoop, _studySeatingLoop, _studyHomeworkLoop, _studyPresentationLoop, _studyPhotosLoop, _studyExitLoop);
        await Task.WhenAll(_controlLoop, _softwareUpdateLoop, _schoolManagementLoop);
        await _history.DisposeAsync();
        _device?.Dispose(); _work.Dispose(); _updateWake.Dispose(); _lifetime.Dispose();
    }
}
