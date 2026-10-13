using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NPEduTools.UiPrototype;

public partial class WorkspaceWindow : Window
{
    internal readonly WorkspaceState State = new();
    private bool _ready, _compact, _schoolTab, _reportTab, _meterVerified, _sampling, _updatingScene;
    private string _page = "Home";
    private int _calibrationStep, _samplingTicks;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public WorkspaceWindow()
    {
        InitializeComponent();
        StartDate.SelectedDate = EndDate.SelectedDate = WorkspaceState.DemoNow.Date;
        EndChoice.SelectedIndex = Scene.SelectedIndex = Microphone.SelectedIndex = CalibrationMode.SelectedIndex = 0;
        _timer.Tick += (_, _) => AdvanceSampling();
        _ready = true;
        Loaded += (_, _) => { NPEduTools.App.DesktopWindowBounds.KeepVisible(this); Render(); };
        SizeChanged += (_, _) => { if (_ready) ResizeNavigation(); };
        Closed += (_, _) => _timer.Stop();
        Render();
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Tell(string message, bool error = false)
    {
        FeedbackText.Text = message;
        Feedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#FFF0EB" : "#EDF4EF"));
        Feedback.Visibility = Visibility.Visible;
    }

    private void Navigate(string page)
    {
        Feedback.Visibility = Visibility.Collapsed;
        if (_sampling && page != "Calibration") { AbortSampling(); Tell("演示采样已取消，未保存参考。"); }
        _page = page;
        HomePage.Visibility = Visible(page == "Home"); StudyPage.Visibility = Visible(page == "Study");
        DevicesPage.Visibility = Visible(page == "Devices"); CalibrationPage.Visibility = Visible(page == "Calibration");
        HomeNav.Tag = page == "Home" ? "active" : null;
        StudyNav.Tag = page == "Study" ? "active" : null;
        DevicesNav.Tag = page is "Devices" or "Calibration" ? "active" : null;
        PageTitle.Text = page switch { "Study" => "自习", "Devices" => "设备与连接", "Calibration" => "联机校准 (Beta)", _ => "主页" };
        PageSubtitle.Text = page switch { "Study" => "手动开始、学校安排和报告，在同一处管理。", "Devices" => "查看共享设备、校准参考和软件连接。", "Calibration" => "连接验证 → 同期采样 → 确认结果", _ => "今天需要做什么，一眼看清。" };
        Render();
    }

    private void Render()
    {
        if (!_ready) return;
        bool active = State.ManualState == "Active", scheduled = State.ManualState == "Scheduled";
        string microphone = Microphone.SelectedIndex == 1 ? "备用麦克风" : "教室麦克风";
        HomeTask.Text = State.SchoolActive ? "学校自习正在进行" : active ? "手动自习正在进行" : scheduled ? "手动自习已预约" : !State.ClockReady ? "学校时间需要检查" : "当前没有自习任务";
        HomeTaskHint.Text = State.SchoolActive ? "本次 19:15–21:30 · 噪音采集与展示分别核对" : active ? "本次从 18:40 开始 · 后台采集与展示分别核对" : scheduled ? $"{State.ManualStart:MM-dd HH:mm} 开始 · 到点才采集" : !State.ClockReady ? "当前没有可靠的学校时间，暂不显示下一次有效安排。" : "学校安排已同步；也可以开始一节手动自习。";
        HomePrimary.Content = State.SchoolActive ? "查看本次自习" : active || scheduled ? "管理手动自习" : !State.ClockReady ? "检查时间连接" : "开始手动自习";
        HomeNext.Text = State.ClockReady ? State.SchoolActive ? "明天 19:15–21:30" : "今天 19:15–21:30" : "学校时间未确认";
        HomeNextHint.Text = State.ClockReady ? "日常模式 · ClassIsland 学校时间 · 提前五分钟打开展示" : "先检查 ClassIsland 连接，再确认安排。";
        HomeReference.Text = State.ReferenceSaved ? "估算 dB(A) · 参考已确认（演示）" : "快速校准 · dBFS（演示）";
        HomeReadiness.Text = microphone + "已选择 · 摄像头策略关闭";
        ManualPane.Visibility = Visible(!_schoolTab && !_reportTab); SchoolPane.Visibility = Visible(_schoolTab && !_reportTab); ReportPane.Visibility = Visible(_reportTab);
        ManualTab.Style = (Style)FindResource(!_schoolTab && !_reportTab ? "Primary" : "Quiet");
        SchoolTab.Style = (Style)FindResource(_schoolTab && !_reportTab ? "Primary" : "Quiet");
        ReportsTab.Style = (Style)FindResource(_reportTab ? "Primary" : "Quiet");
        ManualForm.Visibility = Visible(!active && !scheduled); ManualRunning.Visibility = Visible(active || scheduled);
        ManualHeading.Text = active ? "手动自习进行中" : scheduled ? "手动自习已预约" : "开始一节手动自习";
        ManualExplanation.Text = State.SchoolActive ? "学校定时自习优先。本时段内不能再开始手动会话。" : active ? "收起展示后，后台仍继续监测。" : scheduled ? "预约已保存，到指定时间才采集。" : "使用已选择的设备，最多三小时。";
        ManualTimeSummary.Text = $"{State.ManualStart:MM-dd HH:mm} 开始" + (State.ManualEnd is {} end ? $" · {end:HH:mm} 结束" : " · 主动结束，最多三小时");
        ManualLive.Text = active ? State.ReferenceSaved ? "采集：示例 56.0 估算 dB(A)" : "采集：示例 −43.0 dBFS" : "采集尚未开始";
        StartFields.Visibility = Visible(Scheduled.IsChecked == true); EndFields.Visibility = Visible(EndChoice.SelectedIndex == 1);
        SchoolHeading.Text = State.SchoolActive ? "学校自习进行中" : State.ClockReady ? "等待学校自习" : "学校时间不可用";
        SchoolWhen.Text = State.ClockReady ? "今天 19:15–21:30" : "有效时段待确认";
        SchoolTimeline.Visibility = Visible(State.ClockReady);
        SchoolCapture.Text = State.SchoolActive ? "新鲜采样已确认（演示）" : State.ClockReady ? "到开始时间才采集" : "暂未确认执行";
        SchoolDisplay.Text = State.SchoolActive ? "自习展示已打开（演示）" : State.ClockReady ? "19:10 自动打开" : "等待时间恢复";
        ReportSummary.Text = State.HasReport ? "示例手动自习 · 已完成\n均值 −52 dBFS · 峰值 −28 dBFS\n有效采集覆盖率 96% · 提醒 1 次\n本机报告已生成；学校是否收到需另外核对。" : "还没有完成的自习。结束后可以在这里查看概况。";
        StudyPrimary.Content = _reportTab ? "返回自习" : _schoolTab ? State.ClockReady ? "查看排程详情" : "检查时间连接" : active ? "结束自习" : scheduled ? "取消预约" : Immediate.IsChecked == true ? "开始自习" : "保存预约";
        StudyPrimary.IsEnabled = _schoolTab || _reportTab || !State.SchoolActive;
        StudySecondary.Content = "设备与校准";
        StudyFooter.Text = microphone + (State.ReferenceSaved ? " · 估算 dB(A)" : " · dBFS");
        Microphone.IsEnabled = !active && !State.SchoolActive && !_sampling;
        ReferenceStatus.Text = State.ReferenceSaved ? "估算 dB(A) 参考已确认（演示）" : "当前使用 dBFS · 尚无估算 dB(A) 参考";
        ClockStatus.Text = State.ClockReady ? "学校时间已确认（演示）" : "学校时间不可用，定时自习执行待确认";
        IdleMinutes.IsEnabled = NoReturn.IsChecked != true;
        ResizeNavigation(); RenderCalibration();
    }

    private void ResizeNavigation()
    {
        bool compact = _compact || ActualWidth < 900;
        NavColumn.Width = new GridLength(compact ? 64 : 164);
        foreach (var text in new[] { BrandText, HomeLabel, StudyLabel, DevicesLabel, ScopeLabel }) text.Visibility = Visible(!compact);
    }
    private void HomeClicked(object sender, RoutedEventArgs e) => Navigate("Home");
    private void StudyClicked(object sender, RoutedEventArgs e) { _schoolTab = State.SchoolActive; _reportTab = false; Navigate("Study"); }
    private void ManualClicked(object sender, RoutedEventArgs e) { _schoolTab = _reportTab = false; Navigate("Study"); }
    private void SchoolClicked(object sender, RoutedEventArgs e) { _schoolTab = true; _reportTab = false; Navigate("Study"); }
    private void ReportsClicked(object sender, RoutedEventArgs e) { _reportTab = true; Navigate("Study"); }
    private void DevicesClicked(object sender, RoutedEventArgs e) => Navigate("Devices");
    private void StudySecondaryClicked(object sender, RoutedEventArgs e) => Navigate("Devices");
    private void FoldClicked(object sender, RoutedEventArgs e) { _compact = !_compact; ResizeNavigation(); }
    private void ManualOptionsChanged(object sender, RoutedEventArgs e) { if (_ready) Render(); }
    private void ReturnChanged(object sender, RoutedEventArgs e) { if (_ready) Render(); }
    private void HomePrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (!State.ClockReady && !State.SchoolActive && State.ManualState == "Idle") Navigate("Devices");
        else { _schoolTab = State.SchoolActive; _reportTab = false; Navigate("Study"); }
    }

