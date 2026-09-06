namespace NPEduTools.Contracts;

public sealed record LaunchSettings(long Revision, string? ExecutablePath);

public sealed record LaunchExecution(Guid RequestId, string ExecutablePath, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, string Outcome, string? ErrorCode, string Message, int? ProcessId = null);

public sealed record LaunchData(LaunchSettings Settings, LaunchExecution? Execution, string? StorageWarning = null);
