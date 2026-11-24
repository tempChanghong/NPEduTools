using System.Text.Json.Serialization;

namespace NPEduTools.Contracts;

// Independent local management capability. Secrets are never echoed in responses or logs.
public sealed record NoiseManagementCommand(string Action, string? Secret = null, string? NewSecret = null,
    string? Purpose = null, Guid? TargetRequestId = null, Guid? InstanceId = null, Guid? SessionId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DeviceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PolicyToken = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ClassroomPolicyVersion = null);
public sealed record ClassroomActionPolicyState(string Version, string StopScheduled, string ExitHost,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SwitchClassroomMode = null);
public sealed record NoiseProtectionState(bool Protected, bool Configured, Guid InstanceId, Guid? SessionId,
    string? ErrorCode = null, Guid? Ticket = null, string ManagementSource = "Local", long CredentialRevision = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassroomActionPolicyState? ClassroomPolicy = null);
public static class NoiseManagementContract
{
    public static bool SecretValid(string? value) => value is { Length: >= 8 and <= 128 } && !value.Any(char.IsControl);
    public static bool Valid(NoiseManagementCommand c)
    {
        if (c.ClassroomPolicyVersion is { } version)
            return version.Length == 64 && version.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                c.Action == "authorize" && c.NewSecret is null && c.DeviceId is null && c.PolicyToken is null &&
                (c.Secret is null || SecretValid(c.Secret)) && c.TargetRequestId is { } target && target != Guid.Empty &&
                c.InstanceId is { } host && host != Guid.Empty && c.SessionId != Guid.Empty &&
                (c.Purpose is "maintenance" or "switch-classroom-mode" || c.Purpose == "stop" && c.SessionId is not null);
        if (c.Action == "replace-microphone")
            return SecretValid(c.Secret) && NoiseContract.ValidDeviceId(c.DeviceId) && c.PolicyToken is { } token && token != Guid.Empty &&
                c.NewSecret is null && c.Purpose is null && c.TargetRequestId is null && c.InstanceId is null && c.SessionId is null;
        return c.DeviceId is null && c.PolicyToken is null && ValidManagementAction(c);
    }
    private static bool ValidManagementAction(NoiseManagementCommand c) => c.Action switch
    {
        "verify" or "unlock" => SecretValid(c.Secret) && c.NewSecret is null && c.Purpose is null && c.TargetRequestId is null && c.InstanceId is null && c.SessionId is null,
        "configure" => SecretValid(c.NewSecret) && (c.Secret is null || SecretValid(c.Secret)) &&
            c.Purpose is null && c.TargetRequestId is null && c.InstanceId is null && c.SessionId is null,
        "authorize" => SecretValid(c.Secret) && c.NewSecret is null &&
            c.Purpose is "stop" or "maintenance" or "change-environment" &&
            c.TargetRequestId is { } id && id != Guid.Empty && c.InstanceId is { } host && host != Guid.Empty &&
            c.SessionId is { } session && session != Guid.Empty,
        _ => false
    };
}
