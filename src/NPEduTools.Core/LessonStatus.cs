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

public sealed record StatusQuery(TimeSpan Timeout, TimeSpan ObservationWindow, bool IncludeSchedule = false, DateOnly? SchoolDate = null);

public sealed record StatusResult(string Outcome, string? ErrorCode, string Message, LessonStatus? Status = null,
    NPEduTools.Contracts.DaySchedule? Schedule = null, NPEduTools.Contracts.DayForecast? Forecast = null);

public interface ILessonStatusReader
{
    Task<StatusResult> ReadAsync(StatusQuery query, CancellationToken cancellationToken);
}
