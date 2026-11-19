using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public sealed class HomeworkReaderView : Grid
{
    private readonly record struct Metrics(Size Size, string Family, double Dpi);
    private readonly SemaphoreSlim _worker = new(1);
    private readonly Dictionary<HomeworkReadingLayout, ReadingPlan> _plans = [];
    private StudyHomeworkSnapshot? _snapshot;
    private HomeworkReadingLayout _mode;
    private Metrics _metrics;
    private CancellationTokenSource? _pending;
    private ReadingPlan? _plan;
    private HomeworkReadingPaper? _paper;
    private HomeworkReadingScroll? _scroll;
    public HomeworkReadingPosition? Position { get; private set; }
    public int PageIndex { get; private set; }
    public int PageCount => _plan?.Pages.Count ?? 0;
    public bool IsDense => _mode == HomeworkReadingLayout.OnePage && _plan?.Pages.FirstOrDefault() is { } sheet && sheet.Font * sheet.Scale < 36;
    public bool IsLayingOut { get; private set; }
    public event EventHandler? ReadingChanged;

    public HomeworkReaderView()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => RequestLayout();
        Loaded += (_, _) => RequestLayout();
        Unloaded += (_, _) => Clear();
    }
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (Focusable && IsEnabled && !IsKeyboardFocusWithin) Focus();
    }
    public void Show(StudyHomeworkSnapshot snapshot, HomeworkReadingLayout layout, HomeworkReadingPosition? position = null)
    {
        bool changed = !ReferenceEquals(_snapshot, snapshot);
        var anchor = HomeworkReadingPositionResolver.Resolve(_snapshot, snapshot, position ?? Position);
        if (changed || layout != _mode)
        {
            CancelPending(); _plan = null; PageIndex = 0;
            if (changed) _plans.Clear();
        }
        bool moved = Position != anchor;
        Position = anchor; _snapshot = snapshot; _mode = layout;
        if (_plan is not null && moved) GoToAnchor(); else RequestLayout();
    }
    public void Clear()
    {
        CancelPending();
        _plans.Clear(); _snapshot = null; _plan = null; Position = null; PageIndex = 0; IsLayingOut = false;
        EmptyView(); Notify();
    }
    private void CancelPending()
    {
        _pending?.Cancel();
        _pending = null;
    }
    private void EmptyView()
    {
        _scroll = null; _paper = null; Children.Clear();
    }
    private void Notify() => ReadingChanged?.Invoke(this, EventArgs.Empty);
    private void Message(string text)
    {
        EmptyView(); Children.Add(new TextBlock { Text = text, Foreground = HomeworkReadingText.SourceBrush, FontSize = 28,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center });
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi); RequestLayout();
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextElement.FontFamilyProperty && _snapshot is not null) RequestLayout();
    }
    private void RequestLayout()
    {
        if (_snapshot is null || ActualWidth < 1 || ActualHeight < 1) return;
        // The surface is normally 1920×1080 in design units. For a genuinely small host keep a usable design area.
        double scale = Math.Max(1, Math.Max(640 / ActualWidth, 410 / ActualHeight));
        var metrics = new Metrics(new(Math.Round(ActualWidth * scale, 2), Math.Round(ActualHeight * scale, 2)),
            TextElement.GetFontFamily(this).Source, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (_metrics != metrics)
        {
            CancelPending(); _plans.Clear(); _plan = null; _metrics = metrics;
        }
        if (_plan is not null || _pending is not null) return;
        if (_plans.TryGetValue(_mode, out _plan)) { IsLayingOut = false; GoToAnchor(); return; }
        _pending = new();
        IsLayingOut = true; Message("正在排版…");
        var pending = _pending;
        _ = PrepareAsync(_snapshot, _mode, metrics, pending);
        Notify();
    }
    private async Task PrepareAsync(StudyHomeworkSnapshot snapshot, HomeworkReadingLayout mode, Metrics metrics, CancellationTokenSource pending)
    {
        var token = pending.Token;
        try
        {
            await _worker.WaitAsync(token).ConfigureAwait(false);
            ReadingPlan plan;
            try
            {
                plan = await Task.Run(() =>
                {
                    using var planner = new HomeworkReadingPlanner(snapshot, metrics.Size, metrics.Family, metrics.Dpi, token);
                    return planner.Build(mode);
                }, token).ConfigureAwait(false);
            }
            finally { _worker.Release(); }
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_pending, pending)) return;
                _pending = null; IsLayingOut = false; _plans[mode] = _plan = plan;
                GoToAnchor();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) { }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.IO.FileFormatException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_pending, pending)) return;
                _pending = null; IsLayingOut = false; Message("本次排版未能完成，可切换布局或返回自习。"); Notify();
            });
        }
        finally { pending.Dispose(); }
    }
    public void SelectItem(string id)
    {
        if (_snapshot is null || !_snapshot.Items.Any(item => item.Id == id && StudyHomeworkPages.IsDisplayable(item))) return;
        Position = new(id, 0); GoToAnchor();
    }
    public void ChangePage(int delta)
    {
        if (_plan is null || PageCount == 0) return;
        int page = (int)Math.Clamp((long)PageIndex + delta, 0, PageCount - 1);
        if (page == PageIndex) return;
        PageIndex = page;
        var first = _plan.Pages[page].Cells[0].Card.Slice;
        Position = new(first.Work.Item.Id, first.Start); Paint();
    }
    public void ScrollItem(int direction)
    {
        if (direction < 0) _scroll?.PageUp(); else if (direction > 0) _scroll?.PageDown();
    }
    private void GoToAnchor()
    {
        if (_plan is null) return;
        PageIndex = Position is { } anchor ? _plan.Locate(anchor) : 0;
        Paint();
    }
    private void Paint()
    {
        EmptyView();
        if (_plan is null || PageCount == 0) { Notify(); return; }
        var sheet = _plan.Pages[PageIndex];
        var page = new Grid { Width = _metrics.Size.Width, Height = _metrics.Size.Height, ClipToBounds = true };
        _paper = new(sheet, _mode, _metrics.Family, _metrics.Dpi, slice =>
        {
            if (Position?.ItemId != slice.Work.Item.Id || Position.Value.Offset < slice.Start || Position.Value.Offset >= slice.End)
                Position = new(slice.Work.Item.Id, slice.Start);
            if (_paper is not null) { _paper.SelectedId = Position.Value.ItemId; _paper.InvalidateVisual(); }
            Notify();
        }) { SelectedId = Position?.ItemId };
        page.Children.Add(_paper);
        if (_mode == HomeworkReadingLayout.EachItem)
        {
            var card = sheet.Cells[0].Card;
            _scroll = new(card.Body, _metrics.Family, _metrics.Dpi, Position?.Offset ?? 0, offset =>
            {
                Position = new(card.Slice.Work.Item.Id, offset); Notify();
            });
            var viewer = new ScrollViewer { Content = _scroll, CanContentScroll = true, PanningMode = PanningMode.VerticalOnly,
                Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(card.Header.Pad, card.Header.BodyTop, card.Header.Pad, card.Header.Pad) };
            page.Children.Add(viewer);
        }
        Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = page });
        Notify();
    }
}
