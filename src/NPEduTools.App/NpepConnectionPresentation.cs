using NPEduTools.Contracts;

namespace NPEduTools.App;

// Display only. OFFLINE alone does not prove that every kind of operation will retry.
public static class NpepConnectionPresentation
{
    private sealed record Reason(string Cause, string Advice);
    private static bool AutomaticRetry(NpepState state) => !state.ReportingPaused && state.Connection == "OFFLINE" &&
        (state.State is "UNPAIRED" or "CREATING" or "PENDING" or "APPROVED" or "CONFIRMING" or "ACTIVE") &&
        (state.Error == "TLS_VALIDATION_FAILED" || state.State is "ACTIVE" or "PENDING");

    public static string Title(NpepState? state)
    {
        if (state is null) return "后台状态未确认";
        if (state.State == "STORE_UNAVAILABLE") return "学校互联凭据暂不可用";
        if (state.State == "SUSPENDED") return "学校连接已停用";
        if (state.Busy) return "学校互联操作正在处理";
        if (state.Error == "PAIRING_EXPIRED") return "配对码已过期";
        if (state.State == "ACTIVE" && state.ReportingPaused) return "已配对 · 互联已暂停";
        if (AutomaticRetry(state)) return state.State == "ACTIVE" ? "已配对 · 暂时离线，等待自动重试" : "学校服务暂不可用 · 等待自动重试";
        return state.State switch
        {
            "UNPAIRED" => state.Connection == "VERIFIED" ? "服务已检查 · 尚未配对" : "尚未配对",
            "CREATING" => "配对申请待恢复",
            "PENDING" => state.Connection == "STOPPED" ? state.PairingSource == "SCREEN" ? "学校预授权连接已停止" : "配对申请连接已停止" :
                state.PairingSource == "SCREEN" ? "正在读取学校预授权" : "等待管理员批准",
            "APPROVED" => "请在本机确认连接", "CONFIRMING" => "配对确认结果待恢复",
            "ACTIVE" => state.Connection switch
            {
                "ONLINE" => "已配对 · 当前在线", "WAITING" or "CONNECTING" => "已配对 · 正在连接",
                "STOPPED" => "已配对 · 连接已停止", _ => "已配对 · 连接状态待核实"
            },
            "UNPAIRING" => "解绑结果待核实", _ => "学校互联状态待核实"
        };
    }

    public static string Cause(string? code) => code is null ? "" : Explain(code).Cause;

    public static string NextAction(NpepState? state, string? feedbackCode = null)
    {
        if (state is null) return "确认 NPEduTools 后台正在运行后点击“刷新”；未读取当前状态前不要重复配对或解绑。";
        if (state.State == "STORE_UNAVAILABLE") return Explain("CREDENTIAL_STORE_UNAVAILABLE").Advice;
        if (state.State == "SUSPENDED") return "联系学校管理员核实停用原因与归属；需要恢复时解除旧绑定，再按学校安排重新配对。不会自动抢回原连接。";
        if (state.Busy) return "等待后台更新；关闭此页面不会取消已受理的操作，不要重复提交。";
        if (feedbackCode is not null) return Explain(feedbackCode).Advice;
        if (state.State == "ACTIVE" && state.ReportingPaused)
            return "配对信息保留。需要继续连接时点击“恢复互联”，随后后台会重新核查学校授权；不会自行解除暂停。";
        if (AutomaticRetry(state)) return state.Error == "TLS_VALIDATION_FAILED"
            ? "后台会持续自动重试。核对学校地址、本机系统时间和服务器证书；每次仍验证证书，不要关闭证书校验。"
            : "等待后台自动重试，原配对或申请保留；若持续离线，核对网络与学校服务。无需重新配对。";
        if (state.Error is { } code) return Explain(code).Advice;
        return state.State switch
        {
            "UNPAIRED" => state.Connection == "VERIFIED" ? "核对学校服务后，输入网页配对码或创建配对申请。" : "填写学校提供的后端服务地址，再点击“检查学校服务”。",
            "CREATING" or "CONFIRMING" => "点击“恢复未完成操作”核查原申请或确认结果；不要另建配对申请。",
            "PENDING" => state.Connection == "STOPPED" ? "核对错误原因，再点击“恢复未完成操作”；不会自动创建新申请。" :
                state.PairingSource == "SCREEN" ? "等待后台读取学校预授权，再核对归属并确认连接。" : "请学校管理员在有效期内批准原申请；无需重复创建配对码。",
            "APPROVED" => "核对学校、班级和大屏归属，同意连接功能后点击“确认连接”。",
            "ACTIVE" => state.Connection switch
            {
                "ONLINE" => "后台继续接收通知与上报状态，收起主窗口不影响连接。上次回执时间不保证此后一直在线。",
                "CONNECTING" or "WAITING" => "等待后台核查授权并发送新状态；看到新的回执后才能确认当前在线。",
                "STOPPED" => "先核对原因，再点击“重新连接”；连接已停止，不会按离线状态自动重试。",
                _ => "点击“刷新”核实后台状态；未知状态不能认定在线或正在自动重试。"
            },
            "UNPAIRING" => "刷新并核实本机解绑结果，联系学校管理员检查远端撤销；不要直接创建另一份配对。",
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
        "TLS_VALIDATION_FAILED" => new("学校服务的 HTTPS 证书验证失败。", "核对学校地址、系统时间及服务器证书；按当前连接状态等待重试或恢复原操作，保留证书校验。"),
        "NETWORK_UNAVAILABLE" => new("暂时无法连接学校服务。", "核对网络与学校服务；未完成的手动操作需要刷新后核查或恢复原操作，不要重复创建申请。"),
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
        "HOST_UNAVAILABLE" => new("无法读取 NPEduTools 后台确认。", "恢复后台连接后先刷新核实结果；原操作可能已受理，不要重复提交。"),
        _ => new("出现尚未识别的互联问题。", "保留技术详情并联系学校管理员核对；未知错误不等于可以重新配对。")
    };
}
