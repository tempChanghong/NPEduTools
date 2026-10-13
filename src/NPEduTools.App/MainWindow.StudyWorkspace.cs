using System.IO;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private StudyHostState? _workspaceSchool;
    private ManualStudyState? _workspaceManual;
    private string _workspaceTab = "Manual";
    private Window? _workspaceCalibrationController;
    private long _workspaceReportGeneration;

    private void InitializeStudyWorkspace()
    {
        InitializeWorkspaceSidebar();
        Closed += (_, _) => { _workspaceReportGeneration++; };
    }
    private void RefreshWorkspaceHome()
    {
        bool school = _workspaceSchool is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active" or "Preparing" };
        bool manual = _workspaceManual?.State is "Waiting" or "Preparing" or "Starting" or "Active" or "Stopping";
        WorkspaceTaskTitle.Text = school ? (_workspaceSchool!.Phase == "Active" ? "学校自习时段" : "学校自习即将开始") :
            manual ? (_workspaceManual!.State is "Waiting" or "Preparing" ? "已有手动自习预约" : "手动自习会话") :
            _workspaceSchool is null || _workspaceManual is null ? "后台状态待确认" : "当前没有自习任务";
        WorkspacePrimary.Content = school ? "查看学校自习" : manual ? "管理手动自习" : "进入自习";
    }
    private void UpdateWorkspaceManual(ManualStudyState? state)
    { _workspaceManual = state; RefreshWorkspaceHome(); }
    private void UpdateWorkspaceReadiness(NoiseState? noise, StudySettings? settings)
    {
        WorkspaceReadiness.Text = noise is null ? "麦克风状态待确认" :
            string.IsNullOrWhiteSpace(noise.SelectedDeviceId ?? noise.DeviceId) ? "尚未选择监测麦克风" : "监测麦克风已选择（可用性请在设备页刷新核对）";
        WorkspaceReadiness.Text += settings is null ? " · 摄像头策略待确认" : settings.Photos.Enabled ? " · 摄像头捕获策略已开启" : " · 摄像头捕获策略关闭";
        WorkspaceReference.Text = noise?.Estimate is { } estimate
            ? "原始 dBFS 与估算 dB(A) 分别展示 · " + NoiseSignalPresentation.EstimateReason(estimate)
            : "原始电平为 dBFS；估算参考是否可用请在校准页核对。";
    }
    private void WorkspacePrimaryClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace(
        _workspaceSchool is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active" or "Preparing" } ? "School" : "Manual");
    private void StudyNavClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace(_workspaceTab);
    private void ManualWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Manual");
    private void SchoolWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("School");
    private void ReportWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Reports");

    private void HideStudyWorkspace()
    {
        _workspaceReportGeneration++;
        StudyWorkspace.Visibility = WorkspaceCalibration.Visibility = Visibility.Collapsed;
        StudyNav.Tag = null;
        CloseWorkspaceCalibration();
    }
    private void ShowStudyWorkspace(string tab)
    {
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed;
        StudyWorkspace.Visibility = Visibility.Visible;
        HomeNav.Tag = null; StudyNav.Tag = "active"; PageBreadcrumb.Text = "自习";
        _workspaceTab = tab;
        ManualWorkspaceTab.Tag = tab == "Manual" ? "active" : null;
        SchoolWorkspaceTab.Tag = tab == "School" ? "active" : null;
        ReportWorkspaceTab.Tag = tab == "Reports" ? "active" : null;
        ManualWorkspaceHost.Visibility = tab == "Manual" ? Visibility.Visible : Visibility.Collapsed;
        SchoolWorkspaceHost.Visibility = tab == "School" ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceReports.Visibility = tab == "Reports" ? Visibility.Visible : Visibility.Collapsed;
        if (tab == "Manual" && _noiseWindow is null)
        {
            _noiseWindow = new NoiseWindow(_pipe);
            ManualWorkspaceHost.Content = new WorkspacePanel(_noiseWindow, this);
        }
        if (tab == "School" && _studyMonitoringWindow is null)
        {
            _studyMonitoringWindow = new StudyMonitoringWindow(_pipe, () => ShowStudyWorkspace("Manual"))
            { OpenCalibration = () => WorkspaceCalibrationClicked(this, new RoutedEventArgs()) };
            SchoolWorkspaceHost.Content = new WorkspacePanel(_studyMonitoringWindow, this);
        }
        RestoreWindow();
        if (tab == "Reports") _ = RefreshWorkspaceReportsAsync();
    }
    private void WorkspaceDevicesClicked(object sender, RoutedEventArgs e)
    {
        ShowStudyWorkspace("School");
        if (_studyMonitoringWindow?.FindName("DeviceSettings") is Expander devices) devices.IsExpanded = true;
        if (_studyMonitoringWindow?.FindName("Devices") is FrameworkElement picker) picker.BringIntoView();
    }
    private void WorkspaceCalibrationClicked(object sender, RoutedEventArgs e)
    {
        SelectPage(false); HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null; StudyNav.Tag = "active"; PageBreadcrumb.Text = "自习 / 联机校准 (Beta)";
        WorkspaceCalibration.Visibility = Visibility.Visible;
        var meter = new SoundMeterWindow(_pipe) { OpenWorkspaceReference = ShowWorkspaceReference };
        SetWorkspaceCalibration(meter);
        RestoreWindow();
    }
    private void ShowWorkspaceReference(NoiseReferenceWindow reference)
    { SetWorkspaceCalibration(reference); }
    private void SetWorkspaceCalibration(Window controller)
    {
        CloseWorkspaceCalibration();
        _workspaceCalibrationController = controller;
        WorkspaceCalibrationHost.Content = new WorkspacePanel(controller, this);
    }
    private void CloseWorkspaceCalibration()
    {
        var previous = _workspaceCalibrationController;
        _workspaceCalibrationController = null;
        // NoiseReferenceWindow's close handler cancels only its own sample/draft revision.
        // Never send a generic stop that could affect a school session.
        previous?.Close();
        WorkspaceCalibrationHost.Content = null;
    }
    private void ReturnFromCalibrationClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace(_workspaceTab);
    private async void RefreshWorkspaceReportsClicked(object sender, RoutedEventArgs e) => await RefreshWorkspaceReportsAsync();
    private async Task RefreshWorkspaceReportsAsync()
    {
        long generation = ++_workspaceReportGeneration;
        WorkspaceSchoolReport.Text = WorkspaceManualReport.Text = "正在读取本机最近报告…";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(5000);
        try
        {
            var school = await HostClient.RequestAsync(_pipe, "study.report.status", deadline.Token);
            var manual = await HostClient.RequestAsync(_pipe, "manual.study.report.status", deadline.Token);
            if (_lifetime.IsCancellationRequested || generation != _workspaceReportGeneration) return;
            WorkspaceSchoolReport.Text = ReportText(school);
            WorkspaceManualReport.Text = ReportText(manual);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (!_lifetime.IsCancellationRequested && generation == _workspaceReportGeneration)
                WorkspaceSchoolReport.Text = WorkspaceManualReport.Text = "报告状态未确认，请检查后台连接后刷新；不沿用旧结果。";
        }
    }
    private static string ReportText(HostResponse response)
    {
        if (response.Outcome != "Succeeded") return "后台未确认报告，请刷新或在学校后台查看历史报告。";
        if (response.StudyReport?.Data is not { } data) return "本机暂未返回报告；历史报告请在学校后台查看。";
        static string Number(double? value) => value is { } v ? v.ToString("F1") : "—";
        var text = $"{StudySchedulePresentation.Span(data.Window)}\n" +
            $"有效采样 {data.SampledSeconds:F0} 秒 · 覆盖率 {data.Coverage:P0} · 提醒 {data.DisciplineReminders} 次 · 削波 {data.ClippedPercent:F2}%\n" +
            $"dBFS：能量均值 {Number(data.Raw.EnergyMean)} · 最高值 {Number(data.Raw.Maximum)}\n" +
            $"原始阈值超限：中线 {data.Raw.MediumSeconds:F0} 秒 · 高线 {data.Raw.HighSeconds:F0} 秒";
        if (data.Estimated is { } estimate)
            text += $"\n估算 dB(A)：能量均值 {Number(estimate.EnergyMean)} · 最高值 {Number(estimate.Maximum)}\n" +
                $"估算阈值超限：中线 {estimate.MediumSeconds:F0} 秒 · 高线 {estimate.HighSeconds:F0} 秒";
        return text + $"\n时间来源：{data.ClockSource} · " + (data.Complete ? "会话完整" : "会话不完整") +
            (data.Notes.Count > 0 ? "\n" + string.Join("\n", data.Notes) : "");
    }
}
