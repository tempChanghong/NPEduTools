using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace NPEduTools.App;

public partial class QuickAccessWindow
{
    private const double DrawReceiptWidth = 263;
    private Func<Task<SidebarDrawReceipt>>? _secRandomQuickDraw;
    private Func<Task>? _verifySecRandomDraw;
    private SidebarDrawReceipt? _drawReceipt;
    private bool _drawReceiptOpen, _drawNameRevealed;
    private TouchDevice? _drawTouch;
    private Button? _drawTouchButton;
    private bool _drawTouchCancelled;

    private void WireDrawTouch()
    {
        foreach (var button in new[] { RailSecRandom, DrawReceiptAction, DrawNext, DrawReceiptDismiss })
        {
            button.PreviewTouchDown += (_, e) =>
            {
                if (_drawTouch is null && button.IsEnabled)
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
                if (_drawTouch == e.TouchDevice) _drawTouchCancelled |= !DrawButtonContains(button, e);
                e.Handled = true;
            };
            button.PreviewTouchUp += (_, e) =>
            {
                e.Handled = true;
                if (_drawTouch != e.TouchDevice || _drawTouchButton != button) return;
                bool invoke = !_drawTouchCancelled && DrawButtonContains(button, e) && button.IsEnabled && button.IsVisible;
                CancelDrawTouch();
                if (invoke) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            };
            button.LostTouchCapture += (_, e) =>
            {
                if (_drawTouch == e.TouchDevice) CancelDrawTouch();
            };
            button.PreviewMouseLeftButtonDown += (_, e) => { if (IsPromotedTouch(e) || _drawTouch is not null) e.Handled = true; };
            button.PreviewMouseLeftButtonUp += (_, e) => { if (IsPromotedTouch(e) || _drawTouch is not null) e.Handled = true; };
        }
    }

    private bool DrawButtonContains(Button button, TouchEventArgs e) =>
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
        if (_closing || _autoHidden || _moveOrigin is not null || _placement.Compact) return;
        if (_drawReceipt is null) await StartSidebarDrawAsync();
        else if (_drawReceipt.State != SidebarDrawState.Running)
        {
            if (_expanded) Collapse();
            _drawReceiptOpen = true;
            RenderDrawReceipt();
        }
    }

    private async Task StartSidebarDrawAsync()
    {
        if (_secRandomQuickDraw is null || _closing || _autoHidden || _placement.Compact ||
            _drawReceipt?.State is SidebarDrawState.Running or SidebarDrawState.Pending) return;
        Collapse();
        _drawReceipt = new(SidebarDrawState.Running, "正在等待 SecRandom 回执。");
        _drawReceiptOpen = true;
        RenderDrawReceipt();
        var receipt = await _secRandomQuickDraw();
        if (_closing) return;
        _drawReceipt = receipt;
        RenderDrawReceipt();
    }

    internal void ApplyVerifiedSidebarDraw(SidebarDrawReceipt receipt)
    {
        if (_drawReceipt is not { State: SidebarDrawState.Pending } pending ||
            pending.RequestId != receipt.RequestId || _closing) return;
        _drawReceipt = receipt;
        _drawNameRevealed = false;
        RenderDrawReceipt();
    }

    private async void DrawReceiptActionClicked(object sender, RoutedEventArgs e)
    {
        if (_drawReceipt is not { } receipt || !_drawReceiptOpen || _autoHidden) return;
        if (receipt.State == SidebarDrawState.Succeeded)
        {
            _drawNameRevealed = true;
            RenderDrawReceipt();
            return;
        }
        DismissDrawReceipt();
        Position();
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
        DismissDrawReceipt();
        Position();
    }

    private void DismissDrawReceipt()
    {
        CancelDrawTouch();
        _drawReceiptOpen = _drawNameRevealed = false;
        DrawReceiptCard.Visibility = Visibility.Collapsed;
        DrawReceiptDetail.Text = "";
    }

    private void RenderDrawReceipt()
    {
        CancelDrawTouch();
        var receipt = _drawReceipt;
        var (label, glyph, title) = receipt?.State switch
        {
            SidebarDrawState.Running => ("抽取中", "…", "正在闪抽"),
            SidebarDrawState.Succeeded => ("已抽取", "✓", "闪抽完成"),
            SidebarDrawState.Pending => ("待核实", "!", "闪抽待核实"),
            SidebarDrawState.Reconciled => ("已核实", "✓", "闪抽已核实"),
            SidebarDrawState.SetupRequired => ("待配置", "抽", "先配置课堂点名"),
            SidebarDrawState.Failed => ("未完成", "抽", "闪抽未完成"),
            _ => ("闪抽", "抽", "闪抽一人")
        };
        RailSecRandomGlyph.Text = glyph;
        RailSecRandomLabel.Text = label;
        RailSecRandom.IsEnabled = _secRandomQuickDraw is not null && receipt?.State != SidebarDrawState.Running;
        RailSecRandom.ToolTip = title;
        AutomationProperties.SetName(RailSecRandom, title);
        if (!_drawReceiptOpen || _autoHidden || _expanded || _moveOrigin is not null || _placement.Compact || receipt is null)
        {
            DismissDrawReceipt();
            Position();
            return;
        }

        DrawReceiptCard.Visibility = Visibility.Visible;
        DrawReceiptTitle.Text = title;
        DrawReceiptDetail.Text = receipt.State == SidebarDrawState.Succeeded
            ? _drawNameRevealed && receipt.WinnerName is { Length: > 0 } winner ? $"本次抽中：{winner}" : ""
            : receipt.Detail;
        DrawReceiptDetail.Visibility = DrawReceiptDetail.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        string? primary = receipt.State switch
        {
            SidebarDrawState.Succeeded when !_drawNameRevealed && receipt.WinnerName is { Length: > 0 } => "查看结果",
            SidebarDrawState.Pending => "核实",
            SidebarDrawState.SetupRequired => "去配置",
            SidebarDrawState.Failed => "查看课堂点名",
            _ => null
        };
        DrawReceiptAction.Content = primary;
        DrawReceiptAction.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        DrawNext.Content = receipt.State is SidebarDrawState.Succeeded or SidebarDrawState.Reconciled
            ? "再抽一位" : "重新检查并闪抽";
        DrawNext.Visibility = receipt.State is SidebarDrawState.Succeeded or SidebarDrawState.Reconciled or
            SidebarDrawState.SetupRequired or SidebarDrawState.Failed ? Visibility.Visible : Visibility.Collapsed;
        Position();
    }
}
