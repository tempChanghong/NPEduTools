using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace NPEduTools.App;

public partial class OnboardingWindow
{
    private bool _returnToReview, _purposeLayoutInitialized, _compactPurposes;
    private sealed class PreparationRow(string step, string title, string status) : INotifyPropertyChanged
    {
        public string Step { get; } = step;
        public string Title { get; } = title;
        private string _status = status;
        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value; PropertyChanged?.Invoke(this, new(nameof(Status)));
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private PreparationRow[] _reviewRows = [];

    private void PageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PurposeGroups is null || StudyPurpose is null) return;
        bool compact = PageScroll.ActualWidth < 640;
        if (_purposeLayoutInitialized && compact == _compactPurposes) return;
        _purposeLayoutInitialized = true; _compactPurposes = compact;
        PurposeGroups.ColumnDefinitions[1].Width = compact ? new(0) : new(1, GridUnitType.Star);
        var groups = new[] { StudyPurpose, RecordingPurpose, ClassroomPurpose, ToolsPurpose };
        for (int i = 0; i < groups.Length; i++)
        {
            Grid.SetColumn(groups[i], compact ? 0 : i % 2);
            Grid.SetRow(groups[i], compact ? i : i / 2);
            groups[i].Margin = new(0, 0, !compact && i % 2 == 0 ? 12 : 0, 12);
        }
    }
    private void PreparationStatusChanged(object? sender, EventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke((Action)(() => PreparationStatusChanged(sender, e))); return; }
        if (_state.Step == "review") RefreshReviewItems();
    }
    private void RefreshReviewItems()
    {
        ReviewText.Text = (_state.Skipped ?? []).Length == 0 ? "逐项核对准备情况；需要调整时可直接返回对应步骤。" :
            $"有 {(_state.Skipped ?? []).Length} 项选择稍后配置；可以现在处理，也可以完成后继续。";
        var steps = _state.Steps.Where(s => s is not ("welcome" or "review")).ToArray();
        if (_reviewRows.Select(row => row.Step).SequenceEqual(steps))
        {
            // Live status changes must not remove the focused "go to step" button.
            foreach (var row in _reviewRows) row.Status = ReviewStatus(row.Step);
            return;
        }
        _reviewRows = steps.Select(s => new PreparationRow(s, Label(s), ReviewStatus(s))).ToArray();
        ReviewItems.ItemsSource = _reviewRows;
    }
    private void ReviewStepClicked(object sender, RoutedEventArgs e)
    {
        if (_state.Step != "review" || sender is not Button { Tag: string step } || !_state.Steps.Contains(step) || step is "welcome" or "review") return;
        if (Persist(_state with { Step = step, Completed = false, Deferred = false })) { _returnToReview = true; Render(); }
    }
    private OnboardingState AdvancePreparation(bool skip)
    {
        var next = CurrentChoices().Advance(skip);
        return _returnToReview ? next with { Step = "review", Completed = false } : next;
    }
}
