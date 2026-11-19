using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NPEduTools.App;

internal sealed class HomeworkReadingPaper : FrameworkElement
{
    private static readonly Brush CardBrush = HomeworkReadingText.MakeBrush(24, 34, 28);
    private static readonly Pen Edge = MakePen(53, 69, 58, 1.5);
    private static readonly Pen SelectedEdge = MakePen(142, 202, 164, 2.5);
    private readonly ReadingSheet _sheet;
    private readonly HomeworkReadingLayout _mode;
    private readonly string _family;
    private readonly double _dpi;
    private readonly Action<ReadingSlice> _select;
    internal string? SelectedId { get; set; }

    internal HomeworkReadingPaper(ReadingSheet sheet, HomeworkReadingLayout mode, string family, double dpi, Action<ReadingSlice> select)
    {
        _sheet = sheet; _mode = mode; _family = family; _dpi = dpi; _select = select;
        ClipToBounds = true;
        AutomationProperties.SetName(this, string.Join("、", sheet.Cells.Select(c => c.Card.Slice.Work.Item.Subject)));
    }
    private static Pen MakePen(byte r, byte g, byte b, double thickness)
    {
        var pen = new Pen(HomeworkReadingText.MakeBrush(r, g, b), thickness);
        pen.Freeze(); return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_sheet.Preview is { } image)
            dc.DrawImage(image, new Rect(RenderSize));
        dc.PushTransform(new ScaleTransform(_sheet.Scale, _sheet.Scale));
        using var text = _sheet.Preview is null ? new HomeworkReadingText(_family, _dpi) : null;
        foreach (var cell in _sheet.Cells)
        {
            var bounds = cell.Bounds;
            var card = cell.Card; var head = card.Header;
            if (_mode != HomeworkReadingLayout.EachItem)
            {
                bool selected = card.Slice.Work.Item.Id == SelectedId;
                if (text is not null || selected)
                    Plate(dc, bounds, _sheet.Font, selected ? SelectedEdge : Edge, text is not null ? CardBrush : null);
            }
            if (text is null) continue;
            var origin = new Point(bounds.X + head.Pad, bounds.Y + head.Pad);
            dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
            dc.PushTransform(new ScaleTransform(head.Scale, head.Scale));
            text.Draw(dc, head.Subject, new(), HomeworkReadingText.HeadingBrush);
            text.Draw(dc, head.Sources, SourceOrigin(head), HomeworkReadingText.SourceBrush);
            dc.Pop(); dc.Pop();
            if (_mode != HomeworkReadingLayout.EachItem)
                text.Draw(dc, card.Body, new(bounds.X + head.Pad, bounds.Y + head.BodyTop), HomeworkReadingText.BodyBrush);
        }
        dc.Pop();
    }

    private static Point SourceOrigin(ReadingHeader head) => head.Inline
        ? new(head.Subject.Widest + head.Gap * 1.6, (head.Subject.Height - head.Sources.Height) / 2)
        : new(0, head.Subject.Height);

    private static void Plate(DrawingContext context, Rect bounds, double font, Pen edge, Brush? fill)
    {
        bounds.Inflate(-edge.Thickness / 2, -edge.Thickness / 2);
        double radius = Math.Min(15, font * .32);
        context.DrawRoundedRectangle(fill, edge, bounds, radius, radius);
    }

    // A dense overview can contain the whole 2 MiB contract. Rasterize it once on the layout worker.
    // Rendering in small batches bounds the native glyph resources and gives cancellation a point between batches.
    internal static ReadingSheet PrepareOnePage(ReadingSheet sheet, Size viewport, HomeworkReadingText text, double dpi, CancellationToken token)
    {
        int lines = 0, characters = 0;
        foreach (var cell in sheet.Cells)
        {
            var card = cell.Card;
            lines += card.Body.Starts.Length + card.Header.Subject.Starts.Length + card.Header.Sources.Starts.Length;
            characters += card.Body.Text.Length + card.Header.Subject.Text.Length + card.Header.Sources.Text.Length;
        }
        if (lines <= 400 && characters <= 16_384) return sheet;
        double resolution = Math.Min(Math.Max(1, dpi), Math.Min(2560 / viewport.Width, 1600 / viewport.Height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(viewport.Width * resolution), (int)Math.Ceiling(viewport.Height * resolution),
            96 * resolution, 96 * resolution, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(sheet.Scale, sheet.Scale));
            foreach (var cell in sheet.Cells) Plate(dc, cell.Bounds, sheet.Font, Edge, CardBrush);
            dc.Pop();
        }
        bitmap.Render(visual);
        DrawingContext? batch = null;
        int lineCount = 0, charCount = 0;
        try
        {
            foreach (var cell in sheet.Cells)
            {
                var head = cell.Card.Header;
                var origin = new Point(cell.Bounds.X + head.Pad, cell.Bounds.Y + head.Pad);
                DrawLines(head.Subject, origin, head.Scale, new(), HomeworkReadingText.HeadingBrush);
                DrawLines(head.Sources, origin, head.Scale, SourceOrigin(head), HomeworkReadingText.SourceBrush);
                DrawLines(cell.Card.Body, new(cell.Bounds.X + head.Pad, cell.Bounds.Y + head.BodyTop), 1, new(), HomeworkReadingText.BodyBrush);
            }
            Flush();
        }
        finally { batch?.Close(); }
        bitmap.Freeze();
        return sheet with { Preview = bitmap };

        void Flush()
        {
            if (batch is null) return;
            batch.Pop();
            batch.Close();
            batch = null;
            bitmap.Render(visual);
            lineCount = charCount = 0;
        }

        void DrawLines(ReadingLines run, Point origin, double scale, Point inset, Brush brush)
        {
            for (int first = 0; first < run.Starts.Length;)
            {
                token.ThrowIfCancellationRequested();
                if (batch is null)
                {
                    batch = visual.RenderOpen();
                    batch.PushTransform(new ScaleTransform(sheet.Scale, sheet.Scale));
                }
                int end = first + 1;
                while (end < run.Starts.Length && lineCount + end - first < 512 &&
                    charCount + run.Starts[end] - run.Starts[first] < 16_384) end++;
                batch.PushTransform(new TranslateTransform(origin.X, origin.Y));
                batch.PushTransform(new ScaleTransform(scale, scale));
                text.Draw(batch, run, inset, brush, first, end, token);
                batch.Pop(); batch.Pop();
                lineCount += end - first;
                charCount += (end < run.Starts.Length ? run.Starts[end] : run.End) - run.Starts[first];
                if (lineCount >= 512 || charCount >= 16_384) Flush();
                first = end;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsEnabled || _mode == HomeworkReadingLayout.EachItem) return;
        var pos = e.GetPosition(this);
        var cell = _sheet.Cells.FirstOrDefault(c => c.Bounds.Contains(new Point(pos.X / _sheet.Scale, pos.Y / _sheet.Scale)));
        if (cell is null) return;
        _select(cell.Card.Slice);
        e.Handled = true;
    }
}
