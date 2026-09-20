using System.Windows;
using System.IO;
using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ClassroomModeWindow? _classroomModeWindow;
    private void OpenClassroomModeClicked(object sender, RoutedEventArgs e) => ShowClassroomMode();
    private void ShowClassroomMode()
    {
        _quick?.Collapse(false);
        if (_classroomModeWindow is null)
        {
            _classroomModeWindow = new ClassroomModeWindow(_pipe,
                ShowClassIslandPathSettings,
                () => AdminPanelClicked(this, new RoutedEventArgs()), ShowExamAware);
            Closed += (_, _) => _classroomModeWindow.Shutdown();
        }
        _classroomModeWindow.Show();
        if (_classroomModeWindow.WindowState == WindowState.Minimized) _classroomModeWindow.WindowState = WindowState.Normal;
        _classroomModeWindow.Activate();
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
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException) { }
            // A late reply must never restore a stale mode after the Host stopped.
            ShowClassroomModeState(_lifetime.IsCancellationRequested ? null : state);
            try { await Task.Delay(1500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }

    private void ShowClassroomModeState(ClassroomModeState? state)
    {
        var view = ClassroomModePresentation.From(state);
        ClassroomModeTitle.Text = view.Title;
        ClassroomModeDetail.Text = view.Detail;
        ClassroomModeCard.Background = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(view.Attention ? "#FFF5E6" : "#EAF6F2"));
        ClassroomModeCard.BorderBrush = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(view.Attention ? "#E8D3AD" : "#C8E4D9"));
        _quick?.UpdateClassroomMode(view);
    }
}
