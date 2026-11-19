using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class StudySurface
{
    private const double DesignActionTarget = 72;
    private const double MinimumActionTarget = 44;
    private static readonly HomeworkReadingLayout[] ReaderLayouts =
        [HomeworkReadingLayout.OnePage, HomeworkReadingLayout.Fit, HomeworkReadingLayout.EachItem];

    public static readonly DependencyProperty ActionTargetProperty = DependencyProperty.Register(
        nameof(ActionTarget), typeof(double), typeof(StudySurface), new PropertyMetadata(DesignActionTarget));

    public double ActionTarget => (double)GetValue(ActionTargetProperty);

    private StudyHomeworkSnapshot? _readingSnapshot;
    private HomeworkReadingLayout _readingLayout = HomeworkReadingLayout.Fit;
    private IInputElement? _readerFocusBeforeCoverage;
    private bool ReaderOpen => _readingSnapshot is not null;
    private bool PickerOpen => HomeworkPicker.Visibility == Visibility.Visible;
    private bool ReaderCovered => StageNotice.Visibility == Visibility.Visible || SeatingOverlay.Visibility == Visibility.Visible;
    private bool ReaderAcceptsInput => ReaderOpen && !ReaderCovered && !_paused;
    private StudyHomeworkSnapshot? LatestReaderSnapshot
    {
        get
        {
            if (_timeline.State?.Homework?.SnapshotId is not { } id) return null;
            if (_pendingHomework is not null && id == _latestHomework) return _pendingHomework;
            return id == _loadedHomework ? _overviewHomework : null;
        }
    }

    private sealed record HomeworkChoice(string Id, string Subject, string Summary, string Sources);

    private void StudyViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double scale = Math.Min(e.NewSize.Width / 1920, e.NewSize.Height / 1080);
        if (scale > 0)
            SetValue(ActionTargetProperty, Math.Clamp(Math.Ceiling(MinimumActionTarget / scale), DesignActionTarget, 104));
    }

    private void OpenHomeworkReaderClicked(object sender, RoutedEventArgs e)
    {
        if (_paused || _busy || ReaderOpen || ReaderCovered || !OpenHomeworkReader.IsEnabled ||
            _overviewHomework is not { } snapshot || _pages[_pager.Page].Items.FirstOrDefault() is not { } first) return;

        CancelHold();
        _pager.Advance(false);
        _readingSnapshot = snapshot;
        StudyOverview.IsEnabled = false;
        StudyOverview.IsHitTestVisible = false;
        HomeworkReader.Visibility = Visibility.Visible;
        SelectReaderMode(_readingLayout);
        ReaderBody.Show(snapshot, _readingLayout, new HomeworkReadingPosition(first.Item.Id, first.StartOffset));
        UpdateReaderControls();
        ReturnFromReader.Focus();
    }

    private void ReturnFromReaderClicked(object sender, RoutedEventArgs e)
    {
        if (!ReaderAcceptsInput || PickerOpen) return;
        CloseReader(true);
        Render();
    }

    private void CloseReader(bool restoreFocus)
    {
        if (!ReaderOpen) return;
        _readingSnapshot = null;
        _readerFocusBeforeCoverage = null;
        HomeworkReader.Visibility = Visibility.Collapsed;
        ClosePicker(false);
        ReaderBody.Clear();
        ReaderInputShield.Visibility = Visibility.Collapsed;
        ReadingInput.IsEnabled = ReadingInput.IsHitTestVisible = true;
        StudyOverview.IsEnabled = StudyOverview.IsHitTestVisible = true;
        _pager.Advance(false);
        if (restoreFocus) OpenHomeworkReader.Focus();
    }

    private void ReaderCoverageChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!ReaderOpen) return;
        bool covered = ReaderCovered;
        bool changed = covered == ReadingInput.IsEnabled;
        if (changed)
        {
            if (covered && ReadingInput.IsKeyboardFocusWithin)
                _readerFocusBeforeCoverage = Keyboard.FocusedElement;
            ReadingInput.IsEnabled = ReadingInput.IsHitTestVisible = !covered;
            ReaderInputShield.Visibility = covered ? Visibility.Visible : Visibility.Collapsed;
        }
        if (covered)
        {
            if (SeatingOverlay.IsVisible && StageNotice.Visibility != Visibility.Visible && !SeatingOverlay.IsKeyboardFocusWithin)
                CloseSeating.Focus();
            return;
        }
        if (!changed) return;
        var previous = _readerFocusBeforeCoverage;
        _readerFocusBeforeCoverage = null;
        if (previous is null || !ReferenceEquals(Keyboard.Focus(previous), previous))
            (PickerOpen ? CloseHomeworkPicker : ReturnFromReader).Focus();
    }

    private void ReaderModeClicked(object sender, RoutedEventArgs e)
    {
        if (!ReaderAcceptsInput || PickerOpen || sender is not RadioButton mode) return;
        var layout = mode == ReaderOnePage ? HomeworkReadingLayout.OnePage :
            mode == ReaderEachItem ? HomeworkReadingLayout.EachItem : HomeworkReadingLayout.Fit;
        ChangeReaderMode(layout);
    }

    private void SelectReaderMode(HomeworkReadingLayout layout)
    {
        var selected = layout switch
        {
            HomeworkReadingLayout.OnePage => ReaderOnePage,
            HomeworkReadingLayout.EachItem => ReaderEachItem,
            _ => ReaderFit
        };
        selected.IsChecked = true;
        foreach (RadioButton button in ReaderModes.Children)
            button.IsTabStop = button == selected;
    }

    private void ChangeReaderMode(HomeworkReadingLayout layout)
    {
        if (_readingSnapshot is not { } snapshot || layout == _readingLayout) return;
        _readingLayout = layout;
        SelectReaderMode(layout);
        ReaderBody.Show(snapshot, layout);
        UpdateReaderControls();
    }

    private void ReaderUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (!ReaderAcceptsInput || PickerOpen || LatestReaderSnapshot is not { } snapshot ||
            ReferenceEquals(snapshot, _readingSnapshot)) return;

        _readingSnapshot = snapshot;
        ReaderBody.Show(snapshot, _readingLayout);
        UpdateReaderControls();
        ReturnFromReader.Focus();
    }

    private void ReaderPreviousClicked(object sender, RoutedEventArgs e) => ChangeReaderPage(-1);
    private void ReaderNextClicked(object sender, RoutedEventArgs e) => ChangeReaderPage(1);

    private void ChangeReaderPage(int direction)
    {
        if (!ReaderAcceptsInput || PickerOpen || ReaderBody.IsLayingOut || _readingLayout == HomeworkReadingLayout.OnePage) return;
        ReaderBody.ChangePage(direction);
    }

    private void UpdateReaderControls()
    {
        if (!ReaderOpen) return;
        bool loading = ReaderBody.IsLayingOut;
        bool eachItem = _readingLayout == HomeworkReadingLayout.EachItem;
        bool update = LatestReaderSnapshot is { } latest && !ReferenceEquals(latest, _readingSnapshot);
        bool cached = _timeline.State?.Reason == "OFFLINE_CONTINUING" || !_timeline.Fresh;
        int count = ReaderBody.PageCount;
        int page = ReaderBody.PageIndex;
        ReaderStatus.Text = cached ? "状态待同步 · 显示最近作业" :
            update ? "作业已更新" : !loading && ReaderBody.Position is null ? "今日暂无作业" :
            _readingLayout == HomeworkReadingLayout.OnePage && ReaderBody.IsDense ? "内容较密，建议切换合适布局" : "";
        ReaderUpdate.Visibility = update ? Visibility.Visible : Visibility.Collapsed;
        ReaderUpdate.IsEnabled = !loading;
        PickHomework.IsEnabled = !loading && count > 0;
        bool showPages = !loading && count > 0 && (eachItem || _readingLayout == HomeworkReadingLayout.Fit && count > 1);
        ReaderPageNavigation.Visibility = showPages ? Visibility.Visible : Visibility.Collapsed;
        ReaderPageLabel.Text = $"{page + 1} / {count}";
        ReaderPrevious.Content = eachItem ? "上一项" : "上一页";
        ReaderNext.Content = eachItem ? "下一项" : "下一页";
        ReaderPrevious.IsEnabled = page > 0;
        ReaderNext.IsEnabled = page + 1 < count;
    }

    private void PickHomeworkClicked(object sender, RoutedEventArgs e)
    {
        if (!ReaderAcceptsInput || PickerOpen || !PickHomework.IsEnabled || _readingSnapshot is not { } snapshot) return;
        HomeworkChoices.ItemsSource = snapshot.Items.Where(StudyHomeworkPages.IsDisplayable).Select(item => new HomeworkChoice(
            item.Id, item.Subject, HomeworkSummary(item), string.Join(" · ", item.Sources.Select(source => source.Name)))).ToArray();
        HomeworkChoices.SelectedItem = HomeworkChoices.Items.Cast<HomeworkChoice>().FirstOrDefault(item => item.Id == ReaderBody.Position?.ItemId);
        if (HomeworkChoices.SelectedIndex < 0 && HomeworkChoices.HasItems) HomeworkChoices.SelectedIndex = 0;
        HomeworkPicker.Visibility = Visibility.Visible;
        ReadingControls.IsEnabled = ReadingControls.IsHitTestVisible = false;
        if (HomeworkChoices.SelectedItem is { } current)
        {
            HomeworkChoices.ScrollIntoView(current);
            HomeworkChoices.UpdateLayout();
            if (HomeworkChoices.ItemContainerGenerator.ContainerFromItem(current) is ListBoxItem row && row.Focus()) return;
        }
        CloseHomeworkPicker.Focus();
    }

    private static string HomeworkSummary(StudyHomeworkItem item)
    {
        var text = (string.IsNullOrWhiteSpace(item.Title) ? item.Content : item.Title).AsSpan().TrimStart();
        int end = text.IndexOfAny('\r', '\n');
        if (end >= 0) text = text[..end];
        int length = 0;
        for (int i = 0; i < 64 && length < text.Length; i++)
            length += StringInfo.GetNextTextElementLength(text[length..]);
        return length < text.Length ? text[..length].ToString() + "…" : text.ToString();
    }

    private void CloseHomeworkPickerClicked(object sender, RoutedEventArgs e)
    {
        if (ReaderAcceptsInput && PickerOpen) ClosePicker(true);
    }

    private void ClosePicker(bool restoreFocus)
    {
        HomeworkPicker.Visibility = Visibility.Collapsed;
        HomeworkChoices.ItemsSource = null;
        ReadingControls.IsEnabled = ReadingControls.IsHitTestVisible = true;
        if (restoreFocus) PickHomework.Focus();
    }

    private void HomeworkChoiceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(HomeworkChoices, source) is ListBoxItem { Content: HomeworkChoice choice })
        {
            ChooseHomework(choice);
            e.Handled = true;
        }
    }

    private void ChooseHomework(HomeworkChoice choice)
    {
        if (!ReaderAcceptsInput || !PickerOpen) return;
        ReaderBody.SelectItem(choice.Id);
        ClosePicker(true);
    }

    private void StudyReaderKeyDown(object sender, KeyEventArgs e)
    {
        if (!ReaderAcceptsInput) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (e.IsRepeat) return;
            if (PickerOpen) ClosePicker(true);
            else ReturnFromReaderClicked(sender, e);
        }
        else if (PickerOpen && HomeworkChoices.IsKeyboardFocusWithin && e.Key is Key.Enter or Key.Space &&
            HomeworkChoices.SelectedItem is HomeworkChoice choice)
        {
            e.Handled = true;
            ChooseHomework(choice);
        }
        else if (ReaderModes.IsKeyboardFocusWithin && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            int direction = e.Key == Key.Right ? 1 : -1;
            int index = (Array.IndexOf(ReaderLayouts, _readingLayout) + ReaderLayouts.Length + direction) % ReaderLayouts.Length;
            ChangeReaderMode(ReaderLayouts[index]);
            ((RadioButton)ReaderModes.Children[index]).Focus();
        }
        else if (ReaderBody.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Left or Key.Right)
            {
                e.Handled = true;
                ChangeReaderPage(e.Key == Key.Right ? 1 : -1);
            }
            else if (_readingLayout == HomeworkReadingLayout.EachItem && e.Key is Key.PageUp or Key.PageDown)
            {
                e.Handled = true;
                if (!ReaderBody.IsLayingOut) ReaderBody.ScrollItem(e.Key == Key.PageDown ? 1 : -1);
            }
        }
    }
}
