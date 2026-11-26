using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class QuickAccessWindow
{
    private sealed record RecordingTarget(RecorderControl Control, string Phase)
    {
        public bool Matches(RecordingTarget? other) => other is not null && Phase == other.Phase && Control.Matches(other.Control);
    }

    private sealed record RecordingPress(Button Button, RecordingTarget Target, TouchDevice? Finger, Key Key);
    private RecordingTarget? _recordingTarget, _recordingConfirmation;
    private RecordingPress? _recordingPress;
    private bool _recordingNeedsReview;
    private TouchDevice? RecordingTouch => _recordingPress?.Finger;
    private bool _recordingPressLeft;
    private Action<string, RecorderControl, string>? _recordingAction;

    private void InitializeRecordingControls()
    {
        foreach (var button in new[] { RecordingPause, RecordingStop, RecordingConfirmStop, RecordingCancelStop })
        {
            button.PreviewTouchDown += (_, e) =>
            {
                e.Handled = true;
                if (BeginRecordingPress(button, e.TouchDevice) && !button.CaptureTouch(e.TouchDevice)) CancelRecordingPress();
            };
            button.PreviewTouchMove += (_, e) =>
            {
                e.Handled = true;
                if (RecordingTouch == e.TouchDevice)
                    _recordingPressLeft |= !ButtonContainsTouchPoint(button, e);
            };
            button.PreviewTouchUp += (_, e) =>
            {
                e.Handled = true;
                if (RecordingTouch == e.TouchDevice) ReleaseRecordingPress(button, ButtonContainsTouchPoint(button, e));
            };
            button.LostTouchCapture += (_, e) =>
            {
                if (RecordingTouch == e.TouchDevice) CancelRecordingPress();
            };
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!IsPromotedTouch(e) && BeginRecordingPress(button) && !button.CaptureMouse()) CancelRecordingPress();
            };
            button.PreviewMouseMove += (_, e) =>
            {
                if (_recordingPress is { Finger: null, Key: Key.None } press && press.Button == button)
                    _recordingPressLeft |= !new Rect(button.RenderSize).Contains(e.GetPosition(button));
            };
            button.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!IsPromotedTouch(e) && _recordingPress is { Finger: null, Key: Key.None })
                    ReleaseRecordingPress(button, new Rect(button.RenderSize).Contains(e.GetPosition(button)));
            };
            button.LostMouseCapture += (_, _) =>
            {
                if (_recordingPress is { Finger: null, Key: Key.None } press && press.Button == button) CancelRecordingPress();
            };
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Space or Key.Enter)) return;
                e.Handled = true;
                if (!e.IsRepeat) BeginRecordingPress(button, key: e.Key);
            };
            button.PreviewKeyUp += (_, e) =>
            {
                if (e.Key is not (Key.Space or Key.Enter)) return;
                e.Handled = true;
                if (_recordingPress?.Key == e.Key) ReleaseRecordingPress(button, inside: true);
            };
            button.LostKeyboardFocus += (_, _) =>
            {
                if (_recordingPress is { Key: not Key.None } press && press.Button == button) CancelRecordingPress();
            };
        }
        ToolsPage.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.ViewportWidthChange != 0) CancelRecordingPress();
        };
    }

    private bool BeginRecordingPress(Button button, TouchDevice? finger = null, Key key = Key.None)
    {
        if (_recordingPress is not null || _handleTouch is not null || _drawTouch is not null || _pptTouch is not null ||
            _recordingTarget is not { } target || !CanActOnRecording(button, target)) return false;
        if (key == Key.None && finger is null) button.Focus();
        if (!CanActOnRecording(button, target)) return false;
        _recordingPress = new(button, target, finger, key);
        _recordingPressLeft = false;
        return true;
    }

    private void CancelRecordingPress()
    {
        var press = _recordingPress;
        _recordingPress = null;
        if (press?.Finger is { } finger) press.Button.ReleaseTouchCapture(finger);
        else if (press?.Button.IsMouseCaptured == true) press.Button.ReleaseMouseCapture();
    }

    private void ReleaseRecordingPress(Button button, bool inside)
    {
        var press = _recordingPress;
        bool invoke = inside && !_recordingPressLeft && press?.Button == button;
        CancelRecordingPress();
        if (invoke) InvokeRecordingButton(button, press!.Target);
    }

    private bool CanActOnRecording(Button button, RecordingTarget target) =>
        _expanded && !_autoHidden && !_closing && !_shortcutsSelected && _moveOrigin is null &&
        button.IsVisible && button.IsEnabled && target.Matches(_recordingTarget);

    private void InvokeRecordingButton(Button button, RecordingTarget target)
    {
        if (!CanActOnRecording(button, target)) return;
        if (button == RecordingPause)
        {
            _recordingAction?.Invoke(target.Phase == "Paused" ? "resume" : "pause", target.Control, target.Phase);
            return;
        }
        if (button == RecordingStop)
        {
            _recordingConfirmation = target;
            RecordingConfirmationText.Text = target.Control.Owner == "Automatic" ? "结束本场？后续自动录课照常" : "结束本场录课？";
            RecordingConfirmationText.ToolTip = RecordingConfirmationText.Text;
            RefreshRecordingControlSlot();
            if (IsActive) RecordingCancelStop.Focus();
            return;
        }
        if (!target.Matches(_recordingConfirmation)) return;
        bool stop = button == RecordingConfirmStop;
        DismissRecordingConfirmation(refocus: !stop);
        if (stop) _recordingAction?.Invoke("stop", target.Control, target.Phase);
    }

    private bool DismissRecordingConfirmation(bool refocus)
    {
        if (_recordingConfirmation is null) return false;
        _recordingConfirmation = null;
        CancelRecordingPress();
        RefreshRecordingControlSlot();
        if (refocus && _expanded && IsActive)
        {
            if (RecordingStop.IsVisible && RecordingStop.IsEnabled) RecordingStop.Focus();
            else RecordingButton.Focus();
        }
        return true;
    }

    private void RecordingButtonAction(object sender)
    {
        if (sender is Button button && _recordingTarget is { } target) InvokeRecordingButton(button, target);
    }

    private void RecordingPauseClicked(object sender, RoutedEventArgs e) => RecordingButtonAction(sender);
    private void RecordingStopClicked(object sender, RoutedEventArgs e) => RecordingButtonAction(sender);
    private void RecordingConfirmStopClicked(object sender, RoutedEventArgs e) => RecordingButtonAction(sender);
    private void RecordingCancelStopClicked(object sender, RoutedEventArgs e) => RecordingButtonAction(sender);

    public void UpdateRecording(RecordingState state, bool stateAvailable = true) => UpdateRecording(state, stateAvailable, null);

    internal void UpdateRecording(RecordingState state, bool stateAvailable, SidebarStop? stop)
    {
        SidebarStopPhase? result = stop is { } operation &&
            (operation.Target.Matches(state.Control) || state.Control is null && operation.Phase == SidebarStopPhase.Unconfirmed)
            ? operation.Phase : null;
        if (!stateAvailable && result == SidebarStopPhase.Saving) result = SidebarStopPhase.Unconfirmed;
        bool active = stateAvailable && state.Active;
        var next = active && !state.Busy && state.Control is { } control && result != SidebarStopPhase.Requesting
            ? new RecordingTarget(control, state.Phase) : null;
        bool targetChanged = _recordingTarget is null ? next is not null : !_recordingTarget.Matches(next);
        _recordingTarget = next;
        bool review = result == SidebarStopPhase.Unconfirmed;
        if (_recordingTask != active || _recordingNeedsReview != review)
        {
            _recordingTask = active; _recordingNeedsReview = review; _taskOrderDirty = true;
        }
        RefreshTaskOrder();

        var phase = HomeRecordingPresentation.Phase(state.Phase, stateAvailable);
        if (result == SidebarStopPhase.Requesting) phase = HomeRecordingPresentation.Phase("Saving", true);
        HomeRecordingPresentation.ShowMark(QuickRecordingPhaseMark, review ? "?" : phase.Mark, review ? "TeacherWarningBrush" : phase.Brush);
        RecordingStatus.SetResourceReference(TextBlock.ForegroundProperty, review ? "TeacherWarningBrush" : "TeacherMutedBrush");
        RecordingStatus.Text = result switch
        {
            SidebarStopPhase.Requesting => "正在请求停止本场…",
            SidebarStopPhase.Saving => "正在保存本场录课…",
            SidebarStopPhase.Saved => "本场录课已保存",
            SidebarStopPhase.Failed => "本场录课未完成",
            SidebarStopPhase.Unconfirmed => active ? "停止结果待核实 · 本场未结束" : "停止结果待核实",
            SidebarStopPhase.Rejected => "本次停止未获准",
            _ => HomeRecordingPresentation.Detail(state, stateAvailable)
        };
        if (result is null && active && state.Control is { Owner: "Automatic", HardDeadline: > 0 } automatic)
            RecordingStatus.Text += $" · 至多剩余 {TimeSpan.FromSeconds(Math.Max(0, RecorderDeadline.SecondsUntil(automatic.HardDeadline))):hh\\:mm\\:ss}";
        RecordingStatus.ToolTip = (result is SidebarStopPhase.Rejected or SidebarStopPhase.Failed) && !string.IsNullOrWhiteSpace(state.Error)
            ? RecordingStatus.Text + "\n" + state.Error : RecordingStatus.Text;
        RecordingButton.Content = !stateAvailable || state.Busy ? "查看" : active ? "控制" : "录制";
        RecordingPause.Content = state.Phase == "Paused" ? "继续" : "暂停";
        RecordingPause.IsEnabled = RecordingStop.IsEnabled = next is not null;

        if (targetChanged)
        {
            CancelRecordingPress();
            DismissRecordingConfirmation(refocus: RecordingStopConfirmation.IsKeyboardFocusWithin);
        }
        RefreshRecordingControlSlot();
    }

    private void RefreshRecordingControlSlot()
    {
        if (!_expanded) _recordingControlSlot = _recordingTask;
        bool confirming = _recordingConfirmation is not null;
        RecordingControls.Visibility = !_recordingControlSlot ? Visibility.Collapsed :
            _recordingTask && !confirming ? Visibility.Visible : Visibility.Hidden;
        RecordingStatus.Visibility = confirming ? Visibility.Hidden : Visibility.Visible;
        RecordingConfirmationText.Visibility = RecordingStopConfirmation.Visibility = confirming ? Visibility.Visible : Visibility.Collapsed;
    }
}
