using System.Globalization;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

/// <summary>Pure school-calendar calculations. Offset zero carries wall-clock fields, never UTC conversion.</summary>
public static class SchoolNoiseSchedule
{
    private const int WeekMinutes = 7 * 1440;
    public const int MaxRules = 32, MaxWindowMinutes = 180;
    private sealed record Span(int Start, int End);

    public static string? Validate(SchoolNoisePolicy? policy, bool grade = false)
    {
        if (policy is null) return null;
        if (policy.Mode is not ("Inherit" or "Override" or "Disabled") || grade && policy.Mode == "Inherit") return "INVALID_MODE";
        if (policy.Rules is null || policy.Rules.Count > MaxRules) return "INVALID_RULES";
        if (policy.Mode != "Override") return policy.Rules.Count == 0 ? null : "UNEXPECTED_RULES";
        if (policy.Rules.Count == 0) return "EMPTY_RULES";
        foreach (var rule in policy.Rules)
        {
            if (rule is null || rule.Days is null || rule.Days.Count is < 1 or > 7 ||
                rule.Days.Any(d => d is < 1 or > 7) || rule.Days.Distinct().Count() != rule.Days.Count ||
                !TryMinute(rule.Start, out var start) || !TryMinute(rule.End, out var end)) return "INVALID_RULE";
            int length = (end - start + 1440) % 1440;
            if (length == 0 || length > MaxWindowMinutes) return "INVALID_DURATION";
        }
        return Merge(Expand(policy.Rules)).Any(w => w.End - w.Start > MaxWindowMinutes) ? "MERGED_WINDOW_TOO_LONG" : null;
    }

    public static EffectiveNoiseSchedule Resolve(SchoolNoisePolicy? grade, SchoolNoisePolicy? classroom)
    {
        if (Validate(grade, true) is { } g) throw new ArgumentException(g);
        if (Validate(classroom) is { } c) throw new ArgumentException(c);
        var selected = classroom is null || classroom.Mode == "Inherit" ? grade : classroom;
        return selected is null ? new("None", []) : selected.Mode == "Disabled" ? new("Disabled", [])
            : new(ReferenceEquals(selected, classroom) ? "Class" : "Grade", selected.Rules);
    }

    public static SchoolNoiseWindows Windows(EffectiveNoiseSchedule policy, DateTimeOffset now)
    {
        if (now.Offset != TimeSpan.Zero || now.Year is < 2000 or > 9998) throw new ArgumentException("INVALID_SCHOOL_CALENDAR");
        if (Validate(new(policy.Rules.Count == 0 ? "Disabled" : "Override", policy.Rules)) is { } error) throw new ArgumentException(error);
        int weekday = ((int)now.DayOfWeek + 6) % 7;
        var monday = new DateTimeOffset(now.Date.AddDays(-weekday), TimeSpan.Zero);
        var windows = Merge(Expand(policy.Rules)).Select(w => new SchoolNoiseWindow(monday.AddMinutes(w.Start), monday.AddMinutes(w.End))).ToArray();
        return new(windows.FirstOrDefault(w => w.Start <= now && now < w.End), windows.FirstOrDefault(w => w.Start > now));
    }

    public static bool Overlaps(SchoolNoiseWindow left, SchoolNoiseWindow right) => left.Start < right.End && right.Start < left.End;

    private static bool TryMinute(string? value, out int minute)
    {
        minute = 0;
        if (value is null || value.Length != 5 || value[2] != ':' || value.Where((_, i) => i != 2).Any(c => c is < '0' or > '9')) return false;
        int hour = int.Parse(value[..2], CultureInfo.InvariantCulture), rest = int.Parse(value[3..], CultureInfo.InvariantCulture);
        if (hour > 23 || rest > 59) return false;
        minute = hour * 60 + rest; return true;
    }

    private static IEnumerable<Span> Expand(IReadOnlyList<SchoolNoiseRule> rules)
    {
        // Three weeks expose Sunday/Monday merge violations and provide this and next occurrence.
        foreach (int week in new[] { -1, 0, 1 })
            foreach (var rule in rules)
            {
                TryMinute(rule.Start, out int start); TryMinute(rule.End, out int end);
                int length = (end - start + 1440) % 1440;
                foreach (int day in rule.Days)
                { int from = week * WeekMinutes + (day - 1) * 1440 + start; yield return new(from, from + length); }
            }
    }

    private static IReadOnlyList<Span> Merge(IEnumerable<Span> spans)
    {
        var result = new List<Span>();
        foreach (var span in spans.OrderBy(s => s.Start).ThenBy(s => s.End))
        {
            if (result.Count > 0 && span.Start <= result[^1].End)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, span.End) };
            else result.Add(span);
        }
        return result;
    }
}
