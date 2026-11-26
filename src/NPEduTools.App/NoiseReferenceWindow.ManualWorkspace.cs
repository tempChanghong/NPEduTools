using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class NoiseReferenceWindow
{
    private sealed record ReadingEdit(NoiseReferenceSample Sample, string Value, string Minimum, string Maximum, bool SameWindow);
    private readonly Dictionary<Guid, ReadingEdit> _manualReadings = [];
    private Guid? _manualReadingDraft;
    private NoiseReferenceSample? _manualInputSample;
    private bool _manualWorkspace, _manualVisible, _renderingManualRows, _renderingManualReading;

    internal void EnableManualWorkspace()
    {
        _manualWorkspace = true;
        ReferenceRoot.Tag = "ManualWorkspace";
        Heading.Visibility = Visibility.Collapsed;
        DeviceStatus.TextWrapping = TextWrapping.NoWrap;
        DeviceStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(ReferenceHeadingActions, Dock.Right);
        SetupStep.Content = "准备";
        SamplingStep.Content = "采样与读数";
        ResultStep.Content = "复核与保存";
        StepNavigation.Visibility = ManualSegmentSummary.Visibility = ManualSegmentTarget.Visibility = Visibility.Visible;
        AdvancedSettings.Header = "仪表和检查门槛（必填）";
        AdvancedSettings.IsExpanded = true;
        LegacySampleDetailsBody.Children.Remove(ReferenceSampleReading);
        ReferenceSampleLayout.Children.Add(ReferenceSampleReading);
        ReferenceSampleProgress.Children.Remove(SampleDetails);
        ReferenceSampleStage.Children.Add(SampleDetails);
        SampleDetails.Header = "采样记录与删除操作";
        SampleDetails.SetResourceReference(StyleProperty, "TeacherExpander");
        SampleDetails.IsExpanded = OptionalRangeInputs.IsExpanded = false;
        SampleList.Height = 80;
        OptionalRangeInputs.ClearValue(TemplateProperty);
        ManualReferenceInputs.Children.Remove(OptionalRangeInputs);
        ReferenceReadingBody.Children.Insert(ReferenceReadingBody.Children.IndexOf(SameWindow) + 1, OptionalRangeInputs);
        OptionalRangeInputs.Header = "可选：同期最低 / 最高（成对填写）";
        ManualReferenceInputs.Orientation = Orientation.Vertical;
        ReferenceReadingLabel.Text = "同期稳定读数（30–130 dB(A)）";
        ReferenceInstructions.Visibility = Visibility.Collapsed;
        ReadingActions.Children.Remove(AttachReference);
        ReadingActions.Visibility = Visibility.Collapsed;
        ReferenceFooterActions.Children.Add(AttachReference);
        AttachReference.SetResourceReference(StyleProperty, "TeacherPrimary");
        AttachReference.Content = "保存本段读数";
        RoleChoice.SelectionChanged += (_, _) =>
        {
            if (!_renderingManualRows) SelectManualRole();
        };
        ManagementSecret.PasswordChanged += (_, _) => UpdateButtons();
        ReferenceValue.TextChanged += (_, _) => ManualReadingEdited();
        ReferenceMinimum.TextChanged += (_, _) => ManualReadingEdited();
        ReferenceMaximum.TextChanged += (_, _) => ManualReadingEdited();
        SameWindow.Checked += (_, _) => ManualReadingEdited();
        SameWindow.Unchecked += (_, _) => ManualReadingEdited();
        UpdateButtons();
    }

    internal void ManualWorkspaceVisibilityChanged(bool visible)
    {
        if (!_manualWorkspace || _manualVisible == visible || _lifetime.IsCancellationRequested) return;
        _manualVisible = visible;
        if (!visible)
        {
            RememberManualReading();
            _timer.Stop();
            _connected = false;
            ManagementSecret.Clear();
            // Leaving withdraws an old read. A command already sent may still settle.
            if (!_busy || _verifying) { _order.Advance(); _reading = false; }
            UpdateButtons();
            return;
        }
        _timer.Start();
        _ = ReadAsync();
    }

    private void ReferenceWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_manualWorkspace) return;
        bool wide = e.NewSize.Width >= 750 && WorkspacePanel.DialogOwner(this).ActualWidth >= 930;
        ReferenceSampleLayout.ColumnDefinitions[1].Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ReferenceSampleReading, wide ? 0 : 1);
        Grid.SetColumn(ReferenceSampleReading, wide ? 1 : 0);
        ReferenceSampleReading.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }

    private static NoiseReferenceDraft? ManualDraft(NoiseReferenceState? state) =>
        state?.Draft is { MeterDevicePath: null, Workflow: "Stable" } draft &&
        draft.DeviceId == state.DeviceId ? draft : null;

    private static bool ManualReviewReady(NoiseReferenceDraft? draft) =>
        draft is not null && HasReading(draft, "Primary") && HasReading(draft, "Check");

    private static bool HasReading(NoiseReferenceDraft draft, string role) =>
        draft.Samples.Any(s => s.Role == role && s is { Quality: "Good", SameWindowConfirmed: true, ReferenceDbA: not null });

    private void RenderManualSegments(NoiseReferenceState? previous, NoiseReferenceState state, bool changed)
    {
        var draft = ManualDraft(state);
        if (draft is null)
        {
            _step = 1;
            if (state.Draft is not null)
                DraftStatus.Text = "原草稿属于 USB 联机流程或另一支麦克风。若新建手动草稿，会先请你确认替换。";
        }
        else if (state.State == "Sampling" && previous?.State != "Sampling") _step = 2;
        else if (_manualReadingDraft is not null && _manualReadingDraft != draft.Id) _step = 1;
        else if (_step == 3 && !ManualReviewReady(draft)) _step = 2;

        if (!changed && _manualReadingDraft == draft?.Id) return;

        RememberManualReading();
        if (_manualReadingDraft != draft?.Id)
        {
            _manualReadings.Clear();
            _manualReadingDraft = draft?.Id;
            _manualInputSample = null;
        }
        var samples = draft?.Samples ?? [];
        foreach (var entry in _manualReadings.ToArray())
            if (!samples.Contains(entry.Value.Sample)) _manualReadings.Remove(entry.Key);

        _renderingManualRows = true;
        SampleList.ItemsSource = samples.Select(s => new SampleRow(s.Id,
            $"{Role(s.Role)} · {SegmentState(s)} · {SegmentTime(s)}")).ToArray();
        if (ManualDraft(previous)?.Id != draft?.Id)
            RoleChoice.SelectedValue = samples.LastOrDefault()?.Role ?? "Primary";
        _renderingManualRows = false;
        SelectManualRole(remember: false);
    }

    private static string SegmentTime(NoiseReferenceSample sample) =>
        $"{sample.WindowStartedAt.ToLocalTime():HH:mm:ss}–{sample.WindowEndedAt.ToLocalTime():HH:mm:ss}";

    private static string SegmentState(NoiseReferenceSample? sample) => sample switch
    {
        null => "待采样",
        { Quality: not "Good" } => Quality(sample.Quality),
        { SameWindowConfirmed: true, ReferenceDbA: not null } => "已记录",
        _ => "待填读数"
    };

    private void SelectManualRole(bool remember = true)
    {
        if (remember) RememberManualReading();
        var sample = ManualDraft(_state)?.Samples.FirstOrDefault(s => s.Role == RoleChoice.SelectedValue as string);
        _renderingManualRows = true;
        SampleList.SelectedItem = (SampleList.ItemsSource as IEnumerable<SampleRow>)?.FirstOrDefault(row => row.Id == sample?.Id);
        _renderingManualRows = false;
        ShowManualReading(sample);
    }

    private void ManualSampleSelected()
    {
        if (_renderingManualRows) return;
        RememberManualReading();
        var sample = ManualDraft(_state)?.Samples.FirstOrDefault(s => s.Id == (SampleList.SelectedItem as SampleRow)?.Id);
        if (sample is not null)
        {
            _renderingManualRows = true;
            RoleChoice.SelectedValue = sample.Role;
            _renderingManualRows = false;
        }
        ShowManualReading(sample);
    }

    private ReadingEdit CaptureManualReading(NoiseReferenceSample sample) =>
        new(sample, ReferenceValue.Text, ReferenceMinimum.Text, ReferenceMaximum.Text, SameWindow.IsChecked == true);

    private static ReadingEdit SavedReading(NoiseReferenceSample sample) =>
        new(sample, ReadingText(sample.ReferenceDbA), ReadingText(sample.ReferenceMinimumDbA),
            ReadingText(sample.ReferenceMaximumDbA), sample.SameWindowConfirmed);

    private static string ReadingText(double? value) => value?.ToString(CultureInfo.CurrentCulture) ?? "";

    private void RememberManualReading()
    {
        if (_manualInputSample is not { Quality: "Good" } sample) return;
        var edit = CaptureManualReading(sample);
        if (edit == SavedReading(sample)) _manualReadings.Remove(sample.Id);
        else _manualReadings[sample.Id] = edit;
    }

    private void ShowManualReading(NoiseReferenceSample? sample)
    {
        _manualInputSample = sample;
        var edit = sample is null ? null : _manualReadings.GetValueOrDefault(sample.Id) ?? SavedReading(sample);
        _renderingManualReading = true;
        ReferenceValue.Text = edit?.Value ?? "";
        ReferenceMinimum.Text = edit?.Minimum ?? "";
        ReferenceMaximum.Text = edit?.Maximum ?? "";
        SameWindow.IsChecked = edit?.SameWindow == true;
        _renderingManualReading = false;
        UpdateButtons();
    }

    private void ManualReadingEdited()
    {
        if (!_renderingManualReading) UpdateManualReadingAction();
    }

    private void UpdateManualReadingAction()
    {
        AttachReference.IsEnabled = _connected && !_busy && !_closing && !_verifying &&
            _state is { State: "Ready", Error: null } && !string.IsNullOrEmpty(ManagementSecret.Password) &&
            _manualInputSample is { Quality: "Good" } sample && SameWindow.IsChecked == true &&
            !string.IsNullOrWhiteSpace(ReferenceValue.Text) && CaptureManualReading(sample) != SavedReading(sample);
    }

    private void UpdateManualSteps()
    {
        var draft = ManualDraft(_state);
        bool ready = _connected && _state is { State: "Ready", Error: null };
        bool sampling = _connected && _state?.State == "Sampling" && draft is not null;
        bool settled = !_busy && !_closing && !_verifying;
        bool signedIn = !string.IsNullOrEmpty(ManagementSecret.Password);
        bool reviewing = ManualReviewReady(draft);
        bool editable = ready && settled && _manualInputSample is { Quality: "Good" };

        PreparePanel.Visibility = VisibleWhen(_step == 1);
        SamplePanel.Visibility = VisibleWhen(_step == 2);
        ResultPanel.Visibility = VisibleWhen(_step == 3);
        SavedReference.Visibility = VisibleWhen(_step == 3 || _state?.Profile is not null);
        SavedReference.Header = _state?.Profile is null ? "使用说明" : "已保存参考与使用说明";
        if (_displayedStep != _step) { _displayedStep = _step; ReferencePageScroll.ScrollToTop(); }

        SetupStep.IsEnabled = settled;
        SamplingStep.IsEnabled = settled && (ready || sampling) && draft is not null;
        ResultStep.IsEnabled = settled && ready && reviewing;
        SetupStep.Tag = _step == 1 ? "active" : null;
        SamplingStep.Tag = _step == 2 ? "active" : null;
        ResultStep.Tag = _step == 3 ? "active" : null;

        bool continuing = _step == 1 && draft is not null;
        BeginDraft.Visibility = VisibleWhen(_step == 1 && (!continuing || !sampling));
        BeginDraft.Content = continuing ? "另建草稿…" : "新建参考草稿";
        BeginDraft.SetResourceReference(StyleProperty, continuing ? "TeacherButton" : "TeacherPrimary");
        ContinueDraft.Visibility = VisibleWhen(continuing);
        ContinueDraft.IsEnabled = SamplingStep.IsEnabled;
        ContinueDraft.SetResourceReference(StyleProperty, "TeacherPrimary");
        StartSample.Visibility = VisibleWhen(_step == 2 && !sampling);
        StartSample.Content = _manualInputSample is null ? "开始本段采样" : "重新采样…";
        StartSample.IsEnabled &= signedIn && draft is not null;
        StartSample.SetResourceReference(StyleProperty, _manualInputSample is null ? "TeacherPrimary" : "TeacherButton");
        CancelSample.Visibility = VisibleWhen(_step == 2 && sampling);
        CancelSample.IsEnabled &= signedIn && draft is not null;
        ConfirmProfile.Visibility = ReviewSamples.Visibility = VisibleWhen(_step == 3);
        ConfirmProfile.IsEnabled &= signedIn && draft is not null;
        ReviewSamples.IsEnabled = SamplingStep.IsEnabled;
        ClearProfile.IsEnabled &= signedIn;
        RoleChoice.IsEnabled = SampleList.IsEnabled = settled && ready;
        ManualReferenceInputs.IsEnabled = OptionalRangeInputs.IsEnabled = SameWindow.IsEnabled = editable;
        AttachReference.Visibility = VisibleWhen(_step == 2 && _manualInputSample is { Quality: "Good" } && !sampling);
        UpdateManualReadingAction();
        ReferenceSampleReading.Visibility = VisibleWhen(!sampling && _manualInputSample is { Quality: "Good" });
        Progress.Visibility = VisibleWhen(sampling);
        RemoveSample.IsEnabled &= signedIn && draft is not null;
        RefreshStatus.Content = _step == 1 ? "刷新 / 验证口令" : "刷新状态";
        ManualCredentialStatus.Visibility = VisibleWhen(_step != 1 && _connected && !signedIn);

        var primary = draft?.Samples.FirstOrDefault(s => s.Role == "Primary");
        var check = draft?.Samples.FirstOrDefault(s => s.Role == "Check");
        ManualSegmentSummary.Text = $"第一电平 · {SegmentState(primary)}　　复核电平 · {SegmentState(check)}";
        ManualSegmentTarget.Text = _manualInputSample is { } selected
            ? $"{Role(selected.Role)} · {SegmentTime(selected)} · {SegmentState(selected)}"
            : "完成本段采样后，再填写同一时段的读数。";
    }

    private static Visibility VisibleWhen(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void ContinueDraftClicked(object sender, RoutedEventArgs e) => Navigate(2);
}
