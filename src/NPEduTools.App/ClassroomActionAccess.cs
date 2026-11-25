using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class ClassroomActionAccess
{
    internal const string StatusCapability = "noise.management.status.classroom-policy";
    internal const string SwitchStatusCapability = StatusCapability + ".v2";
    internal const string ManualStudyStatusCapability = StatusCapability + ".v4";

    internal static async Task<HostResponse> ReadAsync(string pipe, CancellationToken token, bool includeClassroomSwitch = false)
    {
        if (includeClassroomSwitch)
        {
            var current = await HostClient.RequestAsync(pipe, SwitchStatusCapability, token);
            if (current is not { Outcome: "Rejected", ErrorCode: "UnknownCapability" }) return current;
        }
        var response = await HostClient.RequestAsync(pipe, StatusCapability, token);
        if (response is { Outcome: "Rejected", ErrorCode: "UnknownCapability" })
            return await HostClient.RequestAsync(pipe, "noise.management.status", token);
        return response;
    }

    internal static string Mode(NoiseProtectionState state, string purpose)
    {
        if (state.ClassroomPolicy is not { } policy) return "default";
        string? mode = purpose switch
        {
            "start-manual-study" => policy.StartManualStudy,
            "switch-classroom-mode" => policy.SwitchClassroomMode,
            "maintenance" => policy.ExitHost,
            "stop" when state.Protected => policy.StopScheduled,
            _ => null
        };
        if (mode is null) return "default";
        return string.IsNullOrWhiteSpace(policy.Version) ? "unavailable" : mode;
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

    internal static string? Blocked(NoiseProtectionState state, string mode, string? purpose = null) => mode switch
    {
        "deny" => Describe(mode),
        _ when purpose == "switch-classroom-mode" && state.Protected && state.ClassroomPolicy is { } policy &&
            (policy.StopScheduled != "default" || mode != "default") => policy.StopScheduled == "deny"
                ? "本场定时监测禁止现场停止，请等本场结束后再切换课堂模式。"
                : "本场定时监测正在运行。请先到“自习监测”按本场停止权限办理，再切换课堂模式。",
        "default" when state.Protected && !state.Configured => state.ManagementSource == "School"
            ? "本班管理口令尚未同步或不可用。请在 NPEduConsole 核对本班口令，并检查学校互联。"
            : "本机管理口令尚未配置。请在无定时监测时到“自习 → 联机校准”设置。",
        "password" or "both" when !state.Configured => "本班管理口令尚未同步或不可用，请联系学校管理员。",
        "default" or "allow" or "password" or "usb" or "either" or "both" => null,
        _ => Describe(mode)
    };

    internal static NoiseManagementCommand AuthorizeCommand(NoiseProtectionState state, HostRequest target, string mode, string? secret)
    {
        var purpose = Protocol.NoisePurpose(target);
        // A switch without an explicit school rule retains the existing scheduled-interruption ticket.
        if (purpose == "switch-classroom-mode" && mode == "default") purpose = "change-environment";
        return new("authorize", secret, Purpose: purpose, TargetRequestId: target.RequestId,
            InstanceId: state.InstanceId, SessionId: state.SessionId,
            ClassroomPolicyVersion: mode == "default" ? null : state.ClassroomPolicy?.Version,
            ManualStudyTarget: purpose == "start-manual-study" ? target.ManualStudy : null);
    }

    internal static Guid? Ticket(HostResponse reply, NoiseProtectionState state)
        => reply is { Outcome: "Succeeded", NoiseProtection: { Ticket: { } ticket } proof } && ticket != Guid.Empty &&
            proof.InstanceId == state.InstanceId && proof.SessionId == state.SessionId ? ticket : null;
}
