using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public enum HomeworkReadingLayout { OnePage, Fit, EachItem }
public readonly record struct HomeworkReadingPosition(string ItemId, int Offset);

internal sealed class ReadingWork(StudyHomeworkItem item)
{
    internal StudyHomeworkItem Item { get; } = item;
    internal string Body { get; } = StudyHomeworkPages.ReadingText(item);
    internal string Sources { get; } = string.Join(" · ", item.Sources.Select(s => s.Name));
    private int[]? _elements;
    private int? _hardLines;
    internal int HardLines => _hardLines ??= CountHardLines(Body);

    private static int CountHardLines(string body)
    {
        int count = 0;
        for (int i = 0; i < body.Length; i++)
            if (body[i] == '\n' || body[i] == '\r' && (i + 1 == body.Length || body[i + 1] != '\n')) count++;
        return count + (body[^1] is '\n' or '\r' ? 0 : 1);
    }

    internal int BreakBefore(int start, int end)
    {
        if (end >= Body.Length) return Body.Length;
        _elements ??= StringInfo.ParseCombiningCharacters(Body);
        int index = Array.BinarySearch(_elements, end);
        if (index < 0) index = ~index - 1;
        if (_elements[index] <= start) return index + 1 < _elements.Length ? _elements[index + 1] : Body.Length;
        int earliest = start + (end - start) / 2;
        int sentence = -1;
        for (int i = index; i >= 0 && _elements[i] > earliest; i--)
        {
            int at = _elements[i];
            if (Body[at - 1] is '\n' or '\r') return at;
            if (sentence < 0 && Body[at - 1] is '。' or '！' or '？' or '；') sentence = at;
        }
        return sentence >= 0 ? sentence : _elements[index];
    }
}

internal readonly record struct ReadingSlice(ReadingWork Work, int Start, int End);
internal sealed record ReadingHeader(ReadingLines Subject, ReadingLines Sources, bool Inline, double Pad, double Gap, double Scale)
{
    internal double Height => (Inline ? Math.Max(Subject.Height, Sources.Height) : Subject.Height + Sources.Height) * Scale;
    internal double BodyTop => Pad + Height + Gap;
}
internal sealed record ReadingCard(ReadingSlice Slice, ReadingHeader Header, ReadingLines Body)
{
    internal double Height => Header.BodyTop + Body.Height + Header.Pad + 2;
}
internal sealed record ReadingCell(ReadingCard Card, Rect Bounds);
internal sealed record ReadingSheet(double Font, double Scale, IReadOnlyList<ReadingCell> Cells)
{
    internal BitmapSource? Preview { get; init; }
}
internal sealed record ReadingPlan(IReadOnlyList<ReadingSheet> Pages)
{
    internal int Locate(HomeworkReadingPosition target)
    {
        int last = -1;
        for (int i = 0; i < Pages.Count; i++)
            foreach (var cell in Pages[i].Cells)
            {
                var slice = cell.Card.Slice;
                if (slice.Work.Item.Id != target.ItemId) continue;
                if (target.Offset < slice.End) return i;
                last = i;
            }
        return Math.Max(0, last);
    }
}

// A planner belongs to a single request. In particular none of its text/measurement cache survives a scope change.
internal sealed partial class HomeworkReadingPlanner : IDisposable
{
    private const double Gap = 20;
    private readonly HomeworkReadingText _text;
    private readonly double _dpi;
    private readonly double _width, _height;
    private readonly CancellationToken _token;
    private readonly Dictionary<(ReadingWork, double, double, bool), ReadingHeader> _headers = [];
    private readonly Dictionary<(ReadingSlice, double, double), ReadingCard?> _cards = [];
    private readonly ReadingWork[] _works;

