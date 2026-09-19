namespace NPEduTools.Contracts;

public sealed record LessonSlot(int Number, Guid SubjectId, string Subject, DateTimeOffset Start,
    DateTimeOffset End, bool Enabled);

/// <summary>A bounded, detached snapshot of the currently effective day's timetable.</summary>
public sealed record DaySchedule(Guid ProfileId, Guid PlanId, Guid LayoutId, string Name, DateOnly Date,
    string Revision, DateTimeOffset SampledAt, bool Enabled, bool ClockVerified, string ClockMessage,
    LessonSlot[] Lessons);

/// <summary>A projection only. Never accepted by the rehearsal engine as the current day's source.</summary>
public sealed record DayForecast(Guid BridgeInstanceId, DateOnly SchoolDate, DateOnly RequestedDate,
    DaySchedule Schedule, bool Forecast = true);
