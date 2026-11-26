using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace NPEduTools.App;

public partial class QuickAccessWindow
{
    private const double DrawReceiptWidth = 263;
    private const long FlashDrawRepeatDelayMs = 450;
    private Func<Task<SidebarDrawReceipt>>? _secRandomQuickDraw;
    private Func<Task>? _verifySecRandomDraw;
    private SidebarDrawReceipt? _drawReceipt;
    private bool _drawReceiptOpen, _drawInFlight;
    private long _nextDrawAllowedTick;
    private TouchDevice? _drawTouch;
    private Button? _drawTouchButton;
    private bool _drawTouchCancelled;

    private void WireDrawTouch()
    {
        foreach (var button in new[] { RailSecRandom, DrawReceiptAction, DrawNext, DrawReceiptDismiss })
        {
            button.PreviewTouchDown += (_, e) =>
            {
                if (_drawTouch is null && button.IsEnabled && CanPressDrawButton(button))
                {
                    _drawTouch = e.TouchDevice;
                    _drawTouchButton = button;
                    _drawTouchCancelled = false;
                    if (!button.CaptureTouch(e.TouchDevice)) CancelDrawTouch();
                }
                e.Handled = true;
            };
            button.PreviewTouchMove += (_, e) =>
            {
                if (_drawTouch == e.TouchDevice) _drawTouchCancelled |= !ButtonContainsTouchPoint(button, e);
                e.Handled = true;
            };
            button.PreviewTouchUp += (_, e) =>
            {
                e.Handled = true;
                if (_drawTouch != e.TouchDevice || _drawTouchButton != button) return;
                bool invoke = !_drawTouchCancelled && ButtonContainsTouchPoint(button, e) && button.IsEnabled && button.IsVisible;
                CancelDrawTouch();
                if (invoke) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            };
            button.LostTouchCapture += (_, e) =>
            {
                if (_drawTouch == e.TouchDevice) CancelDrawTouch();
            };
            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (IsPromotedTouch(e) || _drawTouch is not null || !CanPressDrawButton(button)) e.Handled = true;
            };
            button.PreviewMouseLeftButtonUp += (_, e) => { if (IsPromotedTouch(e) || _drawTouch is not null) e.Handled = true; };
            button.PreviewKeyDown += (_, e) =>
            {
                if ((button == RailSecRandom || button == DrawNext) && e.IsRepeat && e.Key is Key.Enter or Key.Space)
                    e.Handled = true;
            };
        }
    }

    private bool CanPressDrawButton(Button button) =>
        !(button == RailSecRandom || button == DrawNext) || _drawReceipt?.State == SidebarDrawState.Pending ||
        !_drawInFlight && Environment.TickCount64 >= _nextDrawAllowedTick;

    private bool ButtonContainsTouchPoint(Button button, TouchEventArgs e) =>
        new Rect(button.RenderSize).Contains(button.PointFromScreen(PointToScreen(e.GetTouchPoint(this).Position)));

    private void CancelDrawTouch()
    {
        var finger = _drawTouch;
        var button = _drawTouchButton;
        _drawTouch = null;
        _drawTouchButton = null;
        if (finger is not null) button?.ReleaseTouchCapture(finger);
    }

    internal void SetSecRandomQuickDrawAction(Func<Task<SidebarDrawReceipt>> draw, Func<Task> verify)
    {
        _secRandomQuickDraw = draw;
        _verifySecRandomDraw = verify;
        RenderDrawReceipt();
    }

    private async void RailSecRandomClicked(object sender, RoutedEventArgs e)
    {
        if (_closing || _autoHidden || _moveOrigin is not null || _placement.Compact || !CanPressDrawButton(RailSecRandom)) return;
        if (_drawReceipt is null) await StartSidebarDrawAsync();
        else
        {
            if (_expanded) Collapse();
            _drawReceiptOpen = true;
            RenderDrawReceipt();
        }
    }

    private async Task StartSidebarDrawAsync()
    {
        if (_secRandomQuickDraw is null || _closing || _autoHidden || _placement.Compact || _moveOrigin is not null ||
            _drawInFlight || _drawReceipt?.State == SidebarDrawState.Pending || Environment.TickCount64 < _nextDrawAllowedTick) return;
        Collapse();
        _drawInFlight = true;
        _drawReceipt = null;
        RenderDrawReceipt();
        _drawReceiptOpen = true; // A later collapse or protected display revokes this intent.
        try
        {
            var receipt = await _secRandomQuickDraw();
            if (!_closing) ApplyDrawReceipt(receipt);
        }
        finally { _drawInFlight = false; }
    }

    internal void ApplyVerifiedSidebarDraw(SidebarDrawReceipt receipt)
    {
        if (_drawReceipt is not { State: SidebarDrawState.Pending } pending ||
            pending.RequestId != receipt.RequestId || _closing) return;
        ApplyDrawReceipt(receipt);
    }

    private void ApplyDrawReceipt(SidebarDrawReceipt receipt)
    {
        if (receipt.State is SidebarDrawState.Succeeded or SidebarDrawState.Reconciled)
        {
            _drawReceipt = null;
            _nextDrawAllowedTick = Environment.TickCount64 + FlashDrawRepeatDelayMs;
        }
        else _drawReceipt = receipt;
        RenderDrawReceipt();
    }

    private async void DrawReceiptActionClicked(object sender, RoutedEventArgs e)
    {
        if (_drawReceipt is not { } receipt || !_drawReceiptOpen || _autoHidden) return;
        DismissDrawReceipt();
        if (receipt.State is SidebarDrawState.Failed or SidebarDrawState.SetupRequired) _drawReceipt = null;
        RenderDrawReceipt();
        if (receipt.State == SidebarDrawState.Pending && _verifySecRandomDraw is not null)
            await _verifySecRandomDraw();
        else if (receipt.State is SidebarDrawState.Failed or SidebarDrawState.SetupRequired)
            _secRandom?.Invoke();
    }

    private async void DrawNextClicked(object sender, RoutedEventArgs e)
    {
        if (_drawReceiptOpen && DrawNext.IsVisible && _moveOrigin is null) await StartSidebarDrawAsync();
    }

    private void DrawReceiptDismissClicked(object sender, RoutedEventArgs e)
    {
        if (_drawReceipt?.State is SidebarDrawState.Failed or SidebarDrawState.SetupRequired) _drawReceipt = null;
        DismissDrawReceipt();
        RenderDrawReceipt();
    }

    private void DismissDrawReceipt()
    {
        CancelDrawTouch();
        _drawReceiptOpen = false;
        DrawReceiptCard.Visibility = Visibility.Collapsed;
        DrawReceiptDetail.Text = "";
    }

    private void RenderDrawReceipt()
    {
        CancelDrawTouch();
        var receipt = _drawReceipt;
        bool pending = receipt?.State == SidebarDrawState.Pending;
        RailSecRandomGlyph.Text = pending ? "!" : "抽";
        RailSecRandomLabel.Text = pending ? "待核实" : "闪抽";
        RailSecRandom.IsEnabled = _secRandomQuickDraw is not null;
        string railLabel = pending ? "闪抽待核实" : "闪抽一人";
        RailSecRandom.ToolTip = railLabel;
        AutomationProperties.SetName(RailSecRandom, railLabel);
        if (!_drawReceiptOpen || _autoHidden || _expanded || _moveOrigin is not null || _placement.Compact || receipt is null)
        {
            DismissDrawReceipt();
            Position();
            return;
        }

        DrawReceiptCard.Visibility = Visibility.Visible;
        var (title, action) = receipt.State switch
        {
            SidebarDrawState.Pending => ("闪抽待核实", "核实"),
            SidebarDrawState.SetupRequired => ("先配置课堂点名", "去配置"),
            _ => ("闪抽未完成", "打开课堂点名")
        };
        DrawReceiptTitle.Text = title;
        DrawReceiptDetail.Text = receipt.Detail;
        DrawReceiptAction.Content = action;
        DrawNext.Content = receipt.State == SidebarDrawState.SetupRequired ? "重新检查并闪抽" : "重试闪抽";
        DrawNext.Visibility = pending ? Visibility.Collapsed : Visibility.Visible;
        Position();
    }
}
