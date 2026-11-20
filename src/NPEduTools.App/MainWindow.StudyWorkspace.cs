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
    private string _homeStudyTarget = "Manual";
    private Window? _workspaceCalibrationController;
    private long _workspaceReportGeneration;
    private CancellationTokenSource? _workspaceReportRead;

    private void InitializeStudyWorkspace()
    {
        InitializeWorkspaceSidebar();
        Closed += (_, _) => CancelWorkspaceReports();
    }
    private void RefreshWorkspaceHome()
    {
        bool school = _workspaceSchool is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active" or "Preparing" };
        bool manual = _workspaceManual?.State is "Waiting" or "Preparing" or "Starting" or "Active" or "Stopping";
        school |= !manual && (_workspaceSchool is { Phase: "Active" or "Preparing" } or { Execution: { Owner: "Schedule", State: "Active" } });
        _homeStudyTarget = school ? "School" : "Manual";
        WorkspaceTaskTitle.Text = school ? _workspaceSchool!.PolicyState != "APPLIED" || !_workspaceSchool.ClockReady ? "学校自习状态待确认" : (_workspaceSchool!.Phase == "Active" ? "学校自习时段" : "学校自习即将开始") :
            manual ? _workspaceManual!.State switch { "Waiting" => "已有手动自习预约", "Preparing" => "手动自习即将开始", "Starting" => "手动自习启动中", "Stopping" => "正在结束手动自习", _ => "手动自习进行中" } :
            _workspaceSchool is null || _workspaceManual is null ? "自习状态待确认" : "当前没有自习任务";
        WorkspacePrimary.Content = school ? "查看学校自习" : manual ? "管理手动自习" :
            _workspaceSchool is null || _workspaceManual is null ? "检查自习状态" : "开始自习";
        StudyHomeStatus.Text = school ? _workspaceSchool!.PolicyState != "APPLIED" ? "学校安排尚未应用，采集状态请在自习页核对" : !_workspaceSchool.ClockReady ? "学校时间待确认，采集状态请在自习页核对" : _workspaceSchool.Phase == "Preparing" ? "准备自习，到点后才开始采集" :
            _workspaceSchool is { Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } } ? "自习进行中 · 新鲜采样已确认" : "自习时段内 · 采集待确认" :
            manual ? _workspaceManual!.State switch
            {
                "Waiting" => _workspaceManual.StartUtc is { } start ? $"预约 {start.ToLocalTime():yyyy-MM-dd HH:mm} · Windows 时间" : "手动预约时间待确认",
                "Preparing" => "准备手动自习，到点后才开始采集",
                "Starting" => "正在启动手动会话，等待后台确认",
                "Stopping" => "正在结束手动会话，等待后台确认",
                _ => "手动会话已由后台确认；采样详情请在自习页查看"
            } : _workspaceSchool is null || _workspaceManual is null ? "状态尚未确认，不能据此判断后台任务已经停止" : "可立即开始手动自习，或查看学校定时安排";
        _quick?.UpdateStudy(WorkspaceTaskTitle.Text, StudyHomeStatus.Text, StudyHomeNext.Text,
            WorkspacePrimary.Content?.ToString() ?? "查看自习", school || manual);

    }
    private void UpdateWorkspaceManual(ManualStudyState? state)
    { _workspaceManual = state; RefreshWorkspaceHome(); }
    private void UpdateWorkspaceReadiness(NoiseState? noise, StudySettings? settings, NoiseCalibrationStatus? calibration = null)
    {
        WorkspaceReadiness.Text = noise is null ? "麦克风状态待确认" :
            string.IsNullOrWhiteSpace(noise.SelectedDeviceId ?? noise.DeviceId) ? "尚未选择监测麦克风" : "监测麦克风已选择（可用性请在设备页刷新核对）";
        WorkspaceReadiness.Text += settings is null ? " · 摄像头策略待确认" : settings.Photos.Enabled ? " · 摄像头捕获策略已开启" : " · 摄像头捕获策略关闭";
        var label = NoiseCalibrationPresentation.Describe(calibration, noise);
        WorkspaceReference.Text = label.Text + (noise?.Estimate is { } estimate
            ? " · " + NoiseSignalPresentation.EstimateReason(estimate) : " · 原始电平 dBFS");
        WorkspaceReference.ToolTip = label.Detail;
    }
    private void WorkspacePrimaryClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace(_homeStudyTarget);
    private void StudyNavClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace(_workspaceTab);
    private void ManualWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Manual");
    private void SchoolWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("School");
    private void ReportWorkspaceClicked(object sender, RoutedEventArgs e) => ShowStudyWorkspace("Reports");

    private void HideStudyWorkspace()
    {
        CancelWorkspaceReports();
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
            _noiseWindow = new NoiseWindow(_pipe)
            {
                OpenWorkspaceDevices = ShowWorkspaceDevices,
                OpenWorkspaceCalibration = ShowWorkspaceCalibration,
                OpenWorkspaceReport = ShowManualWorkspaceReport
            };
            ManualWorkspaceHost.Content = new WorkspacePanel(_noiseWindow, this, _noiseWindow.WorkspaceVisibilityChanged);
        }
        if (tab == "School" && _studyMonitoringWindow is null)
        {
            _studyMonitoringWindow = new StudyMonitoringWindow(_pipe, () => ShowStudyWorkspace("Manual"))
            { OpenCalibration = ShowWorkspaceCalibration };
            SchoolWorkspaceHost.Content = new WorkspacePanel(_studyMonitoringWindow, this, _studyMonitoringWindow.WorkspaceVisibilityChanged);
        }
        RestoreWindow();
        if (tab == "Reports")
        {
            WorkspaceReportScroll.ScrollToTop();
            _ = RefreshWorkspaceReportsAsync();
        }
    }
    private void ShowManualWorkspaceReport()
    {
        ShowStudyWorkspace("Reports");
        BringManualReportIntoView();
        WorkspaceManualReport.Focus();
    }
    private void BringManualReportIntoView()
    {
        WorkspaceReportScroll.UpdateLayout();
        WorkspaceManualReport.BringIntoView(new Rect(0, 0, 1, 1));
    }
    private bool IsManualReportHeadingInView()
    {
        double top = WorkspaceManualReport.TranslatePoint(new Point(), WorkspaceReportScroll).Y;
        return top >= -1 && top < WorkspaceReportScroll.ActualHeight;
    }
    private void WorkspaceDevicesClicked(object sender, RoutedEventArgs e) => ShowWorkspaceDevices();
    private void ShowWorkspaceDevices()
    {
        ShowStudyWorkspace("School");
        if (_studyMonitoringWindow is not { } school) return;
        school.DeviceSettings.IsExpanded = true;
        SchoolWorkspaceHost.UpdateLayout();
        school.Devices.BringIntoView(new Rect(0, 0, 1, 1));
    }
    private void WorkspaceCalibrationClicked(object sender, RoutedEventArgs e) => ShowWorkspaceCalibration();
    private void ShowWorkspaceCalibration()
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
    private void CancelWorkspaceReports()
    {
        _workspaceReportGeneration++;
        _workspaceReportRead?.Cancel();
        _workspaceReportRead = null; // The owning async operation disposes it after settling.
    }
    private async Task RefreshWorkspaceReportsAsync()
    {
        CancelWorkspaceReports();
        long generation = _workspaceReportGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _workspaceReportRead = deadline;
        deadline.CancelAfter(5000);
        try
        {
            // Each source owns its feedback. A broken or slow reply must not
            // hide the other report, and both requests share navigation cancellation.
            await Task.WhenAll(ReadReportAsync("study.report.status", "学校定时自习", WorkspaceSchoolReport),
                ReadReportAsync("manual.study.report.status", "手动自习", WorkspaceManualReport));
        }
        finally { if (ReferenceEquals(_workspaceReportRead, deadline)) _workspaceReportRead = null; }

        async Task ReadReportAsync(string capability, string title, ContentControl target)
        {
            target.Content = StudyWorkspaceReport.Loading(title);
            StudyWorkspaceReport report;
            try { report = StudyWorkspaceReport.FromResponse(title, await HostClient.RequestAsync(_pipe, capability, deadline.Token)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
            { report = StudyWorkspaceReport.Failed(title); }
            if (!_lifetime.IsCancellationRequested && generation == _workspaceReportGeneration)
            {
                // A late school card can grow above the manual heading. Keep it in view
                // only while the teacher is still reading there.
                bool keepHeading = target == WorkspaceSchoolReport && WorkspaceManualReport.IsKeyboardFocused && IsManualReportHeadingInView();
                target.Content = report;
                if (keepHeading) BringManualReportIntoView();
            }
        }
    }
}
