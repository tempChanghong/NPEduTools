namespace NPEduTools.Contracts;

// Local Host commands; explicit session identity fences an old display after scheduled takeover.
public sealed record ManualStudyCommand(string Action, Guid InstanceId, long Revision, Guid? SessionId = null,
    string? DeviceId = null, DateTimeOffset? StartUtc = null, DateTimeOffset? EndUtc = null, int? HideSeconds = 600);
public sealed record ManualStudyState(Guid InstanceId, long Revision, Guid? SessionId, string State, string? Reason,
    DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, bool HasPlannedEnd, int? HideSeconds, int DisplayLeadSeconds = 120);
