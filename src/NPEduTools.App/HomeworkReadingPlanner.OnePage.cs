using System.Windows;

namespace NPEduTools.App;

internal sealed partial class HomeworkReadingPlanner
{
    private ReadingSheet OnePage()
    {
        if (TryOnePage(60, 1) is { } largest) return largest;
        ReadingSheet? best = null;
        int low = 8, high = 29;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (TryOnePage(mid * 2, 1) is { } page) { best = page; low = mid + 1; }
            else high = mid - 1;
        }
        if (best is not null) return best;

        // Keep native font metrics valid at extreme densities. Only the content sheet scales.
        double upper = 1, lower = .5;
        while ((best = TryOnePage(16, lower)) is null) { upper = lower; lower *= .5; }
        for (int i = 0; i < 5; i++)
        {
            double mid = (lower + upper) / 2;
            if (TryOnePage(16, mid) is { } page) { best = page; lower = mid; }
            else upper = mid;
        }
        return best;
    }

    private ReadingSheet? TryOnePage(int font, double scale)
    {
        _cards.Clear();
        _headers.Clear();
        double width = Math.Min(1_000_000, _width / scale), height = _height / scale, gap = Math.Ceiling(font * .49);
        int count = _works.Length;
        double bestHeight = double.PositiveInfinity;
        List<ReadingCell>? best = null;
        ReadingCard?[] Column(double colWidth) =>
            _works.Select(work => TryCard(new(work, 0, work.Body.Length), font, colWidth, height)).ToArray();

        void Offer(IReadOnlyList<(ReadingCard?[] Cards, double Width, int Start, int End)> columns, double used)
        {
            if (used >= bestHeight || used > height) return;
            bestHeight = used; best = new(count);
            double x = 0;
            foreach (var column in columns)
            {
                double y = 0;
                for (int i = column.Start; i < column.End; i++)
                {
                    var card = column.Cards[i]!;
                    best.Add(new(card, new Rect(x, y, column.Width, card.Height)));
                    y += card.Height + gap;
                }
                x += column.Width + gap;
            }
        }
        for (int n = 1; n <= Math.Min(4, count); n++)
        {
            double colWidth = (width - gap * (n - 1)) / n;
            var cards = Column(colWidth);
            if (cards.Any(card => card is null)) continue;
            double tall = cards.Max(card => card!.Height);
            int ColumnsNeeded(double cap)
            {
                int columns = 1;
                double current = 0;
                foreach (var card in cards)
                {
                    if (current > 0 && current + gap + card!.Height > cap) { columns++; current = 0; }
                    current += (current > 0 ? gap : 0) + card!.Height;
                }
                return columns;
            }
            if (ColumnsNeeded(height) > n) continue;
            double upper = height;
            for (int pass = 0; pass < 18; pass++)
            {
                double mid = (tall + upper) / 2;
                if (ColumnsNeeded(mid) <= n) upper = mid; else tall = mid;
            }
            List<(ReadingCard?[], double, int, int)> groups = [];
            double current = 0, used = 0;
            int first = 0;
            for (int i = 0; i < count; i++)
            {
                if (i > first && current + gap + cards[i]!.Height > upper)
                {
                    groups.Add((cards, colWidth, first, i));
                    used = Math.Max(used, current); first = i; current = 0;
                }
                current += (i > first ? gap : 0) + cards[i]!.Height;
            }
            groups.Add((cards, colWidth, first, count));
            Offer(groups, Math.Max(used, current));
        }
        if (count > 1)
        {
            foreach (double share in new[] { .35, .4, .45, .55, .6, .65 })
            {
                double leftWidth = (width - gap) * share, rightWidth = width - gap - leftWidth;
                var left = Column(leftWidth); var right = Column(rightWidth);
                var suffix = new double[count + 1];
                for (int i = count - 1; i >= 0; i--)
                    suffix[i] = (right[i]?.Height ?? double.PositiveInfinity) + (i + 1 < count ? gap : 0) + suffix[i + 1];
                double prefix = 0, least = bestHeight;
                int split = 0;
                for (int i = 1; i < count; i++)
                {
                    prefix += (left[i - 1]?.Height ?? double.PositiveInfinity) + (i > 1 ? gap : 0);
                    if (prefix > height) break;
                    double used = Math.Max(prefix, suffix[i]);
                    if (used < least && used <= height) { split = i; least = used; }
                }
                if (split > 0) Offer([(left, leftWidth, 0, split), (right, rightWidth, split, count)], least);
            }
        }
        return best is null ? null : new(font, scale, best);
    }
}
