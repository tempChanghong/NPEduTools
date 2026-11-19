using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

internal static class HomeworkReadingPositionResolver
{
    internal static HomeworkReadingPosition? Resolve(StudyHomeworkSnapshot? previous, StudyHomeworkSnapshot current, HomeworkReadingPosition? position)
    {
        var items = current.Items.Where(StudyHomeworkPages.IsDisplayable).ToArray();
        if (items.Length == 0) return null;
        if (position is not { } anchor) return new(items[0].Id, 0);
        var oldItems = previous?.Items;
        var next = items.FirstOrDefault(item => item.Id == anchor.ItemId);
        if (next is not null)
        {
            var old = ReferenceEquals(previous, current) ? null : oldItems?.FirstOrDefault(item => item.Id == next.Id);
            int offset = anchor.Offset;
            if (old is not null && (old.Content != next.Content || old.Title != next.Title))
                offset = MapEdit(StudyHomeworkPages.ReadingText(old), StudyHomeworkPages.ReadingText(next), offset);
            int length = string.IsNullOrWhiteSpace(next.Title) ? next.Content.Length :
                string.IsNullOrWhiteSpace(next.Content) ? next.Title.Length : next.Title.Length + 1 + next.Content.Length;
            return new(next.Id, Math.Clamp(offset, 0, length - 1));
        }
        if (oldItems is not null)
        {
            var live = items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            for (int i = 0; i < oldItems.Count; i++)
                if (oldItems[i].Id == anchor.ItemId)
                {
                    for (int d = 1; d < oldItems.Count; d++)
                    {
                        if (i + d < oldItems.Count && live.Contains(oldItems[i + d].Id)) return new(oldItems[i + d].Id, 0);
                        if (i >= d && live.Contains(oldItems[i - d].Id)) return new(oldItems[i - d].Id, 0);
                    }
                    break;
                }
        }
        return new(items[0].Id, 0);
    }
    private static int MapEdit(string old, string current, int offset)
    {
        offset = Math.Clamp(offset, 0, old.Length);
        int prefix = old.AsSpan().CommonPrefixLength(current);
        if (offset < prefix) return offset;
        int a = old.Length, b = current.Length;
        while (a > prefix && b > prefix && old[a - 1] == current[b - 1]) { a--; b--; }
        if (offset >= a) return b + offset - a;
        // The paragraph itself was edited. Keep its place inside the changed range.
        return Math.Min(b, offset);
    }
}
