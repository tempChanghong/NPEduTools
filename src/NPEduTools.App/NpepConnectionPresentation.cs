using System.Globalization;
using NPEduTools.Contracts;

namespace NPEduTools.App;

// Display only. OFFLINE alone does not prove that every kind of operation will retry.
public static class NpepConnectionPresentation
{
    private sealed record Reason(string Cause, string Advice);
    private static readonly string[] ReceiptTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss.fff'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"];

    internal static bool IsBindingState(NpepState? state) => state?.State is "ACTIVE" or "SUSPENDED" or "UNPAIRING";
    internal static bool CanOfferReplacement(NpepState? state) =>
        state is { State: "CREATING", Connection: "STOPPED" } &&
        ((state.PairingSource is "CLASS_APPLY" or "CLASS_PASSWORD" && state.Error == "BINDING_OCCUPIED") ||
         (state.PairingSource == "REPLACEMENT" && state.Error == "MANAGEMENT_SECRET_INVALID")) &&
        NpepContract.CatalogueId(state.RequestedScreenBindingId) &&
        NpepContract.Valid(new("pair", state.Revision, state.Origin, state.RequestedDeviceName,
            state.Server?["serverInstanceId"]?.ToString(), state.Server?["deploymentEpoch"]?.ToString()));

    private static bool TryReadReceipt(NpepState? state, out DateTimeOffset receivedAt) =>
        DateTimeOffset.TryParseExact(state?.LastReceivedAt, ReceiptTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out receivedAt);

    internal static string Receipt(NpepState? state) => TryReadReceipt(state, out var receivedAt)
        ? "最近有效回执：" + receivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "（本机时间）"
        : IsBindingState(state) ? "最近有效回执：暂无" : "";

    private static bool AutomaticRetry(NpepState state) => !state.ReportingPaused && state.Connection == "OFFLINE" &&
        (state.State is "UNPAIRED" or "CREATING" or "PENDING" or "APPROVED" or "CONFIRMING" or "ACTIVE") &&
        (state.Error == "TLS_VALIDATION_FAILED" || state.State is "ACTIVE" or "PENDING");
    private static bool Resampling(NpepState state) =>
        state is { State: "ACTIVE", Connection: "ONLINE" or "OFFLINE", Error: "SAMPLE_TOO_OLD", ReportingPaused: false };
    private static bool ReceiptExpired(NpepState state) =>
        state is { State: "ACTIVE", Connection: "OFFLINE", Error: null } && TryReadReceipt(state, out _);

    public static string Title(NpepState? state)
    {
        if (state is null) return "后台状态未确认";
        if (state.State == "STORE_UNAVAILABLE") return "学校互联凭据暂不可用";
        if (state.State == "SUSPENDED") return "学校连接已停用";
        if (state.State == "UNPAIRING") return state.Busy ? "正在解除绑定" : "解绑结果待确认";
        if (state.Busy) return "学校互联操作正在处理";
        if (state.PairingSource == "REPLACEMENT" && !IsBindingState(state))
        {
            if (state.Error == "PAIRING_CANCELLED") return "换机申请已取消";
            if (state.Error == "PAIRING_EXPIRED" && state.State != "CONFIRMING") return "换机申请已过期";
            if (state.Error == "REPLACEMENT_UNSUPPORTED") return "学校服务尚未支持换机";
            if (state.State == "CREATING" && state.Error == "MANAGEMENT_SECRET_INVALID") return "换机申请未通过口令验证";
            if (state.State == "PENDING" && state.Connection == "WAITING") return "换机申请已提交 · 等待学校审核";
            if (state.State == "APPROVED") return "学校已批准 · 请在本机确认交接";
            if (state.State == "CONFIRMING") return "换机交接结果待核实";
        }
        if (state.Error == "BINDING_OCCUPIED") return "所选大屏已有设备接入";
        if (state.Error == "PAIRING_EXPIRED") return "配对码已过期";
        if (state.State == "ACTIVE" && state.ReportingPaused) return "已配对 · 互联已暂停";
        if (Resampling(state)) return "已配对 · 状态待重新上报";
        if (ReceiptExpired(state)) return "等待学校新回执";
        if (AutomaticRetry(state)) return state.State == "ACTIVE" ? "暂时离线 · 自动重试" : "学校服务暂不可用 · 等待自动重试";
        return state.State switch
        {
            "UNPAIRED" => state.Connection == "VERIFIED" ? "服务已检查 · 尚未配对" : "尚未配对",
            "CREATING" => "配对申请待恢复",
            "PENDING" => state.Connection == "STOPPED" ? state.PairingSource == "SCREEN" ? "学校预授权连接已停止" : "配对申请连接已停止" :
                state.PairingSource == "SCREEN" ? "正在读取学校预授权" : "等待管理员批准",
            "APPROVED" => "请在本机确认连接", "CONFIRMING" => "配对确认结果待恢复",
            "ACTIVE" => state.Connection switch
            {
                "ONLINE" => TryReadReceipt(state, out _) ? "已配对 · 当前在线" : "已配对 · 等待学校回执",
                "WAITING" or "CONNECTING" => "已配对 · 正在连接",
                "STOPPED" => "已配对 · 连接已停止", _ => "已配对 · 连接状态待核实"
            },
            _ => "学校互联状态待核实"
        };
    }

