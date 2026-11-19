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
    private StudyHomeworkSnapshot? _overviewHomework, _pendingHomework;
    private StudyHomeworkPage[]? _preparedHomeworkPages;
    private CancellationTokenSource? _overviewPreparation;
    private bool _overviewPreparationFailed;
    private string? _loadedHomework, _latestHomework;
    private (Guid? Display, Guid? ManualSession, string? Version, string? SourceVersion, DateTime? Date) _homeworkScope;
    private string? _homeworkStatus;
    private int _drawnPage = -1;
    private void InitializeHomework()
    {
        _pager = new(_time);
        Previous.Click += (_, _) => ChangeHomework(-1);
        Next.Click += (_, _) => ChangeHomework(1);
        ReaderBody.ReadingChanged += (_, _) => UpdateReaderControls();
    }
    private void ChangeHomework(int delta)
    {
        if (_paused || ReaderOpen || ReaderCovered) return;
        if (_overviewPreparation is null && _pendingHomework is { } pending) CommitHomework(pending);
        else _pager.Change(delta);
        _drawnPage = -1;
        Render();
    }
    public void ApplyHomework(string snapshotId, StudyHomeworkSnapshot snapshot)
    {
        if (_paused || _timeline.State is not { } state || snapshot.Version != (state.Manual is null?state.Version:state.Homework?.Version) || snapshot.Date != state.SchoolNow?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ||
            !HomeworkAuthorized(state) || state.Homework is not { State: "READY" } homework || homework.SnapshotId != snapshotId) return;
        if (_latestHomework == snapshotId) return;
        CancelOverviewPreparation();
        _latestHomework = snapshotId;
        _preparedHomeworkPages = null;
        _overviewPreparationFailed = false;
        if (_loadedHomework == snapshotId) { _pendingHomework = null; Render(); return; }
        _pendingHomework = snapshot;
        if (snapshot.Items.Count > 48 || snapshot.Items.Sum(item => item.Content.Length + item.Title.Length) > 8192)
        {
            _overviewPreparation = new();
            _drawnPage = -1;
            _ = PrepareOverviewAsync(snapshot, FontFamily.Source, _overviewPreparation);
        }
        else if (_loadedHomework is null || _pages.All(page => page.Items.Count == 0)) CommitHomework(snapshot);
        Render();
    }
    private void CancelOverviewPreparation()
    {
        if (_overviewPreparation is not { } pending) return;
        _overviewPreparation = null;
        pending.Cancel();
        _pendingHomework = null;
        _latestHomework = _loadedHomework;
        _drawnPage = -1;
    }
    private static StudyHomeworkPage[] BuildOverviewPages(StudyHomeworkSnapshot snapshot, string family, CancellationToken token = default)
    {
        var face = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var culture = CultureInfo.GetCultureInfo("zh-CN");
        return StudyHomeworkPages.Build(snapshot.Items, (text, width) =>
            new FormattedText(text, culture, FlowDirection.LeftToRight, face, 26, Brushes.White, 1) { MaxTextWidth = width }.Height, token);
    }
    private async Task PrepareOverviewAsync(StudyHomeworkSnapshot snapshot, string family, CancellationTokenSource pending)
    {
        var token = pending.Token;
        try
        {
            var pages = await Task.Run(() => BuildOverviewPages(snapshot, family, token), token).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_overviewPreparation, pending)) return;
                _overviewPreparation = null;
                _preparedHomeworkPages = pages;
                if (_loadedHomework is null || _pages.All(page => page.Items.Count == 0)) CommitHomework(snapshot);
                Render();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Dispatcher.HasShutdownStarted) { }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.FileFormatException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(_overviewPreparation, pending)) return;
                _overviewPreparation = null; _pendingHomework = null; _overviewPreparationFailed = true;
                _drawnPage = -1;
                Render();
            });
        }
        finally { pending.Dispose(); }
    }
    private void CommitHomework(StudyHomeworkSnapshot snapshot)
    {
        _pages = _preparedHomeworkPages ?? BuildOverviewPages(snapshot, FontFamily.Source);
        _preparedHomeworkPages = null; _pendingHomework = null;
        _overviewHomework = snapshot;
        _loadedHomework = _latestHomework; _pager.Reset(_pages.Length); _drawnPage = -1;
    }
    private static bool HomeworkAuthorized(StudyHostState state) =>
        state is { PolicyState: "APPLIED", Phase: "Preparing" or "Active" } && state.Settings?.Homework.Enabled != false;

    private void RenderHomework(StudyHostState state, bool fresh)
    {
        // A school source-policy change revokes old homework even while a
        // manual session retains its own independent version and reading timer.
        var scope = (Display: state.Display?.InstanceId, ManualSession: state.Manual?.SessionId, Version: state.Version,
            SourceVersion: state.Homework?.Version, Date: state.SchoolNow?.Date);
        string status = state.Settings?.Homework.Enabled == false ? "DISABLED" : state.Homework?.State ?? "WAITING";
        bool authorized = HomeworkAuthorized(state) && status == "READY";
        if (_homeworkScope != scope || !authorized && _latestHomework is not null)
        {
            if (_homeworkScope.Display != scope.Display || _homeworkScope.ManualSession != scope.ManualSession)
                _readingLayout = HomeworkReadingLayout.Fit;
            CloseReader(false);
            CancelOverviewPreparation();
            _preparedHomeworkPages = null; _overviewPreparationFailed = false;
            _homeworkScope = scope; _loadedHomework = _latestHomework = null; _pendingHomework = null;
            _overviewHomework = null;
            _pages = [new(false, [])]; _pager.Reset(1); _drawnPage = -1;
        }
        string visibleStatus = !fresh && status is "READY" or "WAITING" ? "STALE" : status;
        if (_homeworkStatus != visibleStatus && _pages[_pager.Page].Items.Count == 0) _drawnPage = -1;
        _homeworkStatus = visibleStatus;
        _pager.Configure(state.Settings?.Homework.RotationSeconds ?? 60);
        bool canAdopt = _overviewPreparation is null && _pendingHomework is not null;
        if (_pager.Advance(fresh && authorized && _loadedHomework is not null && !ReaderCovered && !ReaderOpen, canAdopt))
        { if (canAdopt) CommitHomework(_pendingHomework!); _drawnPage = -1; }
        bool usingCachedHomework = authorized && (!fresh || state.Reason == "OFFLINE_CONTINUING");
        WorkUpdateLabel.Text = usingCachedHomework && _loadedHomework is not null ? "状态待同步 · 显示最近作业" :
            _pendingHomework is not null ? "作业已更新 · 本页读完后切换" : "";
        Previous.Visibility = Next.Visibility = _pages.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Text = _pages.Length > 1 ? $"{_pager.Page + 1} / {_pages.Length}" : "";
        RotationLabel.Text = _pages.Length > 1 ? $"每 {_pager.IntervalSeconds} 秒轮换" : "";
        OpenHomeworkReader.IsEnabled = authorized && !ReaderCovered && _pages[_pager.Page].Items.Count > 0;
        UpdateReaderControls();
        if (_drawnPage == _pager.Page) return;
        _drawnPage = _pager.Page; HomeworkGrid.Children.Clear();
        var page = _pages[_pager.Page];
        HomeworkGrid.ColumnDefinitions.Clear();
        for (int i = 0; i < (page.Wide ? 2 : 3); i++) HomeworkGrid.ColumnDefinitions.Add(new());
        if (page.Items.Count == 0)
        {
            string message = visibleStatus switch
            {
                "DISABLED" => "学校未启用作业展示",
                "UNSUPPORTED" => "服务器尚未支持自习作业",
                "UNAVAILABLE" => "今日作业暂不可用",
                "STALE" => "作业待同步",
                _ when _overviewPreparation is not null => "正在排版今日作业…",
                _ when _overviewPreparationFailed => "今日作业暂时无法排版",
                _ => _loadedHomework is null ? "今日作业待同步" : "今日暂无作业"
            };
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
