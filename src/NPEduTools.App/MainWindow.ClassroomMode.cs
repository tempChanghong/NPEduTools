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
                () => { ShowSettings(); Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ExecutablePathBox.BringIntoView()); },
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
            var view = ClassroomModePresentation.From(state);
            ClassroomModeTitle.Text = view.Title;
            ClassroomModeDetail.Text = view.Detail;
            _quick?.UpdateClassroomMode(view);
            try { await Task.Delay(1500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
}