    public static string Cause(string? code, NpepState? state = null) =>
        code is null ? "" : (ReplacementReason(state, code) ?? Explain(code)).Cause;

    private static Reason? ReplacementReason(NpepState? state, string code) => state?.PairingSource != "REPLACEMENT" ||
        IsBindingState(state) ? null : code switch
        {
            "PAIRING_CANCELLED" => new("本次申请已由学校关闭。", "可取消本机保留的申请，核对大屏后重新申请。"),
            "PAIRING_EXPIRED" => new("换机申请的有效期已结束。", "取消本机保留的申请，核对大屏后重新申请。"),
            "MANAGEMENT_SECRET_INVALID" => new("班级管理口令无效，或学校尚未开放口令接入。", "核对班级与大屏后重新输入原班级管理口令。"),
            "REPLACEMENT_UNSUPPORTED" => new("当前学校服务未提供换机接口。", "请学校管理员升级 KV。升级后可在这里恢复原申请。"),
            "APPROVER_NO_LONGER_AUTHORIZED" => new("本次批准所用的学校管理会话已失效。", "请联系学校管理员重新审批新的换机申请。"),
            "BINDING_CHANGED" or "PREAUTHORIZATION_CHANGED" => new("目标大屏、原设备或班级预授权已变化。", "请与学校管理员重新核对此屏的设备和接入权限，然后取消原申请，按当前归属重新办理。"),
            _ => null
        };

