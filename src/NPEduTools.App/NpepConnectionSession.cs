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
    private string? _catalogueSeen;
    private IReadOnlyList<ClassChoice> _classes = [];
    private enum CatalogueView { Quick, Editing, Lookup }
    private sealed record LookupPage(string School, string Class, string? After);
    private CatalogueView _catalogueView;
    private LookupPage? _lookupPage;
    private readonly List<string?> _lookupCursors = [];
    private string _schoolQuery = "", _classQuery = "";
    private string? _lookupFailure;
    private bool _lookupReady;
    private Guid? _lookupInHost;
    internal int ClassSelectionRevision { get; private set; }
    public string UserCode { get => _userCode; set { _userCode = value; Changed(); } }
    private (string? Origin, string Instance, string Epoch)? _providerSeen;
    private string? _serverSeen, _approvalSeen, _feedback;
    internal int ProviderRevision { get; private set; }
    private string? _feedbackCode;
    private Guid? _feedbackRequestId;
    private bool _working, _mutating, _originEdited, _serverConfirmed, _bindingConfirmed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed() => PropertyChanged?.Invoke(this, new(""));
    private static string Text(JsonObject? value, string key) => value?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";

    public string Origin
    {
        get => _origin;
        set
        {
            _originEdited = true;
            if (_origin == value) return;
            _origin = value;
            ResetProviderDrafts();
            Changed();
        }
    }
    private void ResetProviderDrafts()
    {
        ProviderRevision++;
        _serverConfirmed = false;
        _userCode = "";
        ResetClassSelection();
    }
    public string DeviceName { get => _deviceName; set { _deviceName = value; Changed(); } }
    public bool ServerConfirmed
    {
        get => _serverConfirmed;
        set
        {
            if (_serverConfirmed == value) return;
            _serverConfirmed = value;
            if (!value)
            {
                ResetClassSelection();
                UpdateCatalogue();
            }
            Changed();
        }
    }
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
    public bool SchoolVerified => HasCatalogueProvider && ServerConfirmed;
    public bool ShowServiceEntry => ShowAddressEditor && !SchoolVerified;
    public bool ShowInspectedService => HasCatalogueProvider && !ServerConfirmed;
    public bool HasSchoolDirectory => ProviderCatalogue is not null;
    public bool UseAlternatePairing => SchoolVerified && !ShowClassLookup && !HasClasses;
    public bool ShowApprovalReview => ShowBinding && !ShowBindingManagement;
    public bool ScreenOccupied => _state?.Error == "BINDING_OCCUPIED";
    public bool ShowConnectionAdvice => _state is null || _feedback is not null || _state.Error is not null ||
        _state.ReportingPaused || (!ShowCreate && _state.Connection != "ONLINE") || _state.State == "STORE_UNAVAILABLE";
    public bool ShowCode => _state?.State == "PENDING" && _state.PairingSource != "SCREEN";
    public bool ShowBinding => _state?.Approval is not null;
    public bool ShowConfirm => _state?.State == "APPROVED";
    public bool ShowActive => HasPairing;
    public bool ShowBindingManagement => NpepConnectionPresentation.IsBindingState(_state);
    // Read-only polling must not disable a focused input while the user is typing.
    public bool CanEdit => !_mutating && (_state is null || _state.State == "UNPAIRED" && (!_state.Busy ||
        _lookupInHost is { } lookup && _state.OperationId == lookup));
    public bool CanReviewBinding => !_mutating && _state is { Busy: false } && ShowConfirm;
    public bool CanInspect => Available && CanEdit && NpepContract.Valid(new("inspect", 0, Origin.Trim()));
    private bool HasCatalogueProvider => ShowServer && NpepContract.Valid(new("inspect", 0, Origin.Trim())) && Uri.TryCreate(Origin.Trim(), UriKind.Absolute, out var origin) &&
        origin.GetLeftPart(UriPartial.Authority) == _state!.Origin;
    public bool CanPair => Available && CanEdit && ServerConfirmed && HasCatalogueProvider && _state!.Server is { } server &&
        NpepContract.Valid(new("pair", _state.Revision, Origin.Trim(), DeviceName.Trim(), Text(server, "serverInstanceId"), Text(server, "deploymentEpoch")));
    public bool CanConfirm => Available && ShowConfirm && BindingConfirmed &&
        NpepContract.Valid(new("confirm", _state!.Revision, ApprovalId: Text(_state.Approval, "approvalId")));
    private string NormalizedCode => UserCode.Replace("-", "").Replace(" ", "").ToUpperInvariant();
    public bool CanClaim => CanPair && NpepContract.Valid(new("claim", _state!.Revision, Origin.Trim(), DeviceName.Trim(),
        Text(_state.Server, "serverInstanceId"), Text(_state.Server, "deploymentEpoch"), UserCode: NormalizedCode));
    public sealed record ClassChoice(string Id, string Name)
    {
        public bool? Preauthorized { get; init; }
    }
    public IReadOnlyList<ClassChoice> Classes => _classes;
    public bool HasClasses => Classes.Count > 0;
    private JsonObject? ProviderCatalogue => HasCatalogueProvider ? _state?.Catalogue : null;
    public bool ShowClassLookup => Flag(ProviderCatalogue, "supportsLookup");
    public bool CanEditClassCatalogue => HasCatalogueProvider && CanEdit && ServerConfirmed;
    public bool ShowQuickCatalogueAction => HasCatalogueProvider && (_catalogueView != CatalogueView.Quick || HasLookupScope(ProviderCatalogue));
    public bool CanRestoreQuickCatalogue => CanInspect && CanEditClassCatalogue;
    public bool CanFindClass => Available && CanEditClassCatalogue && ShowClassLookup &&
        NpepContract.Valid(LookupCommand(new(SchoolQuery.Trim(), ClassQuery.Trim(), null)));
    public bool ShowPreviousClassPage => _lookupCursors.Count > 1;
    public bool CanFindPreviousClassPage => Available && CanEditClassCatalogue && ShowClassLookup && ShowPreviousClassPage;
    public bool ShowNextClassPage => NextClassCursor is not null;
    public bool CanFindNextClassPage => Available && CanEditClassCatalogue && ShowClassLookup && NextClassCursor is not null;
    public bool ShowRetryClassPage => _lookupFailure is not null && _lookupPage is not null;
    public bool CanRetryClassPage => Available && CanEditClassCatalogue && ShowClassLookup && ShowRetryClassPage;
    public string SchoolQuery { get => _schoolQuery; set => EditLookup(ref _schoolQuery, value); }
    public string ClassQuery { get => _classQuery; set => EditLookup(ref _classQuery, value); }
    private string? NextClassCursor => _lookupReady && _lookupPage == ReadLookupPage(ProviderCatalogue) &&
        Text(ProviderCatalogue, "nextCursor") is { Length: > 0 } next && !_lookupCursors.Contains(next) ? next : null;
    public string ClassCatalogueHint
    {
        get
        {
            if (!HasCatalogueProvider) return "";
            if (_catalogueView == CatalogueView.Editing) return "填写学校与班级的完整名称或代码，再查找并选择大屏。";
            if (_catalogueView == CatalogueView.Lookup)
            {
                if (_lookupFailure is { } failure) return failure;
                if ((_working || _lookupInHost is not null) && !_lookupReady) return "正在查找班级大屏…";
                if (!_lookupReady) return "请查找班级大屏。";
                return HasClasses ? $"本页找到 {Classes.Count} 台大屏，请核对后手动选择。" : "本页没有符合条件的大屏，请核对学校和班级的全称或代码。";
            }
            if (HasLookupScope(ProviderCatalogue)) return "可重新查找，或返回快速列表。";
            if (ProviderCatalogue is not null && !HasClasses && !ShowClassLookup)
                return "学校服务暂未列出可选大屏。可展开其他连接方式，使用网页配对码或发起申请。";
            if (!Flag(ProviderCatalogue, "truncated")) return "";
            return ShowClassLookup ? "快速列表只显示部分大屏。请用下方精确查找定位其他班级。"
                : "快速列表只显示部分大屏。若找不到本班，请联系学校管理员升级学校服务。";
        }
    }

    internal void LeaveClassSelection()
    {
        ResetClassSelection();
        Changed();
    }

    private void ResetClassSelection()
    {
        _catalogueView = CatalogueView.Quick;
        _schoolQuery = _classQuery = "";
        ResetLookupPages();
    }

    private void EditLookup(ref string field, string value)
    {
        if (field == value) return;
        field = value;
        _catalogueView = CatalogueView.Editing;
        ResetLookupPages();
        Changed();
    }

    private void ResetLookupPages()
    {
        _lookupCursors.Clear();
        _lookupPage = null;
        _lookupReady = false;
        _lookupFailure = null;
        ClearClassCatalogue();
    }

    private void ClearClassCatalogue()
    {
        ClassSelectionRevision++;
        _catalogueSeen = null;
        _classes = [];
    }

    public Task<bool> FindClassAsync() => CanFindClass ? ReadClassPageAsync(new(SchoolQuery.Trim(), ClassQuery.Trim(), null)) : Task.FromResult(false);

    public Task<bool> FindPreviousClassPageAsync() => CanFindPreviousClassPage && _lookupPage is { } page
        ? ReadClassPageAsync(page with { After = _lookupCursors[^2] }) : Task.FromResult(false);

    public Task<bool> FindNextClassPageAsync() => CanFindNextClassPage && _lookupPage is { } page && NextClassCursor is { } cursor
        ? ReadClassPageAsync(page with { After = cursor }) : Task.FromResult(false);

    public Task<bool> RetryClassPageAsync() => CanRetryClassPage && _lookupPage is { } page ? ReadClassPageAsync(page) : Task.FromResult(false);

    private Task<bool> ReadClassPageAsync(LookupPage page)
    {
        var command = LookupCommand(page);
        if (!NpepContract.Valid(command)) return Task.FromResult(false);
        int savedPage = _lookupCursors.IndexOf(page.After);
        if (savedPage < 0) _lookupCursors.Add(page.After);
        else _lookupCursors.RemoveRange(savedPage + 1, _lookupCursors.Count - savedPage - 1);
        _catalogueView = CatalogueView.Lookup;
        _lookupPage = page;
        _lookupReady = false;
        _lookupFailure = null;
        ClearClassCatalogue();
        return RunAsync(command, ClassSelectionRevision);
    }

    private NpepCommand LookupCommand(LookupPage page) => new("find-class", _state!.Revision, Origin.Trim(),
        ServerInstanceId: Text(_state.Server, "serverInstanceId"), DeploymentEpoch: Text(_state.Server, "deploymentEpoch"),
        SchoolQuery: page.School, ClassQuery: page.Class, CatalogueAfter: page.After);

    private static bool Flag(JsonObject? value, string key) => value?[key] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && enabled;

    private static bool HasLookupScope(JsonObject? value) => value?["schoolQuery"] is not null || value?["classQuery"] is not null || value?["after"] is not null;

    private static LookupPage? ReadLookupPage(JsonObject? value) =>
        Text(value, "schoolQuery") is { Length: > 0 } school && Text(value, "classQuery") is { Length: > 0 } @class
            ? new(school, @class, Text(value, "after") is { Length: > 0 } after ? after : null) : null;

    private void UpdateCatalogue()
    {
        var catalogue = ProviderCatalogue;
        var page = ReadLookupPage(catalogue);
        if (_catalogueView == CatalogueView.Lookup && _lookupReady && _lookupPage != page)
        {
            _lookupReady = false;
            _lookupFailure = "后台目录已变化，请重新查找。";
        }
        bool matches = _catalogueView == CatalogueView.Quick && !HasLookupScope(catalogue) ||
            _catalogueView == CatalogueView.Lookup && _lookupReady && _lookupPage == page;
        string? serialized = matches ? catalogue?.ToJsonString() : null;
        if (serialized == _catalogueSeen) return;
        ClassSelectionRevision++;
        _catalogueSeen = serialized;
        if (!matches || catalogue?["items"] is not JsonArray items) { _classes = []; return; }

        var choices = items.OfType<JsonObject>().Where(row => Text(row, "screenBindingId").Length > 0)
            .Select(row => new ClassChoice(Text(row, "screenBindingId"), CatalogueLabel(row))
            {
                Preauthorized = row["preauthorized"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) ? enabled : null
            }).DistinctBy(row => row.Id).ToArray();
        var duplicateLabels = choices.GroupBy(row => row.Name).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        _classes = choices.Select(row => duplicateLabels.Contains(row.Name) ? row with { Name = $"{row.Name}（大屏编号 {row.Id}）" } : row).ToArray();
    }

    private static string CatalogueLabel(JsonObject row)
    {
        string WithCode(string key, string codeKey) => Text(row, codeKey) is { Length: > 0 } code ? $"{Text(row, key)}（{code}）" : Text(row, key);
        var grade = Text(row, "gradeName");
        var classLabel = WithCode("className", "classCode");
        if (grade.Length > 0 && !classLabel.StartsWith(grade, StringComparison.Ordinal)) classLabel = $"{grade} / {classLabel}";
        return string.Join(" · ", new[] { WithCode("schoolName", "schoolCode"), classLabel, Text(row, "screenName") }.Where(value => value.Length > 0));
    }
    public Task<bool> ConnectClassAsync(string? binding,string? secret)
    {
        if(!CanPair || binding is null || !Classes.Any(x=>x.Id==binding))return Task.FromResult(false);
        var c=new NpepCommand(secret is null?"apply-class":"password-class",_state!.Revision,Origin.Trim(),DeviceName.Trim(),
            Text(_state.Server,"serverInstanceId"),Text(_state.Server,"deploymentEpoch"),ScreenBindingId:binding,ManagementSecret:secret);
        return NpepContract.Valid(c)?RunAsync(c):Task.FromResult(false);
    }
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
        "UNPAIRED" => "接入教室 · 核对学校服务", "CREATING" or "PENDING" => _state.PairingSource == "SCREEN" ? "接入教室 · 读取学校预授权" : "接入教室 · 配对申请",
        "APPROVED" or "CONFIRMING" => "接入教室 · 确认归属", "ACTIVE" => "已完成配对",
        _ => "连接学校 · 等待检查"
    };
    public string Title => NpepConnectionPresentation.Title(_state);
    public string Message => _feedback ?? (ShowBindingManagement || ScreenOccupied ? "" :
        _state?.Message ?? "无法读取后台状态。恢复后重新检查；旧状态不代表当前连接。");
    public string Error => NpepConnectionPresentation.Cause(ShowBindingManagement ? _state?.Error : _feedbackCode ?? _state?.Error);
    public string NextAction => NpepConnectionPresentation.NextAction(_state, _feedbackCode);
    public string TechnicalDetails => NpepConnectionPresentation.Details(_state, _feedbackCode, _feedbackRequestId, _feedback);
    public string Receipt => NpepConnectionPresentation.Receipt(_state);
    public string BoundSchool => BindingField("schoolName");
    public string BoundClass => BindingField("administrativeClassName");
    public string BoundScreen => BindingField("screenBindingName");
    private string BindingField(string field) => Text(_state?.Approval, field) is { Length: > 0 } value ? value : "暂无记录";
    public string ServerIdentity => _state?.Server is null ? "" : $"地址：{_state.Origin}\n实例：{Text(_state.Server, "serverInstanceId")}\n部署标识：{Text(_state.Server, "deploymentEpoch")}";
    public string PairingCode => Text(_state?.Pairing, "userCode");
    public string Expiry => "配对截止（学校服务器 UTC）：" + Text(_state?.Pairing, "expiresAt");
    public string Binding => $"学校：{Text(_state?.Approval, "schoolName")}\n班级：{Text(_state?.Approval, "administrativeClassName")}\n大屏：{Text(_state?.Approval, "screenBindingName")}";
    public string BoundOrigin => _state?.Origin ?? "";
    public string BindingIdentity => $"学校：{Text(_state?.Approval, "schoolId")}\n班级：{Text(_state?.Approval, "administrativeClassId")}\n大屏：{Text(_state?.Approval, "screenBindingId")}\n批准编号：{Text(_state?.Approval, "approvalId")}";

    private void Apply(HostResponse response, NpepCommand? command)
    {
        _state = response.Npep;
        if (_state?.State is "APPROVED" or "ACTIVE") _userCode = "";
        (string?, string, string)? provider = _state?.Server is { } identity
            ? (_state.Origin, Text(identity, "serverInstanceId"), Text(identity, "deploymentEpoch")) : null;
        string? server = _state?.Server?.ToJsonString();
        string? approval = _state?.Approval?.ToJsonString();
        if (provider != _providerSeen)
        {
            if (_providerSeen is not null) ResetProviderDrafts();
            _providerSeen = provider;
        }
        if (server != _serverSeen)
        {
            _serverSeen = server;
            if (_serverConfirmed) ResetProviderDrafts();
        }
        if (approval != _approvalSeen) { _approvalSeen = approval; _bindingConfirmed = false; }
        if (!_originEdited && _state?.Origin is { } saved) _origin = saved;
        _feedback = response.Outcome == "Rejected" || response.Npep is null ? response.Message : null;
        _feedbackCode = _feedback is null ? null : response.ErrorCode;
        _feedbackRequestId = _feedbackCode is null ? null : response.RequestId;
        ObserveLookup(response, command);
        UpdateCatalogue();
        Changed();
    }

    private void ObserveLookup(HostResponse response, NpepCommand? command)
    {
        if (command?.Action == "find-class" && _catalogueView == CatalogueView.Lookup)
        {
            if (response.Outcome == "Accepted" && _state is { Busy: true } && _state.OperationId == response.RequestId &&
                _lookupPage == ReadLookupPage(ProviderCatalogue))
            {
                _lookupInHost = response.RequestId;
                return;
            }
            CompleteLookup(response.Outcome is "Succeeded" or "Accepted", _feedback);
            return;
        }

        if (_lookupInHost is not { } operation || _state is { Busy: true } && _state.OperationId == operation) return;
        _lookupInHost = null;
        if (_catalogueView == CatalogueView.Lookup)
            CompleteLookup(_state?.OperationId == operation, _state?.OperationId == operation && _state.Error is not null ? _state.Message : null);
    }

    private void CompleteLookup(bool matchingOperation, string? failure)
    {
        _lookupReady = matchingOperation && _state is { Busy: false, Error: null } && _lookupPage == ReadLookupPage(ProviderCatalogue) &&
            ProviderCatalogue?["items"] is JsonArray;
        _lookupFailure = _lookupReady ? null : failure ?? "暂未取得本次查找结果，请重试。";
    }

    public Task<bool> RefreshAsync() => RunAsync(null);
    public async Task<JsonObject?> CollectDiagnosticsAsync(string appVersion, CancellationToken token = default)
    {
        if (lifetime.IsCancellationRequested || token.IsCancellationRequested || !await _gate.WaitAsync(0)) return null;
        _working = true; Changed();
        try
        {
            async Task<(HostResponse? Response, DateTimeOffset? At)> ReadAsync(string capability)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    var response = await request(new(Protocol.Version, Guid.NewGuid(), capability), deadline.Token);
                    return (response, DateTimeOffset.UtcNow);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
                { return (null, null); }
            }
            var connection = await ReadAsync("npep.status");
            if (lifetime.IsCancellationRequested || token.IsCancellationRequested) return null;
            var history = connection.Response?.Outcome == "Succeeded" ? await ReadAsync("npep.diagnostics") : (Response: (HostResponse?)null, At: (DateTimeOffset?)null);
            if (lifetime.IsCancellationRequested || token.IsCancellationRequested) return null;
            var cache = await ReadAsync("host.cached-status");
            if (lifetime.IsCancellationRequested || token.IsCancellationRequested) return null;
            return NpepDiagnostics.Create(appVersion, DateTimeOffset.UtcNow, connection.Response, connection.At, cache.Response, cache.At, history.Response);
        }
        finally { _working = false; _gate.Release(); Changed(); }
    }
    public Task<bool> InspectAsync()
    {
        if (!CanInspect) return Task.FromResult(false);
        ResetClassSelection();
        return RunAsync(new("inspect", _state!.Revision, Origin.Trim()), ClassSelectionRevision);
    }
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
            _feedbackCode = "NpepStateChanged"; _feedbackRequestId = null;
            Changed(); return Task.FromResult(false);
        }
        return RunAsync(new("unpair", confirmedRevision));
    }

    private async Task<bool> RunAsync(NpepCommand? command, int? selectionRevision = null)
    {
        if (lifetime.IsCancellationRequested || !await _gate.WaitAsync(0)) return false;
        _working = true; _mutating = command is not null && command.Action != "find-class"; Changed();
        try
        {
            var pending = new HostRequest(Protocol.Version, Guid.NewGuid(), command is null ? "npep.status" : "npep.command", Npep: command);
            if (authorize is not null && Protocol.NoiseInterruption(pending))
            {
                var authorized = await authorize(pending);
                if (authorized is null)
                { _feedback = "未验证，定时监测和学校连接继续。"; _feedbackCode = null; _feedbackRequestId = null; return false; }
                pending = authorized;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await request(pending, deadline.Token);
            if (lifetime.IsCancellationRequested || selectionRevision is int revision && revision != ClassSelectionRevision) return false;
            Apply(response, command);
            return response.Outcome is "Succeeded" or "Accepted" && (command?.Action != "find-class" || _lookupReady || _lookupInHost is not null);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        {
            if (lifetime.IsCancellationRequested || selectionRevision is int revision && revision != ClassSelectionRevision) return false;
            // A lost mutation response is ambiguous. Disable mutations until a fresh Host snapshot resolves it.
            _state = null; _serverConfirmed = _bindingConfirmed = false;
            ClearClassCatalogue();
            _lookupReady = false;
            _lookupInHost = null;
            _feedback = command?.Action is null or "inspect" or "find-class" ? "后台暂不可用，请重新检查。"
                : "未收到后台确认。操作可能已受理，请刷新状态后继续，勿重复创建申请。";
            if (command?.Action == "find-class") _lookupFailure = _feedback;
            _feedbackCode = "HOST_UNAVAILABLE"; _feedbackRequestId = null;
            Changed(); return false;
        }
        finally { _working = _mutating = false; _gate.Release(); Changed(); }
    }
}
