using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record RecordingRules(int BeforeMinutes = 2, int AfterMinutes = 5, int Weekdays = 127,
    bool AllSubjects = true, Guid[]? SubjectIds = null, int[]? LessonNumbers = null);
public sealed record PlannedRecording(string Key, LessonSlot Lesson, DateTimeOffset Start, DateTimeOffset End,
    bool Selected, bool Conflict, string Reason, bool Fixed = false, string Sources = "");

public static class RecordingPlanner
{
    public static string? Validate(RecordingRules rules)
    {
        if (rules.BeforeMinutes is < 0 or > 5 || rules.AfterMinutes is < 0 or > 5) return "前后余量必须为 0～5 分钟。";
        if (rules.Weekdays is < 0 or > 127) return "星期选项无效。";
        if (rules.SubjectIds is { Length: > 64 } || rules.LessonNumbers is { Length: > 64 } ||
            rules.LessonNumbers?.Any(n => n is < 1 or > 64) == true) return "科目或节次选项无效。";
        return null;
    }

    public static PlannedRecording[] Build(DaySchedule source, RecordingRules rules)
    {
        if (Validate(rules) is { } error) throw new InvalidDataException(error);
        if (source.Lessons.Length > 64 || source.Lessons.Select(l => l.Number).Distinct().Count() != source.Lessons.Length ||
            source.Lessons.Any(l => l.Number is < 1 or > 64 || l.End <= l.Start || DateOnly.FromDateTime(l.Start.Date) != source.Date))
            throw new InvalidDataException("日程包含无效或重复时间点。");
        var rows = source.Lessons.OrderBy(l => l.Start).Select(lesson =>
        {
            string reason = !source.Enabled ? "课表未启用" : !lesson.Enabled ? "课程停用或科目未定义" :
                (rules.Weekdays & (1 << (int)lesson.Start.DayOfWeek)) == 0 ? "星期未选中" :
                !rules.AllSubjects && rules.SubjectIds?.Contains(lesson.SubjectId) != true ? "科目未选中" :
                rules.LessonNumbers is { Length: > 0 } && !rules.LessonNumbers.Contains(lesson.Number) ? "节次未选中" : "已纳入计划";
            // Temporary plan/layout IDs and subject changes must not reopen the same occurrence.
            string key = $"{source.ProfileId:N}/{source.Date:yyyy-MM-dd}/{lesson.Start:HHmmss}/{lesson.End:HHmmss}";
            return new PlannedRecording(key, lesson, lesson.Start.AddMinutes(-rules.BeforeMinutes),
                lesson.End.AddMinutes(rules.AfterMinutes), reason == "已纳入计划", false, reason);
        }).ToArray();
        for (int i = 0; i < rows.Length; i++)
        {
            if (!rows[i].Selected) continue;
            var row = rows[i];
            if (rows.Any(other => other != row && other.Lesson.Enabled &&
                row.Lesson.Start < other.Lesson.End && other.Lesson.Start < row.Lesson.End))
            { rows[i] = row with { Conflict = true, Reason = "正式课时重叠，暂不预演" }; continue; }
            var previous = rows.Take(i).LastOrDefault(r => r.Lesson.Enabled);
            if (previous is not null && row.Start < previous.Lesson.End)
            { rows[i] = row with { Conflict = true, Reason = "课前窗口进入上一节正式课时，请缩短提前量" }; continue; }
            var next = rows.Skip(i + 1).FirstOrDefault(r => r.Lesson.Enabled);
            if (next is not null)
            {
                var boundary = next.Selected ? next.Start : next.Lesson.Start;
                if (boundary < row.Lesson.End) boundary = row.Lesson.End;
                if (boundary < row.End) rows[i] = row with { End = boundary, Reason = "课后余量已缩短，为下一节让出时间" };
            }
        }
        return rows;
    }
}
