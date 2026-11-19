using System.Windows;

namespace NPEduTools.App;

internal sealed partial class HomeworkReadingPlanner
{
    private static readonly int[][][] Grids = [[], [[1]], [[2], [1, 1]], [[1, 2], [2, 1], [3]],
        [[2, 2]], [[2, 3], [3, 2]], [[3, 3]]];
    private static readonly int[] Fonts = [60, 54, 48];
    private readonly record struct Unit(ReadingSlice Slice, bool Continued, bool Exclusive);
    private readonly record struct Cost(int Pages, int FloorItems, int TotalFont, double Uneven)
    {
        internal bool BetterThan(Cost other)
        {
            if (Pages != other.Pages) return Pages < other.Pages;
            if (FloorItems != other.FloorItems) return FloorItems < other.FloorItems;
            if (TotalFont != other.TotalFont) return TotalFont > other.TotalFont;
            return Uneven < other.Uneven;
        }
    }

    private List<Unit> FitUnits()
    {
        List<Unit> units = [];
        foreach (var work in _works)
        {
            var whole = new ReadingSlice(work, 0, work.Body.Length);
            if (TryCard(whole, 48, _width, _height) is not null)
            {
                units.Add(new(whole, false, false));
                continue;
            }
            var header = Header(work, 54, _width);
            int start = 0;
            while (start < whole.End)
            {
                var remaining = whole with { Start = start };
                var lines = MeasureBody(remaining, header, 54, _width, _height);
                int end = work.BreakBefore(start, lines.End);
                units.Add(new(remaining with { End = end }, true, end < whole.End));
                start = end;
            }
        }
        return units;
    }
    private ReadingPlan FitPages()
    {
        var units = FitUnits();
        var suffix = new Cost[units.Count + 1];
        var choices = new ReadingSheet[units.Count];
        for (int i = units.Count - 1; i >= 0; i--)
        {
            _token.ThrowIfCancellationRequested();
            Cost? best = null;
            for (int count = 1; count <= 6 && i + count <= units.Count; count++)
            {
                if (count > 1 && (units[i].Exclusive || units[i + count - 1].Continued)) break;
                foreach (var grid in Grids[count])
                {
                    foreach (int font in Fonts)
                    {
                        if (units[i].Continued && (font > 54 || units[i].Exclusive && font != 54)) continue;
                        var sheet = FitGrid(units, i, grid, font, out double imbalance);
                        if (sheet is null) continue;
                        var tail = suffix[i + count];
                        var score = new Cost(tail.Pages + 1, tail.FloorItems + (font == 48 ? count : 0),
                            tail.TotalFont + font * count, tail.Uneven + imbalance);
                        if (best is null || score.BetterThan(best.Value))
                        {
                            best = score;
                            choices[i] = sheet;
                        }
                        break;
                    }
                }
            }
            suffix[i] = best ?? throw new InvalidOperationException("Reading area cannot fit a text element.");
        }
        List<ReadingSheet> pages = [];
        for (int i = 0; i < choices.Length; i += choices[i].Cells.Count) pages.Add(choices[i]);
        return new(pages);
    }
    private ReadingSheet? FitGrid(List<Unit> units, int start, int[] rows, int font, out double imbalance)
    {
        imbalance = 0;
        var cards = new ReadingCard[rows.Sum()];
        var heights = new double[rows.Length];
        int index = 0;
        for (int row = 0; row < rows.Length; row++)
        {
            double width = (_width - Gap * (rows[row] - 1)) / rows[row];
            for (int col = 0; col < rows[row]; col++, index++)
            {
                var card = TryCard(units[start + index].Slice, font, width, _height);
                if (card is null) return null;
                cards[index] = card;
                heights[row] = Math.Max(heights[row], card.Height);
            }
        }
        double extra = (_height - Gap * (rows.Length - 1) - heights.Sum()) / rows.Length;
        if (extra < 0) return null;
        var cells = new ReadingCell[cards.Length];
        double y = 0, least = 1, most = 0;
        index = 0;
        for (int row = 0; row < rows.Length; row++)
        {
            double height = heights[row] + extra, width = (_width - Gap * (rows[row] - 1)) / rows[row];
            for (int col = 0; col < rows[row]; col++, index++)
            {
                cells[index] = new(cards[index], new Rect(col * (width + Gap), y, width, height));
                double fill = cards[index].Height / height;
                least = Math.Min(least, fill); most = Math.Max(most, fill);
            }
            y += height + Gap;
        }
        imbalance = most - least;
        return new(font, 1, cells);
    }
}
