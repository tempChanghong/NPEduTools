using System.Windows;

namespace NPEduTools.App;

public partial class MainWindow
{
    private void UpdateStudyHome(NPEduTools.Contracts.StudyHostState? state)
    {
        _workspaceSchool = state;
        StudyHomeNext.Text = StudySchedulePresentation.Next(state);
        StudyHomeStatus.Text = state is null ? "后台状态待确认" : state.PolicyState != "APPLIED" ? "学校安排尚未应用" :
            state is { ClockReady: true, Phase: "Active", Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } } ? "自习进行中 · 新鲜采样已确认" :
            state.Phase == "Preparing" ? "准备自习 · 尚未开始采集" : state.Phase == "Active" ? "自习时段内 · 采集待确认" : "查看学校安排与设备准备";
        RefreshWorkspaceHome();
    }
    private NoiseWindow? _noiseWindow;
    private StudyMonitoringWindow? _studyMonitoringWindow;
    private void OpenStudyMonitoringClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("School");
    private void OpenNoiseClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Manual");
}
