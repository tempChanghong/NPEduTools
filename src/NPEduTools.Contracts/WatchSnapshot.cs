namespace NPEduTools.Contracts;

/// <summary>Every frame is a full snapshot, including heartbeats. Counters reset with ConnectionId.</summary>
public sealed record WatchSnapshot(
    int Version, Guid RequestId, Guid StreamId, long Sequence, Guid ConnectionId,
    DateTimeOffset UpdatedAt, string Outcome, string? ErrorCode, string Message, LessonStatusDto? Status);
