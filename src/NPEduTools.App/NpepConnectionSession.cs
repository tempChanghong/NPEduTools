using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;

namespace NPEduTools.App;

/// <summary>Shared settings/OOBE session. Host remains the sole owner of pairing and credentials.</summary>
public sealed class NpepConnectionSession(Func<HostRequest, CancellationToken, Task<HostResponse>> request,
    CancellationToken lifetime = default, Func<HostRequest, Task<HostRequest?>>? authorize = null) : INotifyPropertyChanged
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpepState? _state;
    private string _origin = "", _deviceName = "教室大屏";
    private string _userCode = "";
    public string UserCode { get => _userCode; set { _userCode = value; Changed(); } }
    private string? _serverSeen, _approvalSeen, _feedback;
    private bool _working, _mutating, _originEdited, _serverConfirmed, _bindingConfirmed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(""));
    private static string Text(JsonObject? value, string key) => value?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";

    public string Origin
    {
        get => _origin;
        set { _originEdited = true; if (_origin == value) return; _origin = value; _serverConfirmed = false; Changed(); }
    }
    public string DeviceName { get => _deviceName; set { _deviceName = value; Changed(); } }
    public bool ServerConfirmed { get => _serverConfirmed; set { _serverConfirmed = value; Changed(); } }
    public bool BindingConfirmed { get => _bindingConfirmed; set { _bindingConfirmed = value; Changed(); } }
    public bool IsWorking => _working || _state?.Busy == true;
    private bool Available => !_working && _state is { Busy: false };
    public bool HasPairing => _state?.State == "ACTIVE";
    public long? Revision => _state?.Revision;
    public bool CanFinish => Available && HasPairing;
    public bool CanRefresh => !_working;
    public bool ShowAddressEditor => _state is null || _state.State == "UNPAIRED";
    public bool ShowCurrentAddress => !ShowAddressEditor;
    public string ProviderHint => _state is null ? "后台状态尚未确认，可先填写地址；恢复后台连接后再检查服务。"
        : ShowCreate ? "请填写学校提供的服务地址，然后检查服务。"
        : "此地址属于当前申请或绑定。更换提供商需先取消申请或解除绑定，再输入新地址并重新配对。";
    public bool ShowCreate => _state?.State == "UNPAIRED";
    public bool ShowServer => ShowCreate && _state?.Server is not null;
    public bool ShowCode => _state?.State == "PENDING" && _state.PairingSource != "SCREEN";
    public bool ShowBinding => _state?.Approval is not null;
    public bool ShowConfirm => _state?.State == "APPROVED";
    public bool ShowActive => HasPairing;
    // Read-only polling must not disable a focused input while the user is typing.
    public bool CanEdit => !_mutating && (_state is null || _state is { Busy: false, State: "UNPAIRED" });
    public bool CanReviewBinding => !_mutating && _state is { Busy: false } && ShowConfirm;
    public bool CanInspect => Available && CanEdit && NpepContract.Valid(new("inspect", 0, Origin.Trim()));
    public bool CanPair => Available && CanEdit && ServerConfirmed && _state!.Server is { } server &&
        Uri.TryCreate(Origin.Trim(), UriKind.Absolute, out var origin) && origin.GetLeftPart(UriPartial.Authority) == _state.Origin &&
        NpepContract.Valid(new("pair", _state.Revision, Origin.Trim(), DeviceName.Trim(), Text(server, "serverInstanceId"), Text(server, "deploymentEpoch")));
    public bool CanConfirm => Available && ShowConfirm && BindingConfirmed &&
        NpepContract.Valid(new("confirm", _state!.Revision, ApprovalId: Text(_state.Approval, "approvalId")));
    private string NormalizedCode => UserCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    public bool CanClaim => CanPair && NpepContract.Valid(new("claim", _state!.Revision, Origin.Trim(), DeviceName.Trim(),
        Text(_state.Server, "serverInstanceId"), Text(_state.Server, "deploymentEpoch"), UserCode: NormalizedCode));
    public bool ShowRecover => _state?.State is "CREATING" or "CONFIRMING" ||
        _state?.State is "PENDING" or "ACTIVE" && _state.Connection == "STOPPED";
    public bool CanRecover => Available && ShowRecover;
    public bool CanPause => Available && ShowActive;
    public bool ShowUnpair => _state is not null && _state.State is not ("UNPAIRED" or "STORE_UNAVAILABLE");
    public bool CanUnpair => Available && ShowUnpair;
    public bool ShowChangeProvider => ShowUnpair && !string.IsNullOrEmpty(BoundOrigin);
    public string RecoverLabel => HasPairing ? "重新连接" : "恢复未完成操作";
    public string PauseLabel => _state?.ReportingPaused == true ? "恢复互联" : "暂停互联";
    public string UnpairLabel => _state?.State is "PENDING" or "APPROVED" or "CREATING" ? "取消配对申请" : "解除绑定";
    public string Stage => _state?.State switch
    {
        "UNPAIRED" => "1 / 4 · 检查学校服务", "CREATING" or "PENDING" => _state.PairingSource == "SCREEN" ? "2 / 4 · 读取学校预授权" : "2 / 4 · 等待学校批准",
        "APPROVED" or "CONFIRMING" => "3 / 4 · 核对归属并确认", "ACTIVE" => "4 / 4 · 已完成配对",
        _ => "连接学校 · 等待检查"
    };
    public string Title => _state is null ? "后台未连接" : _state.Error == "PAIRING_EXPIRED" ? "配对码已过期" : _state.State switch
    {
        "UNPAIRED" => "尚未配对", "CREATING" => "配对申请待恢复", "PENDING" => _state.PairingSource == "SCREEN" ? "正在读取学校预授权" : "等待管理员批准",
        "APPROVED" => "请在本机确认连接", "CONFIRMING" => "正在确认配对结果",
        "ACTIVE" => _state.ReportingPaused ? "已配对 · 互联已暂停" : _state.Connection == "ONLINE" ? "已配对 · 当前在线"
            : _state.Connection == "STOPPED" ? "已配对 · 连接已停止" : "已配对 · 当前未在线",
        "SUSPENDED" => "连接已停用", "UNPAIRING" => "解绑尚未完成", _ => "互联暂不可用"
    };
    public string Message => _feedback ?? _state?.Message ?? "无法读取后台状态。恢复后重新检查；旧状态不代表当前连接。";
    public string Error => _state?.Error is { } code ? "错误：" + code : "";
    public string Receipt => DateTimeOffset.TryParse(_state?.LastReceivedAt, out var value)
        ? "学校服务最近接收：" + value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + "（本机时区）" : "";
    public string ServerIdentity => _state?.Server is null ? "" : $"地址：{_state.Origin}\n实例：{Text(_state.Server, "serverInstanceId")}\n部署标识：{Text(_state.Server, "deploymentEpoch")}";
    public string PairingCode => Text(_state?.Pairing, "userCode");
    public string Expiry => "配对截止（学校服务器 UTC）：" + Text(_state?.Pairing, "expiresAt");
    public string Binding => $"学校：{Text(_state?.Approval, "schoolName")}\n班级：{Text(_state?.Approval, "administrativeClassName")}\n大屏：{Text(_state?.Approval, "screenBindingName")}";
    public string BoundOrigin => _state?.Origin ?? "";
    public string BindingIdentity => $"学校：{Text(_state?.Approval, "schoolId")}\n班级：{Text(_state?.Approval, "administrativeClassId")}\n大屏：{Text(_state?.Approval, "screenBindingId")}\n批准编号：{Text(_state?.Approval, "approvalId")}";

    private void Apply(HostResponse response)
    {
        _state = response.Npep;
        if (_state?.State is "APPROVED" or "ACTIVE") _userCode = "";
        string? server = _state?.Server?.ToJsonString();
        string? approval = _state?.Approval?.ToJsonString();
        if (server != _serverSeen) { _serverSeen = server; _serverConfirmed = false; }
        if (approval != _approvalSeen) { _approvalSeen = approval; _bindingConfirmed = false; }
        if (!_originEdited && _state?.Origin is { } saved) _origin = saved;
        _feedback = response.Outcome == "Rejected" || response.Npep is null ? response.Message : null;
        Changed();
    }

    public Task<bool> RefreshAsync() => RunAsync(null);
    public Task<bool> InspectAsync() => CanInspect ? RunAsync(new("inspect", _state!.Revision, Origin.Trim())) : Task.FromResult(false);
    public Task<bool> PairAsync() => CanPair ? RunAsync(new("pair", _state!.Revision, Origin.Trim(), DeviceName.Trim(),
        Text(_state.Server, "serverInstanceId"), Text(_state.Server, "deploymentEpoch"))) : Task.FromResult(false);
    public Task<bool> ClaimAsync() => CanClaim ? RunAsync(new("claim", _state!.Revision, Origin.Trim(), DeviceName.Trim(),
        Text(_state.Server, "serverInstanceId"), Text(_state.Server, "deploymentEpoch"), UserCode: NormalizedCode)) : Task.FromResult(false);
    public Task<bool> ConfirmAsync() => CanConfirm ? RunAsync(new("confirm", _state!.Revision, ApprovalId: Text(_state.Approval, "approvalId"))) : Task.FromResult(false);
    public Task<bool> PauseOrResumeAsync() => CanPause ? RunAsync(new(_state!.ReportingPaused ? "resume" : "pause", _state.Revision)) : Task.FromResult(false);
    public Task<bool> RecoverAsync() => CanRecover ? RunAsync(new(_state!.State switch
        { "CREATING" => "resume-create", "CONFIRMING" => "recover", "ACTIVE" => "resume", _ => "poll" }, _state.Revision)) : Task.FromResult(false);
    public Task<bool> UnpairAsync(long confirmedRevision)
    {
        if (!CanUnpair || confirmedRevision != _state!.Revision)
        {
            _feedback = "连接状态已变化，请重新核对后再取消申请或解绑。";
            Changed(); return Task.FromResult(false);
        }
        return RunAsync(new("unpair", confirmedRevision));
    }

    private async Task<bool> RunAsync(NpepCommand? command)
    {
        if (lifetime.IsCancellationRequested || !await _gate.WaitAsync(0)) return false;
        _working = true; _mutating = command is not null; Changed();
        try
        {
            var pending = new HostRequest(Protocol.Version, Guid.NewGuid(), command is null ? "npep.status" : "npep.command", Npep: command);
            if (authorize is not null && Protocol.NoiseInterruption(pending))
            {
                var authorized = await authorize(pending);
                if (authorized is null) { _feedback = "未验证，定时监测和学校连接继续。"; return false; }
                pending = authorized;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await request(pending, deadline.Token);
            Apply(response);
            return response.Outcome is "Succeeded" or "Accepted";
        }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or JsonException)
        {
            // A lost mutation response is ambiguous. Disable mutations until a fresh Host snapshot resolves it.
            _state = null; _serverConfirmed = _bindingConfirmed = false;
            _feedback = command is null ? "后台暂不可用，请重新检查。" : "未收到后台确认。操作可能已受理，请刷新状态后继续，勿重复创建申请。";
            Changed(); return false;
        }
        finally { _working = _mutating = false; _gate.Release(); Changed(); }
    }
}
