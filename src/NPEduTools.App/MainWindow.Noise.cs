using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void UpdateStudyHome(NPEduTools.Contracts.StudyHostState? state)
    {
        StudyHomeNext.Text = StudySchedulePresentation.Next(state);
        StudyHomeStatus.Text = state is null ? "后台状态待确认" : state.PolicyState != "APPLIED" ? "学校安排尚未应用" :
            state is { ClockReady: true, Phase: "Active", Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } } ? "自习进行中 · 新鲜采样已确认" :
            state.Phase == "Preparing" ? "准备自习 · 尚未开始采集" : state.Phase == "Active" ? "自习时段内 · 采集待确认" : "查看学校安排与设备准备";
    }
    private NoiseWindow? _noiseWindow;
    private StudyMonitoringWindow? _studyMonitoringWindow;
    private void OpenStudyMonitoringClicked(object sender, RoutedEventArgs e)
    {
        _quick?.Collapse(false);
        if (_studyMonitoringWindow is null)
        {
            _studyMonitoringWindow = new(_pipe, () => OpenNoiseClicked(this, new RoutedEventArgs()));
            _studyMonitoringWindow.Closed += (_, _) => _studyMonitoringWindow = null;
        }
        _studyMonitoringWindow.Show();
        if (_studyMonitoringWindow.WindowState == WindowState.Minimized) _studyMonitoringWindow.WindowState = WindowState.Normal;
        _studyMonitoringWindow.Activate();
    }
    private void OpenNoiseClicked(object sender, RoutedEventArgs e)
    {
        _quick?.Collapse(false);
        if (_noiseWindow is null)
        {
            _noiseWindow = new NoiseWindow(_pipe);
            Closed += (_, _) => _noiseWindow.Shutdown();
        }
        _noiseWindow.Show();
        if (_noiseWindow.WindowState == WindowState.Minimized) _noiseWindow.WindowState = WindowState.Normal;
        _noiseWindow.Activate();
    }
}
