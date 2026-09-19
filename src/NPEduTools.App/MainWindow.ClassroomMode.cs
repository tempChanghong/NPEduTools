using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private ClassroomModeWindow? _classroomModeWindow;
    private void OpenClassroomModeClicked(object sender, RoutedEventArgs e)
    {
        _quick?.Collapse(false);
        if (_classroomModeWindow is null)
        {
            _classroomModeWindow = new ClassroomModeWindow(_pipe);
            Closed += (_, _) => _classroomModeWindow.Shutdown();
        }
        _classroomModeWindow.Show();
        if (_classroomModeWindow.WindowState == WindowState.Minimized) _classroomModeWindow.WindowState = WindowState.Normal;
        _classroomModeWindow.Activate();
    }
}

