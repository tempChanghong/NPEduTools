using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class MainWindow
{
    private StudyHostState? _workspaceSchool;
    private ManualStudyState? _workspaceManual;
    private string _workspaceTab = "Manual";
    private string _homeStudyTarget = "Manual";
    private string _workspaceDevicesOrigin = "Home";
    private double _workspaceDeviceReportOffset;
    private Window? _workspaceCalibrationController;
    private SoundMeterDevice? _usbPreviousMeter;
    private NoiseCalibrationWindow? _workspaceQuickCalibrationController;
    private NoiseReferenceWindow? _workspaceManualReferenceController;
    private StudySeatingUploadWindow? _workspaceSeatingController;
    private long _workspaceReportGeneration;
    private CancellationTokenSource? _workspaceReportRead;
    private ContentControl? _workspaceOpenReport;

    private void InitializeStudyWorkspace()
    {
        InitializeWorkspaceSidebar();
        Closed += (_, _) =>
        {
            CancelWorkspaceReports();
            _workspaceSeatingController?.Dismiss();
            _workspaceManualReferenceController?.Close();
            _workspaceQuickCalibrationController?.Close();
        };
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

    private void HideStudyWorkspace(bool keepCalibration)
    {
        CancelWorkspaceReports();
        StudyWorkspace.Visibility = WorkspaceCalibration.Visibility = WorkspaceSeating.Visibility =
            WorkspaceManualReference.Visibility = WorkspaceQuickCalibration.Visibility = WorkspaceDevices.Visibility = Visibility.Collapsed;
        StudyNav.Tag = null;
        if (!keepCalibration) CloseWorkspaceCalibration();
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
        if (tab == "School") EnsureSchoolWorkspace();
        RestoreWindow();
        if (tab == "Reports")
        {
            WorkspaceReportScroll.ScrollToTop();
            _ = RefreshWorkspaceReportsAsync();
        }
    }
    private void EnsureSchoolWorkspace()
    {
        if (_studyMonitoringWindow is not null) return;
        var school = new StudyMonitoringWindow(_pipe, () => ShowStudyWorkspace("Manual"))
        {
            OpenCalibration = ShowWorkspaceCalibration,
            OpenQuickCalibration = ShowWorkspaceQuickCalibration,
            OpenManualReference = ShowWorkspaceManualReference,
            OpenSeatingWorkspace = ShowWorkspaceSeating,
            OpenDevicesWorkspace = ShowWorkspaceDevices
        };
        _studyMonitoringWindow = school;
        WorkspaceDevicesHost.Content = school.TakeDevicesForWorkspace();
        SchoolWorkspaceHost.Content = new WorkspacePanel(school, this, school.WorkspaceVisibilityChanged);
    }
    private void ShowManualWorkspaceReport()
    {
        SelectWorkspaceReportDetails(null);
        ShowStudyWorkspace("Reports");
        FocusWorkspaceReport(WorkspaceManualReport);
    }

    private void FocusWorkspaceReport(ContentControl summary)
    {
        WorkspaceReportScroll.UpdateLayout();
        summary.BringIntoView(new Rect(0, 0, 1, 1));
        summary.Focus();
    }

    private void WorkspaceReportDetailsClicked(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button button) return;
        var summary = button.Tag switch
        {
            "CloseStudyReportDetails" => _workspaceOpenReport,
            "StudyReportDetails" when button.DataContext is StudyWorkspaceReport { HasData: true } report =>
                ReferenceEquals(WorkspaceSchoolReport.Content, report) ? WorkspaceSchoolReport :
                ReferenceEquals(WorkspaceManualReport.Content, report) ? WorkspaceManualReport : null,
            _ => null
        };
        if (summary is null) return;
        e.Handled = true;
        if (ReferenceEquals(_workspaceOpenReport, summary))
        {
            SelectWorkspaceReportDetails(null);
            FocusWorkspaceReport(summary);
            return;
        }
        SelectWorkspaceReportDetails(summary);
        WorkspaceReportDetails.Focus();
        RevealWorkspaceReportDetails();
    }

    private void SelectWorkspaceReportDetails(ContentControl? summary)
    {
        var previous = _workspaceOpenReport;
        _workspaceOpenReport = summary;
        if (previous?.Content is StudyWorkspaceReport { IsDetailsOpen: true } old)
            previous.Content = old with { IsDetailsOpen = false };
        if (summary?.Content is StudyWorkspaceReport { HasData: true } current)
            summary.Content = current with { IsDetailsOpen = true };
        UpdateWorkspaceReportDetails();
    }

    private void UpdateWorkspaceReportDetails()
    {
        var report = _workspaceOpenReport?.Content as StudyWorkspaceReport;
        WorkspaceReportDetails.Content = report is { HasData: true } ? report : null;
        WorkspaceReportDetails.Visibility = WorkspaceReportDetails.Content is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RevealWorkspaceReportDetails()
    {
        WorkspaceReportScroll.UpdateLayout();
        WorkspaceReportDetails.BringIntoView(new Rect(0, 0, 1, Math.Min(WorkspaceReportDetails.ActualHeight, 100)));
    }

    private void WorkspaceDevicesClicked(object sender, RoutedEventArgs e) => ShowWorkspaceDevices();
    private void ShowWorkspaceDevices()
    {
        if (WorkspaceDevices.IsVisible) return;
        _workspaceDevicesOrigin = StudyWorkspace.IsVisible ? _workspaceTab : "Home";
        _workspaceDeviceReportOffset = WorkspaceReportScroll.VerticalOffset;
        RestoreWorkspaceDevices();
    }

    private void RestoreWorkspaceDevices()
    {
        SelectPage(false);
        EnsureSchoolWorkspace();
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        StudyNav.Tag = "active";
        PageBreadcrumb.Text = "自习 / 麦克风与摄像头";
        WorkspaceDevicesBack.Content = _workspaceDevicesOrigin switch
        {
            "School" => "← 返回学校定时自习",
            "Manual" => "← 返回手动自习",
            "Reports" => "← 返回本机报告",
            _ => "← 返回主页"
        };
        WorkspaceDevices.Visibility = Visibility.Visible;
        WorkspaceDevicesScroll.ScrollToTop();
        RestoreWindow();
        WorkspaceDevicesBack.Focus();
    }

    private void ReturnFromDevicesClicked(object sender, RoutedEventArgs e)
    {
        string origin = _workspaceDevicesOrigin;
        if (origin == "Home") SelectPage(false);
        else ShowStudyWorkspace(origin);
        Dispatcher.InvokeAsync(() =>
        {
            var anchor = origin switch
            {
                "School" => _studyMonitoringWindow?.DeviceWorkspaceEntry,
                "Manual" => _noiseWindow?.ManualDeviceLink,
                "Reports" => ReportDevicesButton,
                _ => HomeStudyDevicesButton
            };
            if (anchor is not { IsVisible: true }) return;
            if (origin == "Reports") WorkspaceReportScroll.ScrollToVerticalOffset(_workspaceDeviceReportOffset);
            anchor.BringIntoView();
            anchor.Focus();
        }, DispatcherPriority.Input);
    }
    private void WorkspaceCalibrationClicked(object sender, RoutedEventArgs e) => ShowWorkspaceCalibration();
    private void ShowWorkspaceSeating()
    {
        if (WorkspaceSeating.Visibility == Visibility.Visible) return;
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        StudyNav.Tag = "active";
        PageBreadcrumb.Text = "自习 / 自习座位表";
        if (_workspaceSeatingController is null)
        {
            var controller = new StudySeatingUploadWindow(_pipe)
            {
                ReturnToSchool = ReturnFromSeating,
                WriteStateChanged = writing => WorkspaceSeatingBack.IsEnabled = !writing
            };
            _workspaceSeatingController = controller;
            WorkspaceSeatingHost.Content = new WorkspacePanel(controller, this, controller.WorkspaceVisibilityChanged);
        }
        WorkspaceSeating.Visibility = Visibility.Visible;
        RestoreWindow();
        WorkspaceSeatingBack.Focus();
    }

    private void ReturnFromSeatingClicked(object sender, RoutedEventArgs e) => ReturnFromSeating();
    private void ReturnFromSeating()
    {
        if (_workspaceSeatingController?.IsWriting == true) return;
        ShowStudyWorkspace("School");
        Dispatcher.InvokeAsync(() =>
        {
            if (SchoolWorkspaceHost.IsVisible && _studyMonitoringWindow?.SchoolSeatingButton is { IsVisible: true } button)
            {
                button.BringIntoView();
                button.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void ShowWorkspaceManualReference()
    {
        if (WorkspaceManualReference.Visibility == Visibility.Visible) return;
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        StudyNav.Tag = "active";
        PageBreadcrumb.Text = "自习 / 手动声级计参考";
        if (_workspaceManualReferenceController is null)
        {
            var controller = new NoiseReferenceWindow(_pipe);
            controller.EnableManualWorkspace();
            _workspaceManualReferenceController = controller;
            WorkspaceManualReferenceHost.Content = new WorkspacePanel(controller, this, controller.ManualWorkspaceVisibilityChanged);
        }
        WorkspaceManualReference.Visibility = Visibility.Visible;
        RestoreWindow();
        WorkspaceManualReferenceBack.Focus();
    }

    private void ReturnFromManualReferenceClicked(object sender, RoutedEventArgs e)
    {
        ShowStudyWorkspace("School");
        Dispatcher.InvokeAsync(() =>
        {
            if (SchoolWorkspaceHost.IsVisible && _studyMonitoringWindow?.ManualReferenceButton is { IsVisible: true } button)
            {
                button.BringIntoView();
                button.Focus();
            }
        }, DispatcherPriority.Input);
    }

    private void ShowWorkspaceCalibration()
    {
        if (WorkspaceCalibration.IsVisible) { RestoreWindow(); return; }
        SelectPage(false); HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null; StudyNav.Tag = "active"; PageBreadcrumb.Text = "自习 / 联机校准 (Beta)";
        WorkspaceCalibration.Visibility = Visibility.Visible;
        WorkspaceUsbBack.Content = _workspaceTab switch
        {
            "School" => "← 返回学校定时自习",
            "Reports" => "← 返回自习报告",
            _ => "← 返回手动自习"
        };
        ShowUsbMeterPicker();
        RestoreWindow();
        WorkspaceUsbBack.Focus();
    }

    private void ShowUsbMeterPicker()
    {
        var meter = new SoundMeterWindow(_pipe) { OpenWorkspaceReference = ShowWorkspaceReference };
        meter.UseUsbWorkspace(_usbPreviousMeter, RefreshUsbWorkspace);
        SetWorkspaceCalibration(meter);
        RefreshUsbWorkspace();
    }

    private void ShowWorkspaceQuickCalibration()
    {
        if (WorkspaceQuickCalibration.Visibility == Visibility.Visible) return;
        SelectPage(false);
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null;
        StudyNav.Tag = "active";
        PageBreadcrumb.Text = "自习 / 快速校准";
        if (_workspaceQuickCalibrationController is null)
        {
            var controller = new NoiseCalibrationWindow(_pipe);
            _workspaceQuickCalibrationController = controller;
            WorkspaceQuickCalibrationHost.Content = new WorkspacePanel(controller, this, controller.WorkspaceVisibilityChanged);
        }
        WorkspaceQuickCalibration.Visibility = Visibility.Visible;
        RestoreWindow();
        WorkspaceQuickCalibrationBack.Focus();
    }

    private void ReturnFromQuickCalibrationClicked(object sender, RoutedEventArgs e)
    {
        ShowStudyWorkspace("School");
        Dispatcher.InvokeAsync(() =>
        {
            if (SchoolWorkspaceHost.IsVisible && _studyMonitoringWindow?.QuickCalibrationButton is { IsVisible: true } button)
            {
                button.BringIntoView();
                button.Focus();
            }
        }, DispatcherPriority.Input);
    }
    private void RestoreWorkspaceCalibration()
    {
        SelectPageCore(false, keepCalibration: true);
        HomePage.Visibility = Visibility.Collapsed;
        HomeNav.Tag = null; StudyNav.Tag = "active"; PageBreadcrumb.Text = "自习 / 联机校准 (Beta)";
        WorkspaceCalibration.Visibility = Visibility.Visible;
    }
    private void ShowWorkspaceReference(NoiseReferenceWindow reference, SoundMeterDevice meter)
    {
        _usbPreviousMeter = meter;
        reference.UseUsbWorkspace(meter, RefreshUsbWorkspace);
        SetWorkspaceCalibration(reference);
        RefreshUsbWorkspace();
    }

    private void RefreshUsbWorkspace()
    {
        var reference = _workspaceCalibrationController as NoiseReferenceWindow;
        int active = reference?.UsbWorkspaceStep ?? 0;
        WorkspaceUsbConnection.Content = reference?.UsbNeedsMeterCheck == true ? "重新查找仪表" :
            reference is null ? "连接仪表" : "更换或重查仪表";
        WorkspaceUsbConnection.IsEnabled = reference is null || reference.CanChangeUsbMeter;
        WorkspaceUsbSampling.IsEnabled = reference?.CanShowUsbSampling == true;
        WorkspaceUsbReview.IsEnabled = reference?.CanShowUsbReview == true;
        WorkspaceUsbPrepare.Visibility = reference is { UsbWorkspaceStep: > 0 } &&
            reference.CanShowUsbPreparation ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceUsbContext.Text = _workspaceCalibrationController switch
        {
            SoundMeterWindow picker => picker.UsbWorkspaceContext,
            NoiseReferenceWindow page => page.UsbWorkspaceContext,
            _ => ""
        };
        var tabs = new[] { WorkspaceUsbConnection, WorkspaceUsbSampling, WorkspaceUsbReview };
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].Tag = i == active ? "active" : null;
            System.Windows.Automation.AutomationProperties.SetItemStatus(tabs[i],
                i == active ? "当前步骤" : i < active ? "已进入后续步骤" : "后续步骤");
        }
    }

    private void UsbConnectionClicked(object sender, RoutedEventArgs e)
    {
        if (_workspaceCalibrationController is not NoiseReferenceWindow { CanChangeUsbMeter: true }) return;
        ShowUsbMeterPicker();
        if (_workspaceCalibrationController is SoundMeterWindow meter) meter.FocusUsbCredential();
    }
    private void UsbPreparationClicked(object sender, RoutedEventArgs e) =>
        (_workspaceCalibrationController as NoiseReferenceWindow)?.ShowUsbPreparation();
    private void UsbSamplingClicked(object sender, RoutedEventArgs e) =>
        (_workspaceCalibrationController as NoiseReferenceWindow)?.ShowUsbSampling();
    private void UsbReviewClicked(object sender, RoutedEventArgs e) =>
        (_workspaceCalibrationController as NoiseReferenceWindow)?.ShowUsbReview();
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
    private void ReturnFromCalibrationClicked(object sender, RoutedEventArgs e)
    {
        string origin = _workspaceTab;
        ShowStudyWorkspace(origin);
        Dispatcher.InvokeAsync(() =>
        {
            var source = origin switch
            {
                "School" => _studyMonitoringWindow?.OpenMeterCalibration,
                "Reports" => ReportCalibrationButton,
                _ => _noiseWindow?.ManualCalibrationLink
            };
            if (source is not { IsVisible: true }) return;
            source.BringIntoView();
            source.Focus();
        }, DispatcherPriority.Input);
    }
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
            if (ReferenceEquals(_workspaceOpenReport, target)) UpdateWorkspaceReportDetails();
            StudyWorkspaceReport report;
            try { report = StudyWorkspaceReport.FromResponse(title, await HostClient.RequestAsync(_pipe, capability, deadline.Token)); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
            { report = StudyWorkspaceReport.Failed(title); }
            if (!_lifetime.IsCancellationRequested && generation == _workspaceReportGeneration)
            {
                // Only compensate for a growing school row if the user is still reading the manual report.
                ContentControl? anchor = null;
                if (target == WorkspaceSchoolReport)
                    anchor = _workspaceOpenReport == WorkspaceManualReport && WorkspaceReportDetails.IsKeyboardFocusWithin
                        ? WorkspaceReportDetails : WorkspaceManualReport.IsKeyboardFocusWithin ? WorkspaceManualReport : null;
                double top = anchor?.TranslatePoint(new Point(), WorkspaceReportScroll).Y ?? 0;
                bool keepPosition = anchor is not null && top + anchor.ActualHeight > 0 && top < WorkspaceReportScroll.ViewportHeight;
                bool selected = ReferenceEquals(_workspaceOpenReport, target);
                target.Content = selected && report.HasData ? report with { IsDetailsOpen = true } : report;
                if (selected) UpdateWorkspaceReportDetails();
                if (keepPosition)
                {
                    WorkspaceReportScroll.UpdateLayout();
                    double newTop = anchor!.TranslatePoint(new Point(), WorkspaceReportScroll).Y;
                    WorkspaceReportScroll.ScrollToVerticalOffset(WorkspaceReportScroll.VerticalOffset + newTop - top);
                }
            }
        }
    }
}
