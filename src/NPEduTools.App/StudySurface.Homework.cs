using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class StudySurface
{
    private StudyHomeworkPager _pager = null!;
    private StudyHomeworkPage[] _pages = [new(false, [])];
    private StudyHomeworkSnapshot? _pendingHomework;
    private string? _loadedHomework, _latestHomework, _homeworkScope;
    private string? _homeworkStatus;
    private int _drawnPage = -1;
    private void InitializeHomework()
    {
        _pager = new(_time);
        Previous.Click += (_, _) => ChangeHomework(-1);
        Next.Click += (_, _) => ChangeHomework(1);
    }
    private void ChangeHomework(int delta)
    { if (_pendingHomework is { } pending) CommitHomework(pending); else _pager.Change(delta); _drawnPage = -1; Render(); }
    public void ApplyHomework(string snapshotId, StudyHomeworkSnapshot snapshot)
    {
        if (_timeline.State is not { } state || snapshot.Version != (state.Manual is null?state.Version:state.Homework?.Version) || snapshot.Date != state.SchoolNow?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
            state.Homework is not { State: "READY" } homework || homework.SnapshotId != snapshotId) return;
        if (_latestHomework == snapshotId) return;
        _latestHomework = snapshotId;
        if (_loadedHomework == snapshotId) { _pendingHomework = null; Render(); return; }
        if (_loadedHomework is null || _pages.All(page => page.Items.Count == 0)) CommitHomework(snapshot);
        else _pendingHomework = snapshot;
        Render();
    }
    private double HomeworkHeight(string text, double width)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 26, Brushes.White, 1)
        { MaxTextWidth = width };
        return formatted.Height;
    }
    private void CommitHomework(StudyHomeworkSnapshot snapshot)
    {
        _pages = StudyHomeworkPages.Build(snapshot.Items, HomeworkHeight); _pendingHomework = null;
        _loadedHomework = _latestHomework; _pager.Reset(_pages.Length); _drawnPage = -1;
    }
    private void RenderHomework(StudyHostState state, bool fresh)
    {
        string scope = $"{state.Display?.InstanceId}/{state.Version}/{state.SchoolNow:yyyy-MM-dd}";
        bool available = fresh && state.Homework is { State: "READY" };
        string status = !fresh ? "STALE" : state.Homework?.State ?? "WAITING";
        if (_homeworkScope != scope || fresh && !available && _homeworkStatus != status)
        {
            _homeworkScope = scope; _loadedHomework = _latestHomework = null; _pendingHomework = null;
            _pages = [new(false, [])]; _pager.Reset(1); _drawnPage = -1;
        }
        _homeworkStatus = status;
        _pager.Configure(state.Settings?.Homework.RotationSeconds ?? 60);
        if (_pager.Advance(available && _loadedHomework is not null && _shownNotice is null && SeatingOverlay.Visibility != Visibility.Visible, _pendingHomework is not null))
        { if (_pendingHomework is { } pending) CommitHomework(pending); _drawnPage = -1; }
        WorkUpdateLabel.Text = !fresh && _loadedHomework is not null ? "状态待同步 · 显示最近作业" :
            _pendingHomework is not null ? "作业已更新 · 本页读完后切换" : "";
        Previous.Visibility = Next.Visibility = _pages.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Text = _pages.Length > 1 ? $"{_pager.Page + 1} / {_pages.Length}" : "";
        RotationLabel.Text = _pages.Length > 1 ? $"每 {_pager.IntervalSeconds} 秒轮换" : "";
        if (_drawnPage == _pager.Page) return;
        _drawnPage = _pager.Page; HomeworkGrid.Children.Clear();
        var page = _pages[_pager.Page];
        HomeworkGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < (page.Wide ? 2 : 3); i++) HomeworkGrid.ColumnDefinitions.Add(new());
        if (page.Items.Count == 0)
        {
            string message = !fresh ? "作业待同步" : state.Homework?.State switch { "DISABLED" => "学校未启用作业展示", "UNSUPPORTED" => "服务器尚未支持自习作业", "UNAVAILABLE" => "今日作业暂不可用", _ => _loadedHomework is null ? "今日作业待同步" : "今日暂无作业" };
            var label = new TextBlock { Text = message, FontSize = 26, Foreground = Muted, Margin = new(0, 16, 0, 0) };
            Grid.SetColumnSpan(label, page.Wide ? 2 : 3); HomeworkGrid.Children.Add(label); return;
        }
        for (int i = 0; i < page.Items.Count; i++)
        {
            var part = page.Items[i];
            var stack = new StackPanel { Margin = new(0, 0, 32, 10) };
            stack.Children.Add(new TextBlock { Text = part.Item.Subject + (part.Parts > 1 ? $" · {part.Part}/{part.Parts}" : ""), FontSize = 25,
                FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            string sources = string.Join(" · ", part.Item.Sources.Select(source => source.Name));
            stack.Children.Add(new TextBlock { Text = sources, ToolTip = sources, FontSize = 15, Foreground = Muted,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 3, 0, 7) });
            stack.Children.Add(new TextBlock { Text = part.Text, FontSize = 26, FontWeight = FontWeights.Normal, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, Width = page.Wide ? StudyHomeworkPages.WideWidth : StudyHomeworkPages.CompactWidth });
            Grid.SetColumn(stack, page.Wide ? i : i % 3); Grid.SetRow(stack, page.Wide || page.Tall ? 0 : i / 3);
            if (page.Wide || page.Tall) Grid.SetRowSpan(stack, 2);
            HomeworkGrid.Children.Add(stack);
        }
    }
}
