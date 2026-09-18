namespace NPEduTools.Contracts;

public sealed record LessonSlot(int Number, Guid SubjectId, string Subject, DateTimeOffset Start,
    DateTimeOffset End, bool Enabled);

/// <summary>A bounded, detached snapshot of the currently effective day's timetable.</summary>
public sealed record DaySchedule(Guid ProfileId, Guid PlanId, Guid LayoutId, string Name, DateOnly Date,
    string Revision, DateTimeOffset SampledAt, bool Enabled, bool ClockVerified, string ClockMessage,
    LessonSlot[] Lessons);
