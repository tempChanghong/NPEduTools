using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record RecurringRecordingRule(Guid Id, string Name, bool Enabled, RecordingRules Filter,
    Guid? ProfileId = null, DateOnly? From = null, DateOnly? Until = null,
    TimeOnly? FixedStart = null, TimeOnly? FixedEnd = null, bool UseDefaultExclusions = true)
{
    public bool IsFixed => FixedStart is not null;
    public override string ToString() => $"{(Enabled ? "●" : "○")} {Name} · {(IsFixed ? "固定时段" : "跟随课表")}";
}
public sealed record DatedRecording(Guid Id, string Name, DateOnly Date, TimeOnly Start, TimeOnly End, bool Enabled = true);
public sealed record LessonDateOverride(Guid Id, Guid ProfileId, DateOnly Date, int Number,
    DateTimeOffset OriginalStart, DateTimeOffset OriginalEnd, string Subject, string Mode,
    int BeforeMinutes = 2, int AfterMinutes = 5);
public sealed record RecordingDayPolicy(DateOnly Date, Guid ProfileId, bool OnlyExplicit);
public sealed record SubjectPreference(Guid ProfileId, Guid SubjectId, bool Allow, bool Explicit = true);
public sealed record RecordingPlanBook(int Version, RecurringRecordingRule[] Recurring, DatedRecording[] Dated,
    LessonDateOverride[] Overrides, RecordingDayPolicy[] Days, string[] ExcludedNames,
    SubjectPreference[] Subjects, DateOnly? PauseThrough = null)
{
    public static readonly string[] DefaultExcluded = ["心理", "体育", "书法", "体育(室内)", "选修课", "班会", "技术", "艺术", "美术", "音乐",
        "物理(实验)", "化学(实验)", "生物(实验)", "晨测", "早读", "午自习", "报告厅活动"];
    public static RecordingPlanBook Create() => new(1, [new(Guid.NewGuid(), "默认课程", true, new())], [], [], [], [.. DefaultExcluded], []);
}

