namespace NPEduTools.Contracts;

// Independent local management capability. Secrets are never echoed in responses or logs.
public sealed record NoiseManagementCommand(string Action, string? Secret = null, string? NewSecret = null,
    string? Purpose = null, Guid? TargetRequestId = null, Guid? InstanceId = null, Guid? SessionId = null);
public sealed record NoiseProtectionState(bool Protected, bool Configured, Guid InstanceId, Guid? SessionId,
    string? ErrorCode = null, Guid? Ticket = null);
public static class NoiseManagementContract
{
    public static bool SecretValid(string? value) => value is { Length: >= 8 and <= 128 } && !value.Any(char.IsControl);
    public static bool Valid(NoiseManagementCommand c) => c.Action switch
    {
        "configure" => SecretValid(c.NewSecret) && (c.Secret is null || SecretValid(c.Secret)) &&
            c.Purpose is null && c.TargetRequestId is null && c.InstanceId is null && c.SessionId is null,
        "authorize" => SecretValid(c.Secret) && c.NewSecret is null &&
            c.Purpose is "stop" or "maintenance" or "change-environment" &&
            c.TargetRequestId is { } id && id != Guid.Empty && c.InstanceId is { } host && host != Guid.Empty &&
            c.SessionId is { } session && session != Guid.Empty,
        _ => false
    };
}
