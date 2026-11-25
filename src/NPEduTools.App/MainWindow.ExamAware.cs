using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ExamAwareWindow? _examAwareWindow;
    private Action? _examAwareReturn;
    private FrameworkElement? _examAwareReturnFocus;
    private bool _examAwareMinimized;

    private void OpenExamAwareClicked(object sender, RoutedEventArgs e) => ShowExamAwareFrom(sender as FrameworkElement);
    private void ShowExamAware() => ShowExamAwareFrom();

    private void ShowExamAwareFrom(FrameworkElement? anchor = null, Action? returnTo = null)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (ExamAwareWorkspaceHost.Visibility == Visibility.Visible)
        {
            RestoreWindow();
            return;
        }

        bool keepCalibration = WorkspaceCalibration.Visibility == Visibility.Visible;
        if (keepCalibration && _workspaceCalibrationController is NoiseReferenceWindow { HasActiveSample: true })
        {
            RestoreWindow();
            if (MessageBox.Show(this, "当前校准仍在进行。切换后可返回查看这次采样，自动续采可能暂停。现在切到考试看板吗？",
                "校准进行中", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        var previousPage = ExamAwareOrigin();
        if (anchor is null && keepCalibration && WorkspaceCalibration.IsKeyboardFocusWithin)
            anchor = Keyboard.FocusedElement as FrameworkElement;
        SelectPageCore(false, keepCalibration);
        _examAwareReturn = returnTo is null ? previousPage : () => { previousPage(); returnTo(); };
        _examAwareReturnFocus = anchor;
        if (_examAwareWindow is null)
        {
            _examAwareWindow = new ExamAwareWindow(_pipe);
            _examAwareWindow.SetWorkspaceReturn(ReturnFromExamAware);
            ExamAwareWorkspaceHost.Content = new WorkspacePanel(_examAwareWindow, this, _examAwareWindow.WorkspaceVisibilityChanged);
        }
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        ExamAwareNav.Tag = "active";
        PageBreadcrumb.Text = "ExamAware2 连接";
        ExamAwareWorkspaceHost.Visibility = Visibility.Visible;
        RestoreWindow();
        _examAwareWindow.FocusWorkspaceEntry();
    }

    private Action ExamAwareOrigin()
    {
        if (SettingsPage.Visibility == Visibility.Visible)
        {
            string category = ConnectionsSettingsPanel.Visibility == Visibility.Visible ? "Connections" :
                TeachingSettingsPanel.Visibility == Visibility.Visible ? "Teaching" :
                NpepSettingsPanel.Visibility == Visibility.Visible ? "Npep" : "General";
            double offset = SettingsScroll.VerticalOffset;
            return () => { ShowSettingsCategory(category); SettingsScroll.ScrollToVerticalOffset(offset); };
        }
        if (WorkspaceCalibration.Visibility == Visibility.Visible) return RestoreWorkspaceCalibration;
        if (StudyWorkspace.Visibility == Visibility.Visible)
        {
            string tab = _workspaceTab;
            return () => ShowStudyWorkspace(tab);
        }
        if (RecordingWorkspace.Visibility == Visibility.Visible)
        {
            string tab = _recordingTab;
            return () => ShowRecordingWorkspace(tab);
        }
        if (ShortcutPage.Visibility == Visibility.Visible) return ShowShortcutManager;
        if (AboutPageView.Visibility == Visibility.Visible) return () => AboutNavClicked(AboutNav, new RoutedEventArgs());
        return () => SelectPage(false);
    }

    private void HideExamAwareWorkspace()
    {
        ExamAwareWorkspaceHost.Visibility = Visibility.Collapsed;
        ExamAwareNav.Tag = null;
        _examAwareReturn = null;
        _examAwareReturnFocus = null;
    }

    private void ReturnFromExamAware()
    {
        var previousPage = _examAwareReturn;
        var anchor = _examAwareReturnFocus;
        if (previousPage is null) SelectPage(false);
        else previousPage();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsActive || ExamAwareWorkspaceHost.IsVisible) return;
            var target = anchor is { IsVisible: true, IsEnabled: true } ? anchor : ExamAwareNav;
            target.BringIntoView();
            target.Focus();
        });
    }

    private void UpdateExamAwareWindowState()
    {
        bool minimized = WindowState == WindowState.Minimized;
        if (minimized == _examAwareMinimized) return;
        _examAwareMinimized = minimized;
        if (ExamAwareWorkspaceHost.IsVisible) _examAwareWindow?.WorkspaceVisibilityChanged(!minimized);
    }

    private async void OpenExamAwareQuick()
    {
        _quick?.Collapse(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var result = await HostClient.RequestAsync(_pipe, "examaware.start", deadline.Token);
            if (result.Outcome != "Accepted") ShowExamAware();
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) ShowExamAware(); }
    }
}
