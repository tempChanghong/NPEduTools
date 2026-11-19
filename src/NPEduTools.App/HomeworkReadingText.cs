using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

namespace NPEduTools.App;

// Offsets are UTF-16 positions in the original string, including its line endings.
internal sealed record ReadingLines(string Text, int[] Starts, int End, double Width, double FontSize,
    double LineHeight, FontWeight Weight, double Widest)
{
    internal double Height => Starts.Length * LineHeight;
    internal int LineAt(int offset)
    {
        int found = Array.BinarySearch(Starts, offset);
        return Math.Max(0, found >= 0 ? found : ~found - 1);
    }
}

internal sealed class HomeworkReadingText(string family, double dpi) : IDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly TextFormatter _formatter = TextFormatter.Create(TextFormattingMode.Ideal);
    private readonly FontFamily _family = new(family);
    private readonly Dictionary<(double, FontWeight, Brush), Paragraph> _styles = [];
    internal static readonly Brush BodyBrush = MakeBrush(242, 245, 242);
    internal static readonly Brush HeadingBrush = MakeBrush(231, 244, 234);
    internal static readonly Brush SourceBrush = MakeBrush(177, 195, 183);

    internal static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class RunStyle(Typeface typeface, double size, Brush ink) : TextRunProperties
    {
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => size;
        public override double FontHintingEmSize => size;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => ink;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => Chinese;
        public override TextEffectCollection? TextEffects => null;
    }
    private sealed class Paragraph(RunStyle style) : TextParagraphProperties
    {
        public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
        public override TextAlignment TextAlignment => TextAlignment.Left;
        public override double LineHeight => 0;
        public override bool FirstLineInParagraph => true;
        public override TextRunProperties DefaultTextRunProperties => style;
        public override TextWrapping TextWrapping => TextWrapping.Wrap;
        public override TextMarkerProperties? TextMarkerProperties => null;
        public override double Indent => 0;
    }
    private sealed class Source(string text, int start, int end, TextRunProperties style) : TextSource
    {
        public override TextRun GetTextRun(int index) => index >= end
            ? new TextEndOfParagraph(1, style) : new TextCharacters(text, index, end - index, style);
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int index)
        {
            int length = Math.Clamp(index, start, end) - start;
            return new(length, new(Chinese, new CharacterBufferRange(text, start, length)));
        }
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private Paragraph Style(double size, FontWeight weight, Brush ink)
    {
        if (_styles.TryGetValue((size, weight, ink), out var value)) return value;
        var face = new Typeface(_family, FontStyles.Normal, weight, FontStretches.Normal);
        var run = new RunStyle(face, size, ink) { PixelsPerDip = dpi };
        return _styles[(size, weight, ink)] = new Paragraph(run);
    }

    internal ReadingLines Measure(string text, int start, int end, double size, double width, FontWeight weight,
        int limit, CancellationToken token)
    {
        width = Math.Clamp(width, 1, 1_000_000);
        var paragraph = Style(size, weight, BodyBrush);
        var source = new Source(text, start, end, paragraph.DefaultTextRunProperties) { PixelsPerDip = dpi };
        List<int> starts = [];
        double widest = 0;
        int offset = start;
        while (offset < end && starts.Count < limit)
        {
            token.ThrowIfCancellationRequested();
            starts.Add(offset);
            // Empty lines need no shaping and still consume a full line of reading space.
            if (text[offset] is '\n' or '\r')
            {
                offset += text[offset] == '\r' && offset + 1 < end && text[offset + 1] == '\n' ? 2 : 1;
                continue;
            }
            using var line = _formatter.FormatLine(source, offset, width, paragraph, null);
            widest = Math.Max(widest, line.WidthIncludingTrailingWhitespace);
            offset += Math.Min(end - offset, Math.Max(1, line.Length));
        }
        return new(text, starts.ToArray(), offset, width, size, Math.Ceiling(size * 1.34), weight, widest);
    }

    internal void Draw(DrawingContext dc, ReadingLines lines, Point origin, Brush ink, int first = 0, int last = int.MaxValue,
        CancellationToken token = default)
    {
        last = Math.Min(last, lines.Starts.Length);
        var paragraph = Style(lines.FontSize, lines.Weight, ink);
        for (int i = first; i < last; i++)
        {
            token.ThrowIfCancellationRequested();
            int start = lines.Starts[i], end = i + 1 < lines.Starts.Length ? lines.Starts[i + 1] : lines.End;
            if (end <= start || lines.Text[start] is '\r' or '\n') continue;
            var source = new Source(lines.Text, start, end, paragraph.DefaultTextRunProperties) { PixelsPerDip = dpi };
            using var line = _formatter.FormatLine(source, start, lines.Width, paragraph, null);
            double y = origin.Y + i * lines.LineHeight + (lines.LineHeight - line.Height) / 2;
            line.Draw(dc, new Point(origin.X, y), InvertAxes.None);
        }
    }
    public void Dispose() => _formatter.Dispose();
}
