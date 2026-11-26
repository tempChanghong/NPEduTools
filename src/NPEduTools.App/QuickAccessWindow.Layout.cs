using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace NPEduTools.App;

public partial class QuickAccessWindow
{
    private Button? _pressedLayoutSide;
    private TouchDevice? _layoutTouch;
    private bool _layoutTouchExited;
    private int _layoutSideRevision;

    private bool IsLayoutOpen => LayoutOptions.Visibility == Visibility.Visible;

    private void InitializeLayoutOptions()
    {
        foreach (var button in new[] { LayoutLeft, LayoutRight })
        {
            button.PreviewTouchDown += (_, e) =>
            {
                e.Handled = true;
                if (_layoutTouch is not null || _handleTouch is not null || _drawTouch is not null ||
                    _pptTouch is not null || RecordingTouch is not null || !CanSelectLayoutSide(button)) return;
                _layoutSideRevision++;
                _pressedLayoutSide = button;
                _layoutTouch = e.TouchDevice;
                _layoutTouchExited = false;
                button.Opacity = .72;
                if (!button.CaptureTouch(e.TouchDevice)) CancelLayoutSidePress();
            };
            button.PreviewTouchMove += (_, e) =>
            {
                e.Handled = true;
                if (e.TouchDevice != _layoutTouch || button != _pressedLayoutSide || ButtonContainsTouchPoint(button, e)) return;
                _layoutTouchExited = true;
                button.ClearValue(OpacityProperty);
            };
            button.PreviewTouchUp += (_, e) =>
            {
                e.Handled = true;
                if (e.TouchDevice != _layoutTouch || button != _pressedLayoutSide) return;
                bool invoke = !_layoutTouchExited && ButtonContainsTouchPoint(button, e);
                CancelLayoutSidePress();
                if (!invoke) return;
                // Finish dispatching the original contact before repositioning its HWND.
                int revision = _layoutSideRevision;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (revision == _layoutSideRevision && GetForegroundWindow() == _handle) SelectLayoutSide(button);
                });
            };
            button.LostTouchCapture += (_, e) =>
            {
                if (e.TouchDevice == _layoutTouch) CancelLayoutSidePress();
            };
            button.PreviewMouseLeftButtonDown += (_, e) => { if (IsPromotedTouch(e)) e.Handled = true; };
            button.PreviewMouseLeftButtonUp += (_, e) => { if (IsPromotedTouch(e)) e.Handled = true; };
        }
    }

    private void CancelLayoutSidePress()
    {
        _layoutSideRevision++;
        var button = _pressedLayoutSide;
        var finger = _layoutTouch;
        _pressedLayoutSide = null;
        _layoutTouch = null;
        button?.ClearValue(OpacityProperty);
        if (finger is not null) button?.ReleaseTouchCapture(finger);
    }

    private bool CanSelectLayoutSide(Button button) =>
        _expanded && !_autoHidden && !_closing && LayoutOptions.IsVisible && button.IsVisible && button.IsEnabled;

    private void LayoutSideClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) SelectLayoutSide(button);
    }

    private void SelectLayoutSide(Button button)
    {
        if (CanSelectLayoutSide(button)) SetSide(button == LayoutLeft);
    }

    private void LayoutClicked(object sender, RoutedEventArgs e)
    {
        if (!_expanded || _autoHidden || IsLayoutOpen) return;
        InvalidatePptPress();
        CancelRecordingPress();
        DismissRecordingConfirmation(refocus: false);
        ShortcutViewRevision++;
        LayoutSaveError.Visibility = Visibility.Collapsed;
        LayoutLeft.Tag = LeftSide ? "active" : null;
        LayoutRight.Tag = LeftSide ? null : "active";
        AutomationProperties.SetItemStatus(LayoutLeft, LeftSide ? "当前侧" : "");
        AutomationProperties.SetItemStatus(LayoutRight, LeftSide ? "" : "当前侧");
        QuickPanelContent.Visibility = Visibility.Hidden;
        LayoutOptions.Visibility = Visibility.Visible;
        LayoutBack.Focus();
    }

    private void LayoutBackClicked(object sender, RoutedEventArgs e) => CloseLayoutOptions(refocus: true);

    private void CloseLayoutOptions(bool refocus = false)
    {
        CancelLayoutSidePress();
        if (!IsLayoutOpen) return;
        LayoutOptions.Visibility = Visibility.Collapsed;
        QuickPanelContent.Visibility = Visibility.Visible;
        if (refocus) LayoutButton.Focus();
    }
}