public static class CalendarRecordingPlanner
{
    public static string NormalizeSubject(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s*([()])\s*", "$1");
    public static string? Validate(RecordingPlanBook book)
    {
        if (book.Version != 1 || book.Recurring is null or { Length: > 64 } || book.Dated is null or { Length: > 256 } ||
            book.Overrides is null or { Length: > 512 } || book.Days is null or { Length: > 256 } ||
            book.ExcludedNames is null or { Length: > 128 } || book.Subjects is null or { Length: > 1024 }) return "计划格式或数量无效。";
        if (book.Recurring.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 ||
                r.Filter is null || RecordingPlanner.Validate(r.Filter) is not null || r.From > r.Until ||
                (r.FixedStart is null) != (r.FixedEnd is null) || r.FixedStart >= r.FixedEnd || r.ProfileId == Guid.Empty ||
                r.Filter.SubjectIds?.Any(id => id == Guid.Empty) == true) ||
            book.Recurring.Select(r => r.Id).Distinct().Count() != book.Recurring.Length) return "周期规则无效；固定时段必须在同一天内结束。";
        if (book.Dated.Any(r => r is null || r.Id == Guid.Empty || string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 80 || r.End <= r.Start) ||
            book.Dated.Select(r => r.Id).Distinct().Count() != book.Dated.Length) return "指定日期时段无效。";
        if (book.Overrides.Any(o => o is null || o.Id == Guid.Empty || o.ProfileId == Guid.Empty || o.Number is < 1 or > 64 ||
            o.Mode is not ("Include" or "Exclude") || o.BeforeMinutes is < 0 or > 5 || o.AfterMinutes is < 0 or > 5 ||
            o.OriginalEnd <= o.OriginalStart || o.OriginalStart.Offset != TimeSpan.Zero || o.OriginalEnd.Offset != TimeSpan.Zero ||
            DateOnly.FromDateTime(o.OriginalStart.Date) != o.Date || DateOnly.FromDateTime(o.OriginalEnd.Date) != o.Date ||
            o.Subject is null or { Length: > 256 }) ||
            book.Overrides.Select(o => (o.Date, o.ProfileId, o.Number)).Distinct().Count() != book.Overrides.Length)
            return "单日课程覆盖无效或重复。";
        if (book.Days.Any(d => d is null || d.ProfileId == Guid.Empty) ||
            book.Days.Select(d => (d.Date, d.ProfileId)).Distinct().Count() != book.Days.Length ||
            book.Subjects.Any(s => s is null || s.SubjectId == Guid.Empty || s.ProfileId == Guid.Empty) ||
            book.Subjects.Select(s => (s.ProfileId, s.SubjectId)).Distinct().Count() != book.Subjects.Length ||
            book.ExcludedNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100)) return "日期或科目设置无效。";
        return null;
    }
    public static RecordingPlanBook BindDefaultSubjects(RecordingPlanBook book, DaySchedule day)
    {
        if (day.ProfileId == Guid.Empty) return book;
        var excluded = book.ExcludedNames.Select(NormalizeSubject).ToHashSet(StringComparer.Ordinal);
        var added = day.Lessons.Where(l => l.SubjectId != Guid.Empty && excluded.Contains(NormalizeSubject(l.Subject)) &&
            !book.Subjects.Any(p => p.ProfileId == day.ProfileId && p.SubjectId == l.SubjectId))
            .DistinctBy(l => l.SubjectId).Select(l => new SubjectPreference(day.ProfileId, l.SubjectId, false, false)).ToArray();
        return added.Length == 0 || book.Subjects.Length >= 1024 ? book : book with { Subjects = book.Subjects.Concat(added.Take(1024 - book.Subjects.Length)).ToArray() };
    }
    private static bool Allowed(RecordingPlanBook book, RecurringRecordingRule rule, Guid profile, LessonSlot lesson)
    {
        var choice = book.Subjects.FirstOrDefault(s => s.ProfileId == profile && s.SubjectId == lesson.SubjectId);
        if (choice is { Explicit: true }) return choice.Allow;
        if (!rule.UseDefaultExclusions) return true;
        return choice?.Allow ?? !book.ExcludedNames.Any(n => NormalizeSubject(n) == NormalizeSubject(lesson.Subject));
    }
    private static bool OnDate(RecurringRecordingRule r, DateOnly date) => r.Enabled &&
        (r.Filter.Weekdays & (1 << (int)date.DayOfWeek)) != 0 && (r.From is null || date >= r.From) && (r.Until is null || date <= r.Until);
    public static PlannedRecording[] Build(RecordingPlanBook book, DateOnly date, DaySchedule? source)
    {
        if (Validate(book) is { } error) throw new InvalidDataException(error);
        if (source is not null && source.Date != date) throw new InvalidDataException("课表日期与计划日期不一致。");
        bool only = book.Days.Any(d => d.Date == date && d.ProfileId == source?.ProfileId && d.OnlyExplicit);
        bool paused = book.PauseThrough is { } until && date <= until;
        var rows = new List<PlannedRecording>();
        var rules = book.Recurring.Where(r => OnDate(r, date)).ToArray();
        var overrides = book.Overrides.Where(o => o.Date == date && o.ProfileId == source?.ProfileId).ToArray();
        if (source is not null)
        {
            var basePlans = RecordingPlanner.Build(source, new());
            foreach (var p in basePlans)
            {
                var lesson = p.Lesson;
                var bound = overrides.FirstOrDefault(o => o.Number == lesson.Number);
                bool mapped = bound is not null && bound.OriginalStart < lesson.End && lesson.Start < bound.OriginalEnd;
                var matching = rules.Where(r => !r.IsFixed && (r.ProfileId is null || r.ProfileId == source.ProfileId) &&
                    (r.Filter.LessonNumbers is not { Length: > 0 } || r.Filter.LessonNumbers.Contains(lesson.Number)) &&
                    (r.Filter.AllSubjects || r.ProfileId == source.ProfileId && r.Filter.SubjectIds?.Contains(lesson.SubjectId) == true)).ToArray();
                var allowed = matching.Where(r => Allowed(book, r, source.ProfileId, lesson)).ToArray();
                bool selected = !only && allowed.Length != 0;
                bool conflict = false;
                string reason = matching.Length != 0 && allowed.Length == 0 ? "科目已排除" : only ? "本日仅执行明确安排" : "未匹配周期规则";
                int before = allowed.FirstOrDefault()?.Filter.BeforeMinutes ?? 2, after = allowed.FirstOrDefault()?.Filter.AfterMinutes ?? 5;
                string sources = string.Join("、", allowed.Select(r => r.Name));
                if (selected)
                {
                    conflict = allowed.Any(r => r.Filter.BeforeMinutes != before || r.Filter.AfterMinutes != after);
                    reason = conflict ? "多条周期规则的余量不同，请统一或设置单日覆盖" : "周期规则已纳入";
                }
                if (bound is not null)
                {
                    if (!mapped) { selected = false; conflict = true; reason = "课程时段已改变，需重新确认单日安排"; }
                    else
                    {
                        selected = bound.Mode == "Include"; conflict = false;
                        before = bound.BeforeMinutes; after = bound.AfterMinutes; sources = "指定日期安排";
                        reason = selected ? "单日明确录制（覆盖周期及默认排除）" : "单日明确不录";
                        if (bound.Subject != lesson.Subject || bound.OriginalStart != lesson.Start || bound.OriginalEnd != lesson.End) reason += "；课程内容/时间已变化";
                    }
                }
                if (!source.Enabled || !lesson.Enabled) { selected = false; reason = !source.Enabled ? "课表未启用" : "课程停用或科目未定义"; }
                if (paused) { selected = false; reason = "暂停至 " + book.PauseThrough; }
                rows.Add(p with { Start = lesson.Start.AddMinutes(-before), End = lesson.End.AddMinutes(after), Selected = selected,
                    Conflict = conflict, Reason = reason, Sources = sources });
            }
            foreach (var missing in overrides.Where(o => source.Lessons.All(l => l.Number != o.Number)))
                rows.Add(new($"review/{missing.Id}", new(missing.Number, Guid.Empty, missing.Subject, missing.OriginalStart, missing.OriginalEnd, false),
                    missing.OriginalStart, missing.OriginalEnd, false, true, "原课程已不存在，需复核或清除单日安排", Sources: "待复核"));
        }
        foreach (var rule in rules.Where(r => r.IsFixed))
            rows.Add(Fixed(rule.Id, rule.Name, date, rule.FixedStart!.Value, rule.FixedEnd!.Value, !paused && !only, "周期固定时段"));
        foreach (var item in book.Dated.Where(d => d.Date == date && d.Enabled))
            rows.Add(Fixed(item.Id, item.Name, date, item.Start, item.End, !paused, "指定日期固定时段"));
        var sorted = rows.OrderBy(p => p.Start).ToArray();
        for (int i = 0; i < sorted.Length; i++)
        {
            var p = sorted[i]; if (p.Fixed || !p.Selected || p.Conflict) continue;
            var others = sorted.Where(x => !x.Fixed && x != p && x.Lesson.Enabled).OrderBy(x => x.Lesson.Start).ToArray();
            if (others.Any(x => p.Lesson.Start < x.Lesson.End && x.Lesson.Start < p.Lesson.End) ||
                others.Any(x => x.Lesson.Start < p.Lesson.Start && p.Start < x.Lesson.End))
            { sorted[i] = p with { Conflict = true, Reason = "正式课时或课前窗口冲突" }; continue; }
            var next = others.FirstOrDefault(x => x.Lesson.Start >= p.Lesson.End);
            if (next is not null)
            {
                var boundary = next.Selected && !next.Conflict ? next.Start : next.Lesson.Start;
                if (boundary < p.Lesson.End) boundary = p.Lesson.End;
                if (boundary < p.End) sorted[i] = p with { End = boundary, Reason = p.Reason + "；课后余量已缩短" };
            }
        }
        for (int i = 0; i < sorted.Length; i++)
            for (int j = i + 1; j < sorted.Length; j++)
            {
                var a = sorted[i]; var b = sorted[j];
                if (a.Selected && b.Selected && (a.Fixed || b.Fixed) && a.Start < b.End && b.Start < a.End)
                {
                    sorted[i] = a with { Conflict = true, Reason = "固定时段与其他任务重叠，请调整" };
                    sorted[j] = b with { Conflict = true, Reason = "固定时段与其他任务重叠，请调整" };
                }
            }
        return sorted;
    }
    private static PlannedRecording Fixed(Guid id, string name, DateOnly date, TimeOnly start, TimeOnly end, bool enabled, string source)
    {
        var a = new DateTimeOffset(date.ToDateTime(start), TimeSpan.Zero); var b = new DateTimeOffset(date.ToDateTime(end), TimeSpan.Zero);
        return new($"fixed/{id:N}/{date:yyyy-MM-dd}", new(0, Guid.Empty, name, a, b, true), a, b, enabled, false,
            enabled ? "按学校时间执行，不使用科目排除或课前课后余量" : "当天已暂停或仅执行明确安排", true, source);
    }
}

public sealed class RecordingPlanBookStore(string path)
{
    public bool Migrated { get; private set; }
    public RecordingPlanBook Read(PreviewState? legacy = null)
    {
        if (!File.Exists(path))
        {
            if (legacy is null) return RecordingPlanBook.Create();
            Migrated = true;
            return RecordingPlanBook.Create() with { Recurring = [new(Guid.NewGuid(), "旧规则草稿（核对后启用）", false, legacy.Rules, UseDefaultExclusions: false)] };
        }
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("录课计划文件过大，原文件已保留。");
        var book = JsonSerializer.Deserialize<RecordingPlanBook>(File.ReadAllText(path)) ?? throw new InvalidDataException("计划为空。");
        if (CalendarRecordingPlanner.Validate(book) is { } error) throw new InvalidDataException(error + "原文件已保留。");
        return book;
    }
    public void Save(RecordingPlanBook book)
    {
        if (CalendarRecordingPlanner.Validate(book) is { } error) throw new InvalidDataException(error);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(book);
        if (bytes.Length > 512 * 1024) throw new InvalidDataException("录课计划文件超过大小限制。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", bytes); File.Move(path + ".tmp", path, true);
    }
}
