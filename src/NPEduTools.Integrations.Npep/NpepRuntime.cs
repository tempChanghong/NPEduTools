using System.Diagnostics;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.Integrations.Npep;

/// <summary>One Host owns credentials, UI operations and reporting. Snapshots never wait on network I/O.</summary>
public sealed class NpepRuntime : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<HostResponse> _sample;
    private readonly string _appVersion;
    private readonly NpepDevice? _device;
    private readonly Queue<Guid> _requests = new();
    private NpepState _state = new(0, "UNPAIRED", "DISABLED", "尚未连接学校服务。只有完成配对后才会上报设备状态。");
    private Task _command = Task.CompletedTask;
    private readonly Task _loop;
    private CancellationTokenSource? _network;
    private JsonObject? _verified;
    private string? _verifiedOrigin;
    private long _attemptAt, _receivedAt;
    private double _delay;
    private int _failures;
    private bool _blocked, _disposed;

    public NpepRuntime(string directory, string appVersion, Func<HostResponse> sample)
        : this(() => new NpepDevice(directory), appVersion, sample) { }
    internal NpepRuntime(Func<NpepDevice> factory, string appVersion, Func<HostResponse> sample)
    {
        _sample = sample; _appVersion = appVersion;
        try
        {
            _device = factory();
            Publish("WAITING", _device.View().Text("state") switch
            {
                "UNPAIRED" => "尚未连接学校服务。只有完成配对后才会上报设备状态。",
                "ACTIVE" => "已读取原配对，正在检查授权并恢复连接。",
                "PENDING" => "已恢复配对申请，等待管理员批准。",
                "APPROVED" => "管理员已批准，请核对学校、班级和大屏后在本机确认。",
                "SUSPENDED" => "原连接已停用，请解除旧绑定并重新配对。",
                _ => "存在未完成的操作，请恢复原操作或解除绑定；不会创建新的配对。"
            });
        }
        catch (Exception e) when (StorageError(e))
        { _state = _state with { State = "STORE_UNAVAILABLE", Connection = "DISABLED", Error = "CREDENTIAL_STORE_UNAVAILABLE", Message = "凭据无法读取或已被另一实例占用。请关闭冲突实例后重启后台；原文件已保留。" }; }
        _loop = Task.Run(LoopAsync);
    }

    public NpepState Snapshot()
    {
        lock (_sync)
        {
            var result = _state;
            if (result.Connection == "ONLINE" && Stopwatch.GetElapsedTime(_receivedAt) >= TimeSpan.FromSeconds(60))
                result = result with { Connection = "OFFLINE", Message = "最近 60 秒未收到新的上报回执；显示的是上次观测。" };
            return result with { Server = result.Server?.Copy(), Pairing = result.Pairing?.Copy(), Approval = result.Approval?.Copy() };
        }
    }

    public HostResponse Handle(HostRequest request)
    {
        lock (_sync)
        {
            HostResponse Reply(string outcome, string? error, string message) => new(Protocol.Version, request.RequestId, outcome, error, message, Npep: Snapshot());
            if (request.Capability == "npep.status") return Reply("Succeeded", null, "学校互联状态");
            if (_disposed || _device is null) return Reply("Rejected", "NpepUnavailable", _state.Message);
            if (_requests.Contains(request.RequestId)) return Reply("Accepted", null, "该操作已经受理，请查看状态。");
            var c = request.Npep;
            if (c is null || !NpepContract.Valid(c)) return Reply("Rejected", "InvalidNpepCommand", "互联操作参数不正确。");
            if (_state.Busy || c.Revision != _state.Revision) return Reply("Rejected", "NpepStateChanged", "状态已更新或操作仍在进行，请刷新后重试。");
            _requests.Enqueue(request.RequestId); if (_requests.Count > 128) _requests.Dequeue();
            _state = _state with { Revision = _state.Revision + 1, Busy = true, OperationId = request.RequestId, Error = null, Message = "正在处理，请稍候…" };
            _network?.Cancel();
            _command = Task.Run(() => CommandAsync(c));
            return Reply("Accepted", null, "已受理；网络操作在后台完成，关闭此页面不会取消操作。");
        }
    }

    private async Task CommandAsync(NpepCommand c)
    {
        await _work.WaitAsync();
        try
        {
            var token = _lifetime.Token;
            _blocked = false;
            switch (c.Action)
            {
                case "inspect":
                    if (_device!.View().Text("state") != "UNPAIRED") throw new NpepException("PAIRING_ALREADY_EXISTS");
                    _verified = null; _verifiedOrigin = null;
                    var info = await _device.InspectServerAsync(c.Origin!, token);
                    _verified = info; _verifiedOrigin = NpepApi.ValidateOrigin(c.Origin!);
                    Publish("VERIFIED", "服务可连接。请核对服务地址与实例标识，再创建配对码。"); break;
                case "pair":
                    if (_verified is null || NpepApi.ValidateOrigin(c.Origin!) != _verifiedOrigin ||
                        c.ServerInstanceId != _verified.Text("serverInstanceId") || c.DeploymentEpoch != _verified.Text("deploymentEpoch")) throw new NpepException("LOCAL_CONFIRMATION_REQUIRED");
                    await _device!.BeginAsync(c.Origin!, _verified, c.DeviceName!, _appVersion, token);
                    Publish("WAITING", "请学校管理员在 NPClassworks 中输入配对码，选择对应班级的大屏。"); break;
                case "poll": await _device!.PollApprovalAsync(token); Publish("WAITING", "审批信息已更新，请核对后在本机确认。"); break;
                case "confirm": await _device!.ConfirmAsync(c.ApprovalId!, token); Publish("CONNECTING", "配对完成，正在发送首次状态。"); break;
                case "recover": await _device!.RecoverConfirmationAsync(token); Publish("CONNECTING", "已恢复原配对，正在连接。"); break;
                case "resume-create": await _device!.ResumeCreateAsync(token); Publish("WAITING", "已恢复原配对申请。"); break;
                case "pause": await _device!.SetReportingPausedAsync(true, token); Publish("PAUSED", "已暂停上报。学校端会在在线窗口结束后显示离线；配对保留。"); break;
                case "resume": await _device!.SetReportingPausedAsync(false, token); Publish("CONNECTING", "正在恢复状态上报。"); break;
                case "unpair":
                    string outcome = await _device!.UnpairAsync(token);
                    _verified = null; _verifiedOrigin = null; _receivedAt = 0;
                    Publish("DISABLED", outcome == "LOCAL_ONLY" ? "本机已解绑，但无法确认远端撤销。请学校管理员检查并撤销该设备。" : "已解除绑定并停止上报。", outcome == "LOCAL_ONLY" ? "LOCAL_ONLY" : null); break;
            }
            _failures = 0; _delay = c.Action is "pair" or "poll" or "resume-create" ? 5 : 0;
        }
        catch (Exception e) when (Handled(e)) { Failure(e); }
        finally
        {
            _attemptAt = Stopwatch.GetTimestamp();
            lock (_sync) _state = _state with { Busy = false, Revision = _state.Revision + 1 };
            _work.Release();
        }
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
                if (stage == "PENDING")
                {
                    attempted = true;
                    await _device.PollApprovalAsync(_network.Token);
                    Publish("WAITING", _device.View().Text("state") == "APPROVED" ? "管理员已批准。请核对学校、班级和大屏，再点击确认连接。" : "等待学校管理员批准…");
                    _delay = 5; _failures = 0;
                }
                else if (stage == "ACTIVE" && view["reportingPaused"]?.GetValue<bool>() != true)
                {
                    attempted = true;
                    await _device.OpenSessionAsync(_network.Token);
                    long sampledAt = Stopwatch.GetTimestamp();
                    var sample = new NpepSample(NpepStatusReader.Map(_sample(), _appVersion), sampledAt);
                    var receipt = await _device.ReportAsync(sample, _network.Token);
                    _receivedAt = Stopwatch.GetTimestamp();
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

    private void Publish(string connection, string message, string? error = null, string? receivedAt = null)
    {
        var view = _device!.View(); string stage = view.Text("state");
        bool paused = view["reportingPaused"]?.GetValue<bool>() == true;
        lock (_sync)
        {
            bool changed = _state.State != stage || _state.ReportingPaused != paused;
            _state = _state with
            {
                Revision = _state.Revision + (changed ? 1 : 0), State = stage,
                Connection = paused ? "PAUSED" : stage is "SUSPENDED" or "UNPAIRING" ? "STOPPED" : connection,
                Message = paused ? "已暂停上报，配对保留。恢复后会重新检查授权。" : message,
                Origin = view["origin"]?.GetValue<string>() ?? _verifiedOrigin, Server = _verified?.Copy(),
                Pairing = view["pairing"] as JsonObject, Approval = view["approval"] as JsonObject,
                ReportingPaused = paused, LastReceivedAt = stage == "UNPAIRED" ? null : receivedAt ?? _state.LastReceivedAt, Error = error
            };
        }
    }
    private void Failure(Exception e, bool background = false)
    {
        bool tls = e is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError };
        string code = tls ? "TLS_VALIDATION_FAILED" : e is NpepException n ? n.Code : e is HttpRequestException or OperationCanceledException ? "NETWORK_UNAVAILABLE" : "CREDENTIAL_STORE_UNAVAILABLE";
        bool retry = !tls && (e is HttpRequestException or OperationCanceledException || e is NpepException problem && (problem.Status == 429 || problem.Status >= 500));
        _blocked = !retry;
        int backoff = Math.Min(60, 5 * (1 << Math.Min(_failures++, 4)));
        _delay = Math.Max(backoff, (e as NpepException)?.RetryAfterSeconds ?? 0) * (1 + Random.Shared.NextDouble() * .2);
        string stopped = code switch
        {
            "PAIRING_EXPIRED" => "配对码已过期。请取消本次配对后重新创建；不会自动延长期限。",
            "AUTH_INVALID" or "CREDENTIAL_EXPIRED" => "原授权已失效，请与学校管理员核对后解除旧绑定并重新配对。",
            "INSTANCE_MISMATCH" or "BINDING_CHANGED" => "学校服务或设备归属发生变化，连接已停止。请核对归属后重新配对。",
            "SESSION_SUPERSEDED" or "REVISION_CONFLICT" => "当前会话已被替换。请检查是否有另一实例使用该配对；此实例不会自动抢回会话。",
            _ => "互联操作未完成或授权已失效，请查看错误并处理；不会自动重新配对。"
        };
        Publish(retry ? "OFFLINE" : "STOPPED", tls ? "无法建立可信的 HTTPS 连接。请核对学校地址、本机系统时间及服务器证书；程序不会跳过安全验证。" : retry
            ? background ? "暂时无法连接学校服务，将自动重试。配对信息保留。" : "网络操作结果尚未确认。请刷新状态，恢复未完成操作或重试检查服务；原候选凭据保留。"
            : stopped, code);
    }
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or NpepException or System.Text.Json.JsonException;
    private static bool Handled(Exception e) => StorageError(e) || e is HttpRequestException or OperationCanceledException;
    public async ValueTask DisposeAsync()
    {
        Task command;
        lock (_sync) { _disposed = true; _lifetime.Cancel(); _network?.Cancel(); command = _command; }
        await Task.WhenAll(_loop, command);
        _device?.Dispose(); _work.Dispose(); _lifetime.Dispose();
    }
}
