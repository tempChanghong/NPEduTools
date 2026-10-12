using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class CalibrationAccess
{
    public static Task<HostResponse> VerifyAsync(string pipe, string secret, CancellationToken token) =>
        HostClient.RequestAsync(pipe, new HostRequest(1, Guid.NewGuid(), "noise.management.command", NoiseManagement: new("verify", secret)), token);
    public static string Label(NoiseProtectionState? state) => state?.ManagementSource == "School"
        ? "学校管理口令（原本机口令已停用）" : "本机管理口令";

    public static string Hint(NoiseProtectionState? state) => state is null ? "尚未确认口令来源，请刷新后台状态。" :
        state.ErrorCode is not null ? "管理配置不可用：" + state.ErrorCode + "；请联系管理员，未绕过验证。" :
        state.ManagementSource == "School" ? state.Configured
            ? $"使用学校后台为本班设置的管理口令 · 已同步版本 {state.CredentialRevision}；不是学校 PIN。"
            : "学校管理口令尚未同步，请检查学校互联并等待管理员下发；不能使用旧本机口令。"
        : state.Configured ? "使用在这台电脑设置的管理口令；不是学校 PIN 或 Windows 密码。"
            : "本机尚未配置管理口令，请先点击“设置管理口令”。";

    public static string Message(string? code, string fallback, NoiseProtectionState? state) => code switch
    {
        "MANAGEMENT_SECRET_INVALID" => "管理验证未通过：" + (state?.ManagementSource == "School"
            ? "请输入学校后台为本班设置的管理口令，旧本机口令已停用。" : "本机管理口令不正确。"),
        "MANAGEMENT_NOT_CONFIGURED" => Hint(state),
        "MANAGEMENT_RATE_LIMITED" => "管理验证未通过：尝试过于频繁，请等待一分钟后重试；正确口令也不能跳过限流。",
        "MANAGEMENT_STORE_UNAVAILABLE" => "管理验证未通过：口令配置无法读取或保存，请联系管理员；未连接声级计。",
        "InvalidNoiseManagementCommand" => "请输入 8–128 字符的管理口令，不含控制字符；本次尚未验证。",
        _ => fallback
    };
}