    private void SceneChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingScene || Scene.SelectedIndex == 3) return;
        AbortSampling(); _meterVerified = false;
        State.ChangeScene(Scene.SelectedIndex switch { 1 => "SchoolActive", 2 => "ClockUnavailable", _ => "Idle" });
        Feedback.Visibility = Visibility.Collapsed; Render();
    }
    private void MarkInteraction() { _updatingScene = true; Scene.SelectedIndex = 3; _updatingScene = false; }
    private void StudyPrimaryClicked(object sender, RoutedEventArgs e)
    {
        Feedback.Visibility = Visibility.Collapsed;
        if (_reportTab) { _reportTab = false; Render(); return; }
        if (_schoolTab)
        {
            if (!State.ClockReady) { Navigate("Devices"); return; }
            Tell("学校排程由后台配置；本原型展示今日时段和时间来源，不修改排程。"); return;
        }
        if (State.ManualState != "Idle") { bool hadCapture = State.ManualState == "Active"; State.FinishManual(); MarkInteraction(); Render(); Tell(hadCapture ? "手动自习已结束，示例报告已保留。" : "预约已取消。学校安排继续保留。"); return; }
        if (NoReturn.IsChecked != true && (!int.TryParse(IdleMinutes.Text, out var minutes) || minutes < 1 || minutes > 180)) { Tell("无操作返回等待时间请输入 1–180 分钟。", true); return; }
        string? error = State.Arrange(Immediate.IsChecked == true, StartDate.SelectedDate, StartTime.Text, EndChoice.SelectedIndex == 1, EndDate.SelectedDate, EndTime.Text);
        if (error is not null) { Tell(error, true); return; }
        MarkInteraction(); Render(); Tell(State.ManualState == "Active" ? "手动自习已开始（演示），关闭管理页不会结束会话。" : "预约已保存（演示），开始时间之前不采集。");
    }
    private void ClockRetryClicked(object sender, RoutedEventArgs e)
    {
        State.ConfirmClock(); MarkInteraction(); Render(); Tell("学校时间已重新确认（演示）。下一次有效安排已恢复。");
    }
    private void InputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        AbortSampling(); _meterVerified = false;
        bool wasSaved = State.ReferenceSaved; State.ReferenceSaved = false; MarkInteraction(); Render();
        Tell(wasSaved ? "麦克风已更换，原参考已失效；请重新校准（演示）。" : "示例设备已选择，尚未开始采集。");
    }

    private void CalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (State.SchoolActive || State.ManualState == "Active") { Tell("采集正在进行。请在自习结束后进行校准。", true); return; }
        AbortSampling(); _meterVerified = false; Password.Clear(); ConfirmReference.IsChecked = false;
        MeterStatus.Text = "尚未验证"; Navigate("Calibration");
    }
    private void VerifyClicked(object sender, RoutedEventArgs e)
    {
        _meterVerified = Password.Password == "123456";
        MeterStatus.Text = _meterVerified ? "验证通过 · 已找到 DLY-2202（演示）" : "口令不正确，请重新输入原型口令。";
        if (_meterVerified) { Meter.SelectedIndex = 0; Password.Clear(); Feedback.Visibility = Visibility.Collapsed; }
        RenderCalibration();
    }
    private void RenderCalibration()
    {
        CalibrationSetup.Visibility = Visible(_calibrationStep == 0); CalibrationSampling.Visibility = Visible(_calibrationStep == 1); CalibrationResult.Visibility = Visible(_calibrationStep == 2);
        CalibrationSteps.Text = _calibrationStep switch { 1 => "✓ 连接验证　　② 同期采样　　③ 确认结果", 2 => "✓ 连接验证　　✓ 同期采样　　③ 确认结果", _ => "① 连接验证　　② 同期采样　　③ 确认结果" };
        CalibrationFooter.Text = $"步骤 {_calibrationStep + 1} / 3";
        Meter.IsEnabled = _meterVerified && !_sampling;
        CredentialsPanel.Visibility = Visible(!_meterVerified);
        ResetVerification.Visibility = Visible(_meterVerified);
        MeterPreferences.Visibility = Visible(_meterVerified);
        CalibrationPrimary.Content = _calibrationStep == 2 ? "确认并保存参考" : "开始同期采样";
        CalibrationPrimary.IsEnabled = _calibrationStep == 0 ? _meterVerified : _calibrationStep == 2 && ConfirmReference.IsChecked == true;
        CalibrationBack.Content = _calibrationStep == 1 ? "取消采样并返回" : "返回设备";
    }
    private void ResetVerificationClicked(object sender, RoutedEventArgs e) { _meterVerified = false; Password.Clear(); MeterStatus.Text = "尚未验证"; RenderCalibration(); }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) { if (_ready) RenderCalibration(); }
    private void CalibrationPrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_calibrationStep == 0 && _meterVerified)
        {
            _samplingTicks = 0; _sampling = true; _calibrationStep = 1;
            SamplingProgress.Value = 0; SamplingTitle.Text = "第一段同期采样"; SamplingTime.Text = "第一段：采样中";
            Feedback.Visibility = Visibility.Collapsed; RenderCalibration(); _timer.Start();
        }
        else if (_calibrationStep == 2 && ConfirmReference.IsChecked == true)
        { State.ReferenceSaved = true; MarkInteraction(); Navigate("Devices"); Tell("示例参考已保存。手动与学校定时自习共用；输入变化后失效。"); }
    }
    internal void AdvanceSampling()
    {
        if (!_sampling || _page != "Calibration") return;
        _samplingTicks++; SamplingProgress.Value = _samplingTicks;
        SamplingTitle.Text = _samplingTicks < 3 ? "第一段同期采样" : "第二段独立复核";
        SamplingTime.Text = _samplingTicks < 3 ? $"第一段：{_samplingTicks}/3（演示加速）" : $"复核段：{Math.Min(_samplingTicks - 3, 3)}/3（演示加速）";
        if (_samplingTicks < 6) return;
        _timer.Stop(); _sampling = false; _calibrationStep = 2; ConfirmReference.IsChecked = false;
        ResultDetails.Text = CalibrationMode.SelectedIndex == 1 ? "示例仪表电平 50.5 / 61.5 dB(A)\n参考偏移 +102.0 dB\n复核偏移 +102.3 dB\n两段电平差 11.0 dB" : "示例仪表电平 53.4 / 57.1 dB(A)\n参考偏移 +102.0 dB\n复核偏移 +102.3 dB\n单位：估算 dB(A)";
        RenderCalibration();
    }
    private void AbortSampling() { _timer.Stop(); _sampling = false; _samplingTicks = 0; _calibrationStep = 0; }
    private void DisconnectClicked(object sender, RoutedEventArgs e)
    {
        AbortSampling(); _meterVerified = false; ConfirmReference.IsChecked = false;
        MeterStatus.Text = "USB 连接中断，需重新验证设备。"; RenderCalibration(); Tell("连接中断，本段作废，未保存参考。检查连接后重新验证并采样。", true);
    }
    private void CalibrationBackClicked(object sender, RoutedEventArgs e) => Navigate("Devices");
}
