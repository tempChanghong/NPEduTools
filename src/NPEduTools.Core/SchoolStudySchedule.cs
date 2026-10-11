using System.Globalization;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Matches KV studyWindowsForDate: date bounds and exceptions apply to the start date.
/// Offset zero is a school wall-calendar carrier, never a UTC conversion.</summary>
public static class SchoolStudySchedule
{
    private static bool Date(string? value, out DateOnly date) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out date) && date.Year is >= 2000 and <= 9998;
    private static DateOnly Parse(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string? Validate(StudySettings settings)
    {
        var s = settings.Schedule;
        if (settings.ExamSchedule is { } exam && Validate(settings with { Schedule = exam, ExamSchedule = null }) is { } examError) return examError;
        if (s.Rules.Count == 0) return s.TimeZone == "Asia/Shanghai" && s.Exceptions.Count == 0 && (s.EffectiveFrom == "" || Date(s.EffectiveFrom, out _)) && (s.EffectiveTo is null || Date(s.EffectiveTo, out _)) ? null : "INVALID_DATES";
        if (s.TimeZone != "Asia/Shanghai" || !Date(s.EffectiveFrom, out var from) ||
            s.EffectiveTo is not null && (!Date(s.EffectiveTo, out var to) || to < from)) return "INVALID_DATES";
        if (SchoolNoiseSchedule.Validate(new("Override", s.Rules)) is { } rule) return rule;
        var dates = new HashSet<DateOnly>();
        foreach (var e in s.Exceptions)
            if (!Date(e.Date, out var day) || day < from || s.EffectiveTo is not null && day > Parse(s.EffectiveTo) ||
                !dates.Add(day) || e.Kind is not ("Skip" or "UseWeekday") ||
                (e.Kind == "Skip" ? e.Weekday is not null : e.Weekday is not (>= 1 and <= 7))) return "INVALID_CALENDAR";
        if (settings.Noise.Medium is { } medium && (settings.Noise.High is null || medium >= settings.Noise.High)) return "INVALID_NOISE";
        if (settings.Noise.Estimated is { } estimated && new[] { estimated.HighDbA, estimated.MediumDbA }.Any(v => v is not null && (!double.IsFinite(v.Value) || v is < 30 or > 130))) return "INVALID_NOISE";
        if (settings.Noise.HighContinuousSeconds != settings.Photos.ContinuousSeconds) return "INCONSISTENT_THRESHOLD_DURATION";
        if (settings.Photos.Enabled && (!settings.Noise.Enabled || settings.Noise.High is null)) return "PHOTO_THRESHOLD_REQUIRED";
        if (settings.Photos.Enabled && settings.Photos.RetentionDays > 30 && !settings.Photos.ExtendedRetentionAcknowledged) return "RETENTION_ACK_REQUIRED";
        var h = settings.Homework;
        if (h.SourceMode == "OwnClass" && h.SourceClassIds.Count != 0 || h.Enabled && h.SourceMode == "SelectedClasses" && h.SourceClassIds.Count == 0)
            return "INVALID_HOMEWORK";
        return null;
    }

    public static SchoolNoiseWindows Windows(StudySettings settings, DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now.Year is < 2000 or > 9998) throw new ArgumentException("INVALID_SCHOOL_CALENDAR");
        if (Validate(settings) is { } error) throw new ArgumentException(error);
        var s = settings.Schedule;
        if (s.Rules.Count == 0) return new(null, null);
        var from = Parse(s.EffectiveFrom);
        var to = s.EffectiveTo is null ? new DateOnly(9998, 12, 31) : Parse(s.EffectiveTo);
        var today = DateOnly.FromDateTime(now.Date);
        var day = today.AddDays(-1) > from ? today.AddDays(-1) : from;
        var spans = new List<SchoolNoiseWindow>();
        // At most 366 exception dates and one weekly cycle. No scan from a distant effectiveFrom.
        for (int index = 0; index < 375 && day <= to; index++)
        {
            spans.AddRange(ForDateValidated(settings, day));
            // Include the following date before stopping, so midnight adjacency gets merged.
            if (day > today && spans.Any(w => w.Start > now && DateOnly.FromDateTime(w.Start.Date) < day)) break;
            if (day == to || day == new DateOnly(9998, 12, 31)) break;
            day = day.AddDays(1);
        }
        var merged = new List<SchoolNoiseWindow>();
        foreach (var w in spans.OrderBy(w => w.Start).ThenBy(w => w.End))
            if (merged.Count > 0 && w.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = w.End > merged[^1].End ? w.End : merged[^1].End };
            else merged.Add(w);
        if (merged.Any(w => (w.End - w.Start).TotalMinutes > SchoolNoiseSchedule.MaxWindowMinutes))
            throw new ArgumentException("MERGED_WINDOW_TOO_LONG");
        return new(merged.FirstOrDefault(w => w.Start <= now && now < w.End), merged.FirstOrDefault(w => w.Start > now));
    }
    public static IReadOnlyList<SchoolNoiseWindow> ForDate(StudySettings settings, DateOnly day)
    {
        if (day.Year is < 2000 or > 9998) throw new ArgumentException("INVALID_SCHOOL_CALENDAR");
        if (Validate(settings) is { } error) throw new ArgumentException(error);
        return ForDateValidated(settings, day);
    }
    private static IReadOnlyList<SchoolNoiseWindow> ForDateValidated(StudySettings settings, DateOnly day)
    {
        var s = settings.Schedule;
        if (s.Rules.Count == 0) return [];
        if (day < Parse(s.EffectiveFrom) || s.EffectiveTo is not null && day > Parse(s.EffectiveTo)) return [];
        var special = s.Exceptions.FirstOrDefault(e => e.Date == day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (special?.Kind == "Skip") return [];
        int weekday = special?.Weekday ?? ((int)day.DayOfWeek == 0 ? 7 : (int)day.DayOfWeek);
        var midnight = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var spans = s.Rules.Where(r => r.Days.Contains(weekday)).Select(rule => {
            int start = Minute(rule.Start), end = Minute(rule.End);
            return new SchoolNoiseWindow(midnight.AddMinutes(start), midnight.AddMinutes(start + (end - start + 1440) % 1440));
        }).OrderBy(w => w.Start).ToArray();
        var merged = new List<SchoolNoiseWindow>();
        foreach (var w in spans)
            if (merged.Count > 0 && w.Start <= merged[^1].End)
                merged[^1] = merged[^1] with { End = w.End > merged[^1].End ? w.End : merged[^1].End };
            else merged.Add(w);
        return merged;
    }
    private static int Minute(string value) => int.Parse(value[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(value[3..], CultureInfo.InvariantCulture);
}
