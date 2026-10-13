using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void UpdateStudyHome(NPEduTools.Contracts.StudyHostState? state)
    {
        _workspaceSchool = state;
        StudyHomeNext.Text = StudySchedulePresentation.Next(state);
        RefreshWorkspaceHome();
    }
    private NoiseWindow? _noiseWindow;
    private StudyMonitoringWindow? _studyMonitoringWindow;
    private void OpenStudyMonitoringClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("School");
    private void OpenNoiseClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Manual");
}
