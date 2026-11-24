using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class ClassroomActionAccess
{
    internal const string StatusCapability = "noise.management.status.classroom-policy";

    internal static async Task<HostResponse> ReadAsync(string pipe, CancellationToken token)
    {
        var response = await HostClient.RequestAsync(pipe, StatusCapability, token);
        if (response is { Outcome: "Rejected", ErrorCode: "UnknownCapability" })
            return await HostClient.RequestAsync(pipe, "noise.management.status", token);
        return response;
    }

    internal static string Mode(NoiseProtectionState state, string purpose)
    {
        if (state.ClassroomPolicy is not { } policy) return "default";
        if (purpose != "maintenance" && (purpose != "stop" || !state.Protected)) return "default";
        if (string.IsNullOrWhiteSpace(policy.Version)) return "unavailable";
        return purpose == "maintenance" ? policy.ExitHost : policy.StopScheduled;
    }

    internal static string Describe(string mode) => mode switch
    {
        "allow" => "学校允许现场办理，无需验证。",
        "deny" => "学校已禁止现场办理此操作。",
        "password" => "学校要求使用本班管理口令。",
        "usb" => "学校要求将本班获准的教师 U 盘插入这台大屏。",
        "either" => "本班管理口令和获准的教师 U 盘，任选一种验证。",
        "both" => "学校要求同时验证本班管理口令和插在这台大屏上的教师 U 盘。",
        "default" => "定时监测期间使用当前管理口令。",
        _ => "无法确认这项操作的学校策略，请刷新或联系管理员。"
    };

    internal static string? Blocked(NoiseProtectionState state, string mode) => mode switch
    {
        "default" when state.Protected && !state.Configured => state.ManagementSource == "School"
            ? "本班管理口令尚未同步或不可用。请在 NPEduConsole 核对本班口令，并检查学校互联。"
            : "本机管理口令尚未配置。请在无定时监测时到“自习 → 联机校准”设置。",
        "password" or "both" when !state.Configured => "本班管理口令尚未同步或不可用，请联系学校管理员。",
        "default" or "allow" or "password" or "usb" or "either" or "both" => null,
        _ => Describe(mode)
    };

    internal static NoiseManagementCommand AuthorizeCommand(NoiseProtectionState state, HostRequest target, string mode, string? secret)
        => new("authorize", secret, Purpose: Protocol.NoisePurpose(target), TargetRequestId: target.RequestId,
            InstanceId: state.InstanceId, SessionId: state.SessionId,
            ClassroomPolicyVersion: mode == "default" ? null : state.ClassroomPolicy?.Version);

    internal static Guid? Ticket(HostResponse reply, NoiseProtectionState state)
        => reply is { Outcome: "Succeeded", NoiseProtection: { Ticket: { } ticket } proof } && ticket != Guid.Empty &&
            proof.InstanceId == state.InstanceId && proof.SessionId == state.SessionId ? ticket : null;
}
