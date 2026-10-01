namespace NPEduTools.ClassIsland.Admin;

public sealed record AdminRequest(string Action, string Executable, string UserSid, int SessionId,
    string? ExpectedFingerprint = null, int? ExpectedProcessId = null, long? ExpectedProcessStarted = null);

public sealed record AdminStatus(string TaskState, string TaskMessage, string? Fingerprint,
    string ProcessState, string ProcessMessage, bool PluginInstalled,
    int? ProcessId = null, long? ProcessStarted = null);

public sealed record AdminResult(string Outcome, string Message, AdminStatus? Status = null, string? ErrorCode = null);
