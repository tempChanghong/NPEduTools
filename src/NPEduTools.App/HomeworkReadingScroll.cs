using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NPEduTools.App;

// The scrollbar represents the whole original item. Only the native text lines in its viewport are drawn.
internal sealed class HomeworkReadingScroll : FrameworkElement, IScrollInfo
{
    private readonly ReadingLines _lines;
    private readonly string _family;
    private readonly double _dpi;
    private readonly Action<int> _scrolled;
    private double _requested;
    internal HomeworkReadingScroll(ReadingLines lines, string family, double dpi, int anchor, Action<int> scrolled)
    {
        _lines = lines; _family = family; _dpi = dpi; _scrolled = scrolled;
        _requested = _lines.LineAt(anchor) * _lines.LineHeight;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "作业正文");
    }

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => ViewportWidth;
    public double ExtentHeight => _lines.Height;
    public double ViewportWidth { get; private set; }
    public double ViewportHeight { get; private set; }
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    protected override Size MeasureOverride(Size available) => new(
        double.IsFinite(available.Width) ? available.Width : _lines.Width,
        double.IsFinite(available.Height) ? Math.Min(available.Height, ExtentHeight) : ExtentHeight);
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width != ViewportWidth || finalSize.Height != ViewportHeight)
        {
            ViewportWidth = finalSize.Width; ViewportHeight = finalSize.Height;
            VerticalOffset = Math.Clamp(_requested, 0, Math.Max(0, ExtentHeight - ViewportHeight));
            ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual();
        }
        return finalSize;
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        int first = (int)Math.Floor(VerticalOffset / _lines.LineHeight);
        int last = (int)Math.Ceiling((VerticalOffset + ViewportHeight) / _lines.LineHeight);
        using var text = new HomeworkReadingText(_family, _dpi);
        text.Draw(dc, _lines, new(0, -VerticalOffset), HomeworkReadingText.BodyBrush, first, last);
    }
    public void SetVerticalOffset(double offset)
    {
        if (double.IsNaN(offset)) return;
        double value = Math.Clamp(offset, 0, Math.Max(0, ExtentHeight - ViewportHeight));
        if (value == VerticalOffset) return;
        VerticalOffset = _requested = value;
        ScrollOwner?.InvalidateScrollInfo(); InvalidateVisual();
        if (_lines.Starts.Length > 0)
        {
            int line = Math.Min(_lines.Starts.Length - 1, (int)(value / _lines.LineHeight));
            _scrolled(_lines.Starts[line]);
        }
    }
    public void LineUp() => SetVerticalOffset(VerticalOffset - _lines.LineHeight);
    public void LineDown() => SetVerticalOffset(VerticalOffset + _lines.LineHeight);
    private double WheelDistance => SystemParameters.WheelScrollLines < 0
        ? Math.Max(_lines.LineHeight, ViewportHeight - _lines.LineHeight)
        : SystemParameters.WheelScrollLines * _lines.LineHeight;
    public void MouseWheelUp() => SetVerticalOffset(VerticalOffset - WheelDistance);
    public void MouseWheelDown() => SetVerticalOffset(VerticalOffset + WheelDistance);
    public void PageUp() => SetVerticalOffset(VerticalOffset - Math.Max(_lines.LineHeight, ViewportHeight - _lines.LineHeight));
    public void PageDown() => SetVerticalOffset(VerticalOffset + Math.Max(_lines.LineHeight, ViewportHeight - _lines.LineHeight));
    public void SetHorizontalOffset(double offset) { }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
