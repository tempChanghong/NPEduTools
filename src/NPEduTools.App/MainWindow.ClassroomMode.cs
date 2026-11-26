using System.Windows;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ClassroomModeWindow? _classroomModeWindow;
    private bool _homeModeAttention;
    private Action? _classroomModeReturn, _classroomSettingsReturn;
    private FrameworkElement? _classroomModeReturnFocus;
    private void OpenClassroomModeClicked(object sender, RoutedEventArgs e) => ShowClassroomModeFrom(sender as FrameworkElement);
    private void ShowClassroomMode() => ShowClassroomModeFrom();

    private void ShowClassroomModeFrom(FrameworkElement? anchor = null, Action? returnTo = null, Action? origin = null, string? check = null)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (ClassroomModeWorkspaceHost.Visibility == Visibility.Visible) { RestoreWindow(); return; }
        if (!ConfirmLeaveWorkspaceCalibration("课堂模式")) return;
        bool keepCalibration = WorkspaceCalibration.Visibility == Visibility.Visible ||
            ExamAwareWorkspaceHost.Visibility == Visibility.Visible || SecRandomWorkspaceHost.Visibility == Visibility.Visible;
        if (anchor is null && WorkspaceCalibration.IsKeyboardFocusWithin) anchor = Keyboard.FocusedElement as FrameworkElement;
        var previousPage = origin ?? CaptureWorkspaceOrigin();
        SelectPageCore(false, keepCalibration);
        _classroomModeReturn = returnTo is null ? previousPage : () => { previousPage(); returnTo(); };
        _classroomModeReturnFocus = anchor;
        if (_classroomModeWindow is null)
        {
            _classroomModeWindow = new ClassroomModeWindow(_pipe,
                () => OpenClassroomSettings(ShowClassIslandPathSettings, "SetupClassIslandButton"),
                () => OpenClassroomSettings(() => AdminPanelClicked(this, new RoutedEventArgs()), "SetupAdminButton"),
                OpenClassroomExamAware);
            _classroomModeWindow.SetWorkspaceReturn(ReturnFromClassroomMode);
            ClassroomModeWorkspaceHost.Content = new WorkspacePanel(_classroomModeWindow, this, _classroomModeWindow.WorkspaceVisibilityChanged);
            Closed += (_, _) => _classroomModeWindow.Shutdown();
        }
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        ClassroomModeNav.Tag = "active";
        PageBreadcrumb.Text = "课堂模式";
        ClassroomModeWorkspaceHost.Visibility = Visibility.Visible;
        RestoreWindow();
        _classroomModeWindow.FocusWorkspaceEntry(check);
    }

    private Action CaptureClassroomModeReturn(string? check = null)
    {
        var origin = _classroomModeReturn;
        var anchor = _classroomModeReturnFocus;
        return () => ShowClassroomModeFrom(anchor, origin: origin, check: check);
    }

    private void OpenClassroomSettings(Action open, string check)
    {
        var returnTo = CaptureClassroomModeReturn(check);
        open();
        _classroomSettingsReturn = returnTo;
        UpdateSettingsNavigation();
    }

    private void OpenClassroomExamAware()
    {
        const string check = "SetupExamAwareButton";
        ShowExamAwareFrom(_classroomModeWindow?.FindName(check) as FrameworkElement, origin: CaptureClassroomModeReturn(check));
    }

    private void ReturnToClassroomCheckClicked(object sender, RoutedEventArgs e) => _classroomSettingsReturn?.Invoke();

    private void HideClassroomModeWorkspace()
    {
        ClassroomModeWorkspaceHost.Visibility = Visibility.Collapsed;
        ClassroomModeNav.Tag = null;
        _classroomModeReturn = null;
        _classroomModeReturnFocus = null;
        _classroomSettingsReturn = null;
    }

    private void ReturnFromClassroomMode()
    {
        var anchor = _classroomModeReturnFocus;
        if (_classroomModeReturn is { } returnTo) returnTo();
        else SelectPage(false);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsActive || ClassroomModeWorkspaceHost.IsVisible) return;
            var target = anchor is { IsVisible: true, IsEnabled: true } ? anchor : ClassroomModeNav;
            target.BringIntoView();
            target.Focus();
        });
    }
    private async Task ClassroomModePollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            ClassroomModeState? state = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                state = (await HostClient.RequestAsync(_pipe, "classroom.status", deadline.Token)).ClassroomMode;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException) { }
            // A late reply must never restore a stale mode after the Host stopped.
            ShowClassroomModeState(_lifetime.IsCancellationRequested ? null : state);
            try { await Task.Delay(1500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private void ShowClassroomModeState(ClassroomModeState? state)
    {
        var view = ClassroomModePresentation.From(state);
        if (view.Attention && !_homeModeAttention) HomeClassroomModeDetails.IsExpanded = true;
        _homeModeAttention = view.Attention;
        ClassroomModeTitle.Text = view.Title;
        ConnectionsModeTitle.Text = view.Title;
        ClassroomModeDetail.Text = view.Detail;
        ClassroomModeCard.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, view.Attention ? "TeacherWarningBrush" : "TeacherShellLineBrush");
        _quick?.UpdateClassroomMode(view);
    }
}
