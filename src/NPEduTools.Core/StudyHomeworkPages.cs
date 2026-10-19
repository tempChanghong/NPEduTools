using System.Globalization;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record StudyHomeworkPart(StudyHomeworkItem Item, string Text, int Part, int Parts);
public sealed record StudyHomeworkPage(bool Wide, IReadOnlyList<StudyHomeworkPart> Items, bool Tall = false);

public static class StudyHomeworkPages
{
    public const double CompactWidth = 535, CompactHeight = 72, WideWidth = 835, WideHeight = 218;
    public static StudyHomeworkPage[] Build(IReadOnlyList<StudyHomeworkItem> work, Func<string, double, double> height)
    {
        var fragments = new List<(StudyHomeworkPart Part, int Layout)>();
        foreach (var item in work.DistinctBy(w => w.Id))
        {
            string content = string.IsNullOrWhiteSpace(item.Title) ? item.Content : string.IsNullOrWhiteSpace(item.Content) ? item.Title : item.Title + "\n" + item.Content;
            if (string.IsNullOrWhiteSpace(content)) continue;
            // Very long content goes straight to wide pages; never shape its entire body just to test a small card.
            if (content.Length <= 1024)
            {
                double measured = height(content, CompactWidth);
                if (measured <= WideHeight)
                { fragments.Add((new(item, content, 1, 1), measured <= CompactHeight ? 0 : 1)); continue; }
            }
            var parts = new List<string>();
            int[] positions = StringInfo.ParseCombiningCharacters(content); int start = 0;
            while (start < positions.Length)
            {
                int low = start + 1, high = Math.Min(positions.Length, start + 32), best = start;
                int offset = positions[start];
                // Exponential search bounds measurement to roughly one page instead of repeatedly
                // shaping all remaining text (which becomes quadratic for hundreds of paragraphs).
                while (high < positions.Length)
                {
                    int end = positions[high];
                    if (height(content[offset..end], WideWidth) > WideHeight) break;
                    low = high; high = Math.Min(positions.Length, start + (high - start) * 2);
                }
                while (low <= high)
                {
                    int count = (low + high) / 2, end = count == positions.Length ? content.Length : positions[count];
                    if (height(content[offset..end], WideWidth) <= WideHeight) { best = count; low = count + 1; } else high = count - 1;
                }
                if (best == start) throw new InvalidOperationException("A text element exceeds the homework area.");
                // Prefer nearby paragraph/sentence boundaries, retaining every source character.
                if (best < positions.Length)
                    for (int cut = best - 1; cut >= start + Math.Max(1, (best - start) / 2); cut--)
                        if (content[positions[cut] - 1] is '\n' or '。' or '；' or '！' or '？') { best = cut; break; }
                int limit = best == positions.Length ? content.Length : positions[best];
                parts.Add(content[offset..limit]); start = best;
            }
            for (int i = 0; i < parts.Count; i++) fragments.Add((new(item, parts[i], i + 1, parts.Count), 2));
        }
        var pages = new List<StudyHomeworkPage>(); var current = new List<StudyHomeworkPart>(); int layout = 0;
        static int Capacity(int kind) => kind == 2 ? 2 : kind == 1 ? 3 : 6;
        void Flush() { if (current.Count > 0) { pages.Add(new(layout == 2, current.ToArray(), layout == 1)); current.Clear(); } layout = 0; }
        foreach (var fragment in fragments)
        {
            int needed = Math.Max(layout, fragment.Layout);
            if (current.Count + 1 > Capacity(needed)) Flush();
            layout = Math.Max(layout, fragment.Layout);
            current.Add(fragment.Part);
            if (current.Count == Capacity(layout)) Flush();
        }
        Flush(); return pages.Count > 0 ? pages.ToArray() : [new(false, [])];
    }
}

// Reading time is monotonic, independent of either the Windows clock or school time corrections.
public sealed class StudyHomeworkPager(TimeProvider clock)
{
    private long _sampled = clock.GetTimestamp(); private bool _running;
    public int IntervalSeconds { get; private set; } = 60;
    public int Page { get; private set; }
    public int PageCount { get; private set; } = 1;
    public double RemainingSeconds { get; private set; } = 60;
    public void Configure(int seconds)
    { if (seconds is < 15 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds)); if (seconds != IntervalSeconds) { IntervalSeconds = seconds; Restart(); } }
    private void Restart() { RemainingSeconds = IntervalSeconds; _sampled = clock.GetTimestamp(); _running = false; }
    public void Reset(int pages) { PageCount = Math.Max(1, pages); Page = 0; Restart(); }
    public void Change(int delta) { Page = ((Page + delta) % PageCount + PageCount) % PageCount; Restart(); }
    public bool Advance(bool enabled, bool singlePageBoundary = false)
    {
        long now = clock.GetTimestamp(); double elapsed = clock.GetElapsedTime(_sampled, now).TotalSeconds; _sampled = now;
        if (!enabled) { _running = false; return false; }
        if (!_running) { RemainingSeconds = IntervalSeconds; _running = true; return false; }
        RemainingSeconds -= Math.Max(0, elapsed);
        if (RemainingSeconds > 0) return false;
        Page = (Page + 1) % PageCount; RemainingSeconds = IntervalSeconds;
        return PageCount > 1 || singlePageBoundary;
    }
}
