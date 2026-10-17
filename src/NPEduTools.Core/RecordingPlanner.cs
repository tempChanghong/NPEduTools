using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record RecordingRules(int BeforeMinutes = 2, int AfterMinutes = 5, int Weekdays = 127,
    bool AllSubjects = true, Guid[]? SubjectIds = null, int[]? LessonNumbers = null);
public sealed record PlannedRecording(string Key, LessonSlot Lesson, DateTimeOffset Start, DateTimeOffset End,
    bool Selected, bool Conflict, string Reason, bool Fixed = false, string Sources = "", string OverlapNotice = "");

public static class RecordingPlanner
{
    public const int MaximumMarginMinutes = 10;
    public static string? ValidateMargins(int before, int after) =>
        before is < 0 or > MaximumMarginMinutes || after is < 0 or > MaximumMarginMinutes
            ? $"课前和课后余量必须为 0～{MaximumMarginMinutes} 分钟。" : null;
    public static string? Validate(RecordingRules rules)
    {
        if (ValidateMargins(rules.BeforeMinutes, rules.AfterMinutes) is { } error) return error;
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
        return ResolveOverlaps(FitCourseWindows(rows));
    }

    // The preview and executable calendar use the same boundary policy. Margins
    // yield to formal lessons. Remaining overlaps are handled chronologically.
    internal static PlannedRecording[] FitCourseWindows(PlannedRecording[] rows)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].Fixed || !rows[i].Selected || rows[i].Conflict) continue;
            var row = rows[i];
            var previous = rows.Where(r => !r.Fixed && r != row && r.Lesson.Enabled && r.Lesson.Start < row.Lesson.Start)
                .OrderBy(r => r.Lesson.End).LastOrDefault();
            var boundary = previous is null ? row.Start : previous.Lesson.End < row.Lesson.Start ? previous.Lesson.End : row.Lesson.Start;
            if (row.Start < boundary)
                rows[i] = row with { Start = boundary, Reason = row.Reason + "；课前余量已缩短", OverlapNotice = "与上一节相邻，已缩短课前余量" };
        }
        // Apply tails only after all lead windows have been fitted.
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.Fixed || !row.Selected || row.Conflict) continue;
            var next = rows.Where(r => !r.Fixed && r != row && r.Lesson.Enabled && r.Lesson.Start >= row.Lesson.End)
                .OrderBy(r => r.Lesson.Start).FirstOrDefault();
            if (next is not null)
            {
                var boundary = next.Selected && !next.Conflict ? next.Start : next.Lesson.Start;
                if (boundary < row.Lesson.End) boundary = row.Lesson.End;
                if (boundary < row.End) rows[i] = row with { End = boundary, Reason = row.Reason + "；课后余量已缩短，为下一节让出时间",
                    OverlapNotice = $"与下一节录制范围重叠，已缩短至 {boundary:HH:mm}；下一节优先" };
            }
        }
        return rows;
    }

    internal static PlannedRecording[] ResolveOverlaps(PlannedRecording[] rows)
    {
        var ordered = rows.OrderBy(p => p.Lesson.Start).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
        for (int i = 0; i < ordered.Length; i++)
            for (int j = i + 1; j < ordered.Length && ordered[j].Lesson.Start == ordered[i].Lesson.Start; j++)
                if (ordered[i].Selected && ordered[j].Selected && ordered[i].Start < ordered[j].End && ordered[j].Start < ordered[i].End)
                {
                    const string reason = "同一开始时间存在重叠录制，无法区分下一节，请调整";
                    ordered[i] = ordered[i] with { Conflict = true, Reason = reason, OverlapNotice = reason };
                    ordered[j] = ordered[j] with { Conflict = true, Reason = reason, OverlapNotice = reason };
                }
        // Resolve later tasks first: a task displaced by a still later one must
        // not take recording time away from an earlier surviving task.
        for (int i = ordered.Length - 1; i >= 0; i--)
        {
            if (!ordered[i].Selected || ordered[i].Conflict) continue;
            for (int j = i + 1; j < ordered.Length; j++)
            {
                var earlier = ordered[i]; var later = ordered[j];
                if (!later.Selected || later.Conflict || earlier.Start >= later.End || later.Start >= earlier.End) continue;
                string notice = $"录制范围重叠，下一节“{later.Lesson.Subject}”优先";
                ordered[i] = later.Start <= earlier.Start
                    ? earlier with { Selected = false, Reason = earlier.Reason + "；" + notice + "，本段不录", OverlapNotice = notice + "，本段不录" }
                    : earlier with { End = later.Start, Reason = earlier.Reason + $"；{notice}，本段截短至 {later.Start:HH:mm}", OverlapNotice = notice + $"，本段截短至 {later.Start:HH:mm}" };
                ordered[j] = later with { OverlapNotice = "与上一段录制重叠，本段优先", Reason = later.Reason + "；与上一段录制重叠，本段优先" };
                if (!ordered[i].Selected) break;
            }
        }
        return ordered.OrderBy(p => p.Start).ToArray();
    }

    public static PlannedRecording? FindReplacement(PlannedRecording[] plans, PlannedRecording active, bool sameProfile = true)
    {
        var candidates = plans.Where(p => p.Selected && !p.Conflict && p.Fixed == active.Fixed &&
            (p.Fixed ? p.Key == active.Key : sameProfile && p.Lesson.Start < active.Lesson.End && active.Lesson.Start < p.Lesson.End)).ToArray();
        // A still-known slot explicitly excluded or in conflict is withdrawn,
        // not a retiming of an overlapping neighbor. Only use the existing
        // unique-overlap fallback when the original slot identity disappeared.
        if (plans.Any(p => p.Key == active.Key)) return candidates.FirstOrDefault(p => p.Key == active.Key);
        return candidates.Length == 1 ? candidates[0] : null;
    }

    public static bool IsDifferentKnownLesson(PlannedRecording[]? plans, PlannedRecording current, DateTimeOffset start, DateTimeOffset end) =>
        plans?.Any(p => !p.Fixed && p.Key != current.Key && p.Lesson.Start == start && p.Lesson.End == end) == true;
}
