namespace NPEduTools.Core;

public sealed record LessonStatus(
    DateTimeOffset SampleStartedAt,
    DateTimeOffset SampleCompletedAt,
    string State,
    string? Subject,
    bool IsTimerRunning,
    bool IsClassPlanLoaded,
    bool IsClassPlanEnabled,
    int CurrentSelectedIndex,
    IReadOnlyDictionary<string, long> ObservedEvents);

public sealed record StatusQuery(TimeSpan Timeout, TimeSpan ObservationWindow, bool IncludeSchedule = false);

public sealed record StatusResult(string Outcome, string? ErrorCode, string Message, LessonStatus? Status = null,
    NPEduTools.Contracts.DaySchedule? Schedule = null);

public interface ILessonStatusReader
{
    Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken);
}