    internal HomeworkReadingPlanner(StudyHomeworkSnapshot snapshot, Size size, string family, double dpi, CancellationToken token)
    {
        _width = size.Width;
        _height = size.Height;
        _dpi = dpi;
        _text = new(family, dpi);
        _token = token;
        _works = snapshot.Items.Where(StudyHomeworkPages.IsDisplayable).Select(item => new ReadingWork(item)).ToArray();
    }

    internal ReadingPlan Build(HomeworkReadingLayout mode)
    {
        if (_works.Length == 0) return new([]);
        return mode switch
        {
            HomeworkReadingLayout.OnePage => new([HomeworkReadingPaper.PrepareOnePage(OnePage(), new(_width, _height), _text, _dpi, _token)]),
            HomeworkReadingLayout.EachItem => new(_works.Select(EachPage).ToArray()),
            _ => FitPages()
        };
    }
    private ReadingHeader Header(ReadingWork work, double font, double width, bool each = false)
    {
        var key = (work, font, width, each);
        if (_headers.TryGetValue(key, out var cached)) return cached;
        double pad = Math.Ceiling(font * (each ? .8 : .46)), gap = Math.Ceiling(font * .25);
        double inner = Math.Max(1, width - 2 * pad);
        var subject = _text.Measure(work.Item.Subject, 0, work.Item.Subject.Length, Math.Round(font * (each ? 1.16 : .84)),
            inner, FontWeights.SemiBold, int.MaxValue, _token);
        var sources = _text.Measure(work.Sources, 0, work.Sources.Length, Math.Round(font * .46),
            inner, FontWeights.Normal, int.MaxValue, _token);
        bool inline = !each && subject.Starts.Length == 1 && sources.Starts.Length == 1 && subject.Widest + gap * 1.6 + sources.Widest <= inner - 2;
        double natural = inline ? Math.Max(subject.Height, sources.Height) : subject.Height + sources.Height;
        // A large list of class names must still leave space for the body on a continuation or single-item page.
        double scale = Math.Min(1, _height * .32 / Math.Max(1, natural));
        return _headers[key] = new(subject, sources, inline, pad, gap, scale);
    }
    private ReadingLines MeasureBody(ReadingSlice slice, ReadingHeader header, double font, double width, double availableHeight)
    {
        int limit = (int)Math.Clamp(Math.Floor((availableHeight - header.BodyTop - header.Pad - 2) / Math.Ceiling(font * 1.34)), 0, int.MaxValue);
        return _text.Measure(slice.Work.Body, slice.Start, slice.End, font, Math.Max(1, width - 2 * header.Pad), FontWeights.Normal, limit, _token);
    }
    private ReadingCard? TryCard(ReadingSlice slice, double font, double width, double availableHeight)
    {
        var key = (slice, font, width);
        if (_cards.TryGetValue(key, out var cached)) return cached;
        _token.ThrowIfCancellationRequested();
        var header = Header(slice.Work, font, width);
        if (slice.Start == 0 && slice.End == slice.Work.Body.Length &&
            header.BodyTop + header.Pad + 2 + slice.Work.HardLines * Math.Ceiling(font * 1.34) > availableHeight)
            return _cards[key] = null;
        var lines = MeasureBody(slice, header, font, width, availableHeight);
        return _cards[key] = lines.End == slice.End ? new ReadingCard(slice, header, lines) : null;
    }
    private ReadingSheet EachPage(ReadingWork work)
    {
        _token.ThrowIfCancellationRequested();
        var slice = new ReadingSlice(work, 0, work.Body.Length);
        var header = Header(work, 60, _width, each: true);
        // Reserve room for the native scrollbar so adding it cannot change any line breaks.
        var lines = _text.Measure(work.Body, 0, work.Body.Length, 60, Math.Max(1, _width - 2 * header.Pad - 24),
            FontWeights.Normal, int.MaxValue, _token);
        return new(60, 1, [new(new(slice, header, lines), new Rect(0, 0, _width, _height))]);
    }
    public void Dispose() => _text.Dispose();
}