    public static string NextAction(NpepState? state, string? feedbackCode = null)
    {
        if (state is null) return "确认 NPEduTools 后台正在运行后点击“刷新”；未读取当前状态前不要重复配对或解绑。";
        if (state.State == "STORE_UNAVAILABLE") return Explain("CREDENTIAL_STORE_UNAVAILABLE").Advice;
        if (state.State == "SUSPENDED") return "请联系学校管理员核实授权与归属。停用后不会自动恢复。";
        if (state.State == "UNPAIRING") return state.Busy
            ? "请等待后台确认解绑结果。"
            : "请刷新核实解绑结果，必要时联系管理员检查远端撤销。互联不会自动恢复。";
        if (state.Busy) return "等待后台更新；关闭此页面不会取消已受理的操作，不要重复提交。";
        if (feedbackCode is not null && !IsBindingState(state)) return (ReplacementReason(state, feedbackCode) ?? Explain(feedbackCode)).Advice;
        if (state.State == "ACTIVE" && state.ReportingPaused)
            return "需要继续时，展开“绑定管理”并点击“恢复互联”。后台不会自行解除暂停。";
        if (Resampling(state)) return "无需操作，后台会重新采样并自动重试。";
        if (ReceiptExpired(state)) return "距上次收到回执已满 60 秒，后台会继续上报，无需操作。";
        if (AutomaticRetry(state)) return state.Error == "TLS_VALIDATION_FAILED"
            ? "请核对学校地址、本机系统时间和服务器证书。后台会持续自动重试，每次仍验证证书。"
            : state.State == "ACTIVE"
                ? "无需立即操作，后台会自动重试。若持续离线，请核对网络与学校服务。无需重新配对。"
                : "等待后台自动重试，原配对或申请保留；若持续离线，核对网络与学校服务。无需重新配对。";
        if (state.Error == "BINDING_OCCUPIED" && CanOfferReplacement(state))
            return "若这台电脑要接替旧设备，请核对下方大屏并提交换机申请。学校管理员批准后，还需在本机确认交接。";
        if (state is { PairingSource: "REPLACEMENT", State: "CONFIRMING", Error: null or "NETWORK_UNAVAILABLE" or "PAIRING_EXPIRED" })
            return "点击“核实交接结果”，后台将查询本机的登记并恢复原确认。";
        if (state.Error is { } code) return (ReplacementReason(state, code) ?? Explain(code)).Advice +
            (state is { State: "ACTIVE", Connection: "STOPPED" } ? " 后台不会自动重试。" : "");
        if (state.PairingSource == "REPLACEMENT")
        {
            if (state.State == "PENDING" && state.Connection != "STOPPED")
                return "学校管理员可在 NPEduConsole 审核这台新机。旧机继续工作，批准后请回到这里核对归属并确认。";
            if (state.State == "APPROVED")
                return "核对下方学校、班级和大屏。现场确认成功时，本机接管该屏，旧机的学校连接同时停用。";
        }
        return state.State switch
        {
            "UNPAIRED" => state.Connection == "VERIFIED" ? "核对学校服务，选择本班大屏后继续连接。" : "填写学校提供的服务地址，再点击“检查服务”。",
            "CREATING" or "CONFIRMING" => "点击“恢复未完成操作”核查原申请或确认结果；不要另建配对申请。",
            "PENDING" => state.Connection == "STOPPED" ? "核对错误原因，再点击“恢复未完成操作”；不会自动创建新申请。" :
                state.PairingSource == "SCREEN" ? "等待后台读取学校预授权，再核对归属并确认连接。" : "请学校管理员在有效期内批准原申请；无需重复创建配对码。",
            "APPROVED" => "核对学校、班级和大屏归属，同意连接功能后点击“确认连接”。",
            "ACTIVE" => state.Connection switch
            {
                "ONLINE" when TryReadReceipt(state, out _) => "无需操作，后台会继续接收通知并上报状态。",
                "ONLINE" or "CONNECTING" or "WAITING" => "无需操作，收到新的回执后会自动更新。",
                "STOPPED" => "先核对原因，再点击“重新连接”；连接已停止，不会按离线状态自动重试。",
                _ => "点击“刷新”核实后台状态；未知状态不能认定在线或正在自动重试。"
            },
            _ => "刷新并保留技术详情，核对 App 与 Host 版本；不能把未知状态当作在线。"
        };
    }

    public static string Details(NpepState? state, string? feedbackCode, Guid? feedbackRequestId, string? feedback)
    {
        string current = state is null ? "后台当前状态：未读取" :
            $"配对状态：{state.State}\n连接状态：{state.Connection}\n主动暂停：{state.ReportingPaused}\n状态版本：{state.Revision}\n后台最近操作编号：{state.OperationId?.ToString() ?? "无"}\n状态错误码：{state.Error ?? "无"}\n后台说明：{state.Message}";
        return current + (feedbackCode is null ? "" : $"\n操作错误码：{feedbackCode}\n操作请求编号：{feedbackRequestId?.ToString() ?? "无"}\n操作说明：{feedback}");
    }

