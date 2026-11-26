using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace NPEduTools.App;

public partial class MainWindow
{
    private SecRandomWindow? _secRandomWindow;
    private Action? _secRandomReturn;
    private FrameworkElement? _secRandomReturnFocus;

    private void OpenSecRandomClicked(object sender, RoutedEventArgs e) => ShowSecRandomFrom(sender as FrameworkElement);
    private void ShowSecRandom() => ShowSecRandomFrom();

    private void ShowSecRandomFrom(FrameworkElement? anchor = null, Action? returnTo = null, Action? origin = null)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (SecRandomWorkspaceHost.Visibility == Visibility.Visible)
        {
            RestoreWindow();
            return;
        }
        if (!ConfirmLeaveWorkspaceCalibration("课堂点名")) return;

        var previousPage = origin ?? CaptureWorkspaceOrigin();
        bool keepCalibration = WorkspaceCalibration.Visibility == Visibility.Visible || ExamAwareWorkspaceHost.Visibility == Visibility.Visible ||
            ClassroomModeWorkspaceHost.Visibility == Visibility.Visible;
        if (anchor is null && WorkspaceCalibration.IsKeyboardFocusWithin)
            anchor = Keyboard.FocusedElement as FrameworkElement;
        SelectPageCore(false, keepCalibration);
        _secRandomReturn = returnTo is null ? previousPage : () => { previousPage(); returnTo(); };
        _secRandomReturnFocus = anchor;

        var page = GetSecRandomController();
        SecRandomWorkspaceHost.Content ??= new WorkspacePanel(page, this, page.WorkspaceVisibilityChanged);
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        SecRandomNav.Tag = "active";
        PageBreadcrumb.Text = "课堂点名";
        SecRandomWorkspaceHost.Visibility = Visibility.Visible;
        RestoreWindow();
        page.FocusWorkspaceEntry();
    }

    private SecRandomWindow GetSecRandomController()
    {
        if (_secRandomWindow is not null) return _secRandomWindow;
        _secRandomWindow = new SecRandomWindow(_pipe);
        _secRandomWindow.SetWorkspaceNavigation(ReturnFromSecRandom);
        _secRandomWindow.SidebarDrawVerified += receipt => _quick?.ApplyVerifiedSidebarDraw(receipt);
        return _secRandomWindow;
    }

    private void HideSecRandomWorkspace()
    {
        SecRandomWorkspaceHost.Visibility = Visibility.Collapsed;
        SecRandomNav.Tag = null;
        _secRandomReturn = null;
        _secRandomReturnFocus = null;
    }

    private void ReturnFromSecRandom()
    {
        var previousPage = _secRandomReturn;
        var anchor = _secRandomReturnFocus;
        if (previousPage is null) SelectPage(false);
        else previousPage();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsActive || SecRandomWorkspaceHost.IsVisible) return;
            var target = anchor is { IsVisible: true, IsEnabled: true } ? anchor : SecRandomNav;
            target.BringIntoView();
            target.Focus();
        });
    }

    private Task<SidebarDrawReceipt> QuickDrawSecRandomAsync() => GetSecRandomController().QuickDrawFromSidebarAsync();

    private async Task VerifySidebarDrawAsync()
    {
        ShowSecRandom();
        if (SecRandomWorkspaceHost.IsVisible) await GetSecRandomController().VerifySidebarDrawAsync();
    }
}
