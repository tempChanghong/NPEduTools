using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class QuickAccessWindow
{
    private (bool Available, bool Running, bool Paused) _pptActions;
    private Button? _pptPressed, _pptKeyboardButton;
    private TouchDevice? _pptTouch;
    private bool _pptPressOutside;
    private long _pptRevision, _pptKeyboardRevision;
    private bool? _pptActionsStacked;

    private void InitializePptStrip()
    {
        foreach (var button in new[] { PauseButton, PowerButton })
        {
            button.PreviewTouchDown += (_, e) =>
            {
                e.Handled = true;
                if (_pptPressed is not null || _handleTouch is not null || _drawTouch is not null || !CanUsePptAction(button)) return;
                _pptPressed = button;
                _pptTouch = e.TouchDevice;
                _pptPressOutside = false;
                if (!button.CaptureTouch(e.TouchDevice)) CancelPptPress();
            };
            button.PreviewTouchMove += (_, e) =>
            {
                e.Handled = true;
                if (_pptTouch == e.TouchDevice && _pptPressed == button)
                    _pptPressOutside |= !ButtonContainsTouchPoint(button, e);
            };
            button.PreviewTouchUp += (_, e) =>
            {
                e.Handled = true;
                if (_pptTouch == e.TouchDevice && _pptPressed == button)
                    ReleasePptPress(button, ButtonContainsTouchPoint(button, e));
            };
            button.LostTouchCapture += (_, e) =>
            {
                if (_pptTouch == e.TouchDevice) CancelPptPress();
            };
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (IsPromotedTouch(e) || _pptPressed is not null || _handleTouch is not null || _drawTouch is not null ||
                    !CanUsePptAction(button)) return;
                _pptPressed = button;
                _pptPressOutside = false;
                if (!button.CaptureMouse()) CancelPptPress();
            };
            button.PreviewMouseMove += (_, e) =>
            {
                if (_pptTouch is null && _pptPressed == button &&
                    !new Rect(button.RenderSize).Contains(e.GetPosition(button))) _pptPressOutside = true;
            };
            button.PreviewMouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (!IsPromotedTouch(e) && _pptTouch is null && _pptPressed == button)
                    ReleasePptPress(button, new Rect(button.RenderSize).Contains(e.GetPosition(button)));
            };
            button.LostMouseCapture += (_, _) =>
            {
                if (_pptTouch is null && _pptPressed == button) CancelPptPress();
            };
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is not (Key.Space or Key.Enter)) return;
                if (e.IsRepeat || !CanUsePptAction(button)) { e.Handled = true; return; }
                _pptKeyboardButton = button;
                _pptKeyboardRevision = _pptRevision;
            };
            button.LostKeyboardFocus += (_, _) =>
            {
                if (_pptKeyboardButton == button) _pptKeyboardButton = null;
            };
        }
        PptActionLayout.SizeChanged += (_, _) => ArrangePptActions();
    }

    private void ArrangePptActions()
    {
        double density = ((FrameworkElement)Content).LayoutTransform is System.Windows.Media.ScaleTransform transform
            ? transform.ScaleX : 1;
        if (PptActionLayout.ActualWidth <= 0) return;
        bool stacked = PptActionLayout.ActualWidth * density < 280;
        if (_pptActionsStacked == stacked) return;
        _pptActionsStacked = stacked;
        InvalidatePptPress();
        PptActionGapColumn.Width = new GridLength(stacked ? 0 : 6);
        PptActionSecondaryColumn.Width = stacked ? new GridLength(0) : GridLength.Auto;
        Grid.SetRow(PowerButton, stacked ? 1 : 0);
        Grid.SetColumn(PowerButton, stacked ? 0 : 2);
        PowerButton.Margin = stacked ? new Thickness(0, 6, 0, 0) : new Thickness();
    }

    private void UpdatePptStrip(TouchAssistState? state, string status, bool available)
    {
        var actions = (available && state is not null, state?.Running == true, state?.Paused == true);
        if (_pptActions != actions)
        {
            _pptActions = actions;
            InvalidatePptPress();
        }
        StatusText.Text = status;
        StatusText.ToolTip = status;
        PowerButton.Content = _pptActions.Running ? "停止辅助" : "开启辅助";
        PowerButton.IsEnabled = _pptActions.Available;
        PauseButton.Content = _pptActions.Paused ? "继续辅助" : "暂停辅助";
        PauseButton.Visibility = _pptActions.Running ? Visibility.Visible : Visibility.Hidden;
        PauseButton.IsEnabled = _pptActions.Available && _pptActions.Running;
        PowerButton.SetResourceReference(StyleProperty, _pptActions.Running ? "TeacherButton" : "TeacherPrimary");
    }

    private bool CanUsePptAction(Button button) =>
        _expanded && !_autoHidden && !_closing && _moveOrigin is null && !_shortcutsSelected &&
        _pptActions.Available && button.IsVisible && button.IsEnabled;

    private void InvalidatePptPress()
    {
        _pptRevision++;
        CancelPptPress();
    }

    private void CancelPptPress()
    {
        var button = _pptPressed;
        var finger = _pptTouch;
        _pptPressed = null;
        _pptTouch = null;
        if (finger is not null) button?.ReleaseTouchCapture(finger);
        else if (button?.IsMouseCaptured == true) button.ReleaseMouseCapture();
    }

    private void ReleasePptPress(Button button, bool inside)
    {
        // A command-state change invalidates the capture before this release can arrive.
        bool invoke = inside && !_pptPressOutside && CanUsePptAction(button);
        CancelPptPress();
        if (invoke) InvokePptAction(button);
    }

    private void InvokePptAction(Button button)
    {
        if (!CanUsePptAction(button)) return;
        // Both Shell callbacks dispatch synchronously from the same state used by Update.
        if (button == PauseButton) _pause();
        else _power();
    }

    private void PptActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (_pptKeyboardButton == button)
        {
            _pptKeyboardButton = null;
            if (_pptKeyboardRevision != _pptRevision) return;
        }
        InvokePptAction(button);
    }
}