    private static Reason Explain(string code) => code switch
    {
        "BINDING_OCCUPIED" => new("这块大屏已有一台有效终端。", "请学校管理员核对旧设备。可取消本机申请并重新选择正确的大屏。"),
        "TLS_VALIDATION_FAILED" => new("学校服务的 HTTPS 证书验证失败。", "核对学校地址、系统时间及服务器证书；按当前连接状态等待重试或恢复原操作，保留证书校验。"),
        "NETWORK_UNAVAILABLE" => new("暂时无法连接学校服务。", "核对网络与学校服务；未完成的手动操作需要刷新后核查或恢复原操作，不要重复创建申请。"),
        "TEMPORARILY_UNAVAILABLE" => new("学校服务暂时不可用。", "按当前连接状态等待自动重试或恢复原操作；核对学校服务与网关，不要重复创建配对。"),
        "RATE_LIMITED" => new("学校服务暂时限制了请求频率。", "等待后台按服务给出的间隔重试；未完成的手动操作先核查原结果，不要反复提交。"),
        "SAMPLE_TOO_OLD" => new("本次状态样本已过期，等待新的有效回执。", "后台将重新采样并自动重试；若持续发生，请保留技术详情，核对电脑负载和连接耗时。"),
        "PAIRING_EXPIRED" => new("配对码已过期。", "取消原申请后重新获取配对码；原申请不会自动延长有效期。"),
        "PAIRING_CODE_UNAVAILABLE" => new("网页配对码已使用、过期或失效。", "取消原申请，再从对应班级的大屏网页获取新码。"),
        "SCREEN_PAIRING_DISABLED" or "PREAUTHORIZATION_CHANGED" => new("学校预授权已关闭或变更。", "联系管理员核对班级预授权；处理后取消原申请并重新获取配对码。"),
        "AUTH_INVALID" or "CREDENTIAL_EXPIRED" or "DEVICE_SUSPENDED" => new("原学校授权已失效或设备已停用。", "联系学校管理员核实；确认需要重新连接后解除旧绑定，再重新配对。"),
        "INSTANCE_MISMATCH" or "BINDING_CHANGED" => new("学校服务实例或设备归属发生变化。", "先与学校核对服务及归属，再解除旧绑定并重新配对。"),
        "SESSION_SUPERSEDED" or "REVISION_CONFLICT" => new("当前会话已被另一会话替换。", "检查是否有另一实例使用原配对；不要反复重连争抢会话。"),
        "INVALID_SERVER_ORIGIN" => new("学校服务地址格式不正确。", "输入完整 HTTP 或 HTTPS 后端地址，按实际部署填写端口；不附加接口路径、查询参数或账号密码。"),
        "INVALID_RESPONSE" or "REDIRECT_REJECTED" => new("学校服务没有返回受支持的互联响应。", "向管理员核对后端地址、端口及反向代理；网页首页地址不一定是 API 服务地址。"),
        "CREDENTIAL_STORE_UNAVAILABLE" or "NpepUnavailable" => new("互联凭据无法读取或后台暂不可用。", "正常保存当前工作，核对冲突实例与本机存储，再重启后台；保留原凭据文件，不要删除它来重新配对。"),
        "NpepStateChanged" => new("连接状态已变化或仍有操作在进行。", "刷新并等待原操作结束，再核对当前状态；不要重复创建申请。"),
        "LOCAL_CONFIRMATION_REQUIRED" => new("学校服务尚未完成本机核对。", "重新检查学校服务，核对地址与实例后再确认并使用配对码。"),
        "LOCAL_ONLY" => new("本机已解绑，但尚未确认学校端撤销。", "请学校管理员检查并撤销原设备登记，再按需要重新配对。"),
        "REPLACEMENT_CANCEL_UNCONFIRMED" => new("本机已清除换机申请，学校端尚未确认取消。", "可在 Console 查看这台大屏的换机审核记录，核对原申请是否仍待处理。"),
        "HOST_UNAVAILABLE" => new("无法读取 NPEduTools 后台确认。", "恢复后台连接后先刷新核实结果；原操作可能已受理，不要重复提交。"),
        _ => new("出现尚未识别的互联问题。", "保留技术详情并联系学校管理员核对；未知错误不等于可以重新配对。")
    };
}
