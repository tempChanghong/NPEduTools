using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class StudyMonitoringWindow : Window
{
    private readonly string _pipe;
    private readonly Action _noise;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reading;
    private bool _returning;
    private bool _stopping;
    private bool _nextFirst;
    private ScheduledStop? _stopTarget;
    private readonly StudyDisplayReturn _returnDisplay = new();
    private readonly NoiseCalibrationRead _calibrationRead = new();
    private long _viewGeneration;
    internal Action? OpenDevicesWorkspace { get; init; }
    private sealed record ScheduledStop(bool Legacy, SchoolNoiseWindow Window, Guid Instance, Guid Session, long Revision,
        string? Mode = null, string? Version = null, NoiseProtectionState? Access = null)
    {
        public string Name => Legacy ? "定时噪音监测" : "学校定时自习";
    }
    internal void WorkspaceVisibilityChanged(bool visible)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (WorkspacePanel.Embedded(this)) SchoolManualLink.Visibility = Visibility.Collapsed;
        _viewGeneration++;
        ShowStopTarget(null);
        StopFeedback(null);
        Render(null);
        RenderCalibration(null, null);
        if (visible)
        {
            _ = ReadAsync();
            if (OpenDevicesWorkspace is not null) _ = Devices.RefreshAsync();
        }
    }
    public StudyMonitoringWindow(string pipe, Action noise)
    {
        _pipe = pipe; _noise = noise; InitializeComponent(); Devices.Bind(pipe);
        DesktopDialogSizing.Attach(this, pipe);
        PageScroll.SizeChanged += (_, args) => ArrangeOverview(args.NewSize.Width);
        Devices.StatusChanged += (_, _) => DeviceWorkspaceStatus.Text = DeviceSummary.Text = Devices.Summary;
        _timer.Tick += async (_, _) => { if (WorkspacePanel.Visible(this)) await ReadAsync(); };
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); if (!_lifetime.IsCancellationRequested && WorkspacePanel.Visible(this)) await Devices.RefreshAsync(); };
        Closed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); Devices.CancelPending(); };
    }
    public void Shutdown() => Close();
    internal StudyDevicePicker TakeDevicesForWorkspace()
    {
        DeviceSettings.Content = null;
        StandaloneDeviceSection.Visibility = Visibility.Collapsed;
        DeviceWorkspaceSummary.Visibility = Visibility.Visible;
        Devices.EnableWorkspace();
        return Devices;
    }
    private void OpenDevicesClicked(object sender, RoutedEventArgs e) => OpenDevicesWorkspace?.Invoke();
    private void ArrangeOverview(double contentWidth)
    {
        // Compact density can increase the measured content width without adding space to the window.
        bool wide = contentWidth >= 760 && WorkspacePanel.DialogOwner(this).ActualWidth >= 930;
        OverviewGapColumn.Width = new GridLength(wide ? 12 : 0);
        OverviewSecondaryColumn.Width = wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        int currentRow = _nextFirst ? 1 : 0;
        Grid.SetRow(StudyScheduleCard, currentRow);
        Grid.SetRow(StudyRuntimeCard, wide ? currentRow : currentRow + 1);
        Grid.SetColumn(StudyRuntimeCard, wide ? 2 : 0);
        Grid.SetRow(StudyNextBanner, _nextFirst ? 0 : wide ? 1 : 2);
    }
    private async Task ReadAsync()
    {
        if (_reading || _returning || _stopping || _lifetime.IsCancellationRequested) return;
        _reading = true;
        long generation = _viewGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
        try
        {
            var (response, target) = await ReadStopTargetAsync(deadline.Token);
            if (_lifetime.IsCancellationRequested || generation != _viewGeneration) return;
            Render(response.Outcome == "Succeeded" ? response.Study : null);
            RenderCalibration(response.Outcome == "Succeeded" ? response.NoiseCalibrationStatus : null, response.Noise);
            ShowStopTarget(target);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested && generation == _viewGeneration) { Render(null); RenderCalibration(null, null); ShowStopTarget(null); } }
        finally { _reading = false; }
    }
    private async Task<(HostResponse Study, ScheduledStop? Target)> ReadStopTargetAsync(CancellationToken token)
    {
        var study = await _calibrationRead.RequestAsync(_pipe, new(1, Guid.NewGuid(), "study.status"), token);
        if (study is { Outcome: "Succeeded", Study: { PolicyState: "APPLIED", Phase: "Active", ClockReady: true, SchoolNow: not null, Window: { } window,
                Display.Selected: true, Execution: { Owner: "Schedule", State: "Starting" or "Active", SessionId: { } session, ClockReady: true } execution } school,
                Noise: { State: "Starting" or "Active", SessionId: { } noiseSession } noise } && session == noiseSession &&
            MatchesWindow(school, window, execution))
            return (study, await WithAccessAsync(new(false, window, noise.InstanceId, session, noise.Revision, school.Mode, school.Version), token));

        bool legacy = study is { Outcome: "Succeeded", Study.Display.Selected: false } ||
            study is { Outcome: "Rejected", ErrorCode: "UnknownCapability" or "STUDY_UNSUPPORTED" };
        if (!legacy) return (study, null);
        try
        {
            var display = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.display.status"), token);
            return (study, display is { Outcome: "Succeeded", NoiseDisplay: { Window: { } oldWindow, InstanceId: { } instance, SessionId: { } oldSession },
                Noise: { State: "Starting" or "Active" } current } && current.InstanceId == instance && current.SessionId == oldSession
                ? await WithAccessAsync(new(true, oldWindow, instance, oldSession, current.Revision), token) : null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { return (study, null); }
    }
    private async Task<ScheduledStop> WithAccessAsync(ScheduledStop target, CancellationToken token)
    {
        try
        {
            var reply = await ClassroomActionAccess.ReadAsync(_pipe, token);
            if (reply is { Outcome: "Succeeded", NoiseProtection: { Protected: true } access } &&
                access.InstanceId == target.Instance && access.SessionId == target.Session)
                return target with { Access = access };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            OperationCanceledException or TimeoutException or System.Text.Json.JsonException) { }
        // The captured schedule remains readable when only the policy read failed.
        return target;
    }
    private static bool MatchesWindow(StudyHostState study, SchoolNoiseWindow window, StudyCaptureState execution)
    {
        // Host stamps capture windows in school wall time, without an offset.
        static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return execution.Mode == study.Mode && execution.Window is { } actual &&
            actual.Start == Stamp(window.Start) && actual.End == Stamp(window.End);
    }
    private void ShowStopTarget(ScheduledStop? target)
    {
        _stopTarget = target;
        StopScheduledButton.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        var mode = target?.Access is { } access ? ClassroomActionAccess.Mode(access, "stop") : "unavailable";
        string? blocked = target?.Access is { } available ? ClassroomActionAccess.Blocked(available, mode) : ClassroomActionAccess.Describe(mode);
        StopScheduledButton.IsEnabled = !_stopping && target is not null && blocked is null;
        StopRule.Visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        StopRule.Text = "停止本场：" + (blocked ?? ClassroomActionAccess.Describe(mode));
        if (target is { Legacy: true })
        {
            _nextFirst = false;
            ArrangeOverview(PageScroll.ActualWidth);
            StudyPhase.Text = target.Name;
            StudySchedule.Text = $"当前时段 {StudySchedulePresentation.Span(target.Window)}";
            StudySchedule.Visibility = Visibility.Visible;
            StudyNext.Text = "后续时段请核对原定时排程。";
            StudyNextBanner.Visibility = Visibility.Visible;
            StudyCapture.Text = "本场定时监测正在启动或采集";
        }
    }
    private void StopFeedback(string? message)
    {
        StopNotice.Text = message;
        StopNotice.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        if (message is not null) StopNotice.BringIntoView();
    }
    private async void StopScheduledClicked(object sender, RoutedEventArgs e)
    {
        if (_stopping || !StopScheduledButton.IsEnabled || _stopTarget is not { Access: { } access } target) return;
        _stopping = true;
        StopScheduledButton.IsEnabled = false;
        long generation = ++_viewGeneration;
        bool Current() => !_lifetime.IsCancellationRequested && generation == _viewGeneration;
        async Task<bool> RecheckAsync()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(3500);
            var (_, latest) = await ReadStopTargetAsync(deadline.Token);
            if (!Current()) return false;
            if (latest == target) return true;
            StopFeedback("原场次或监测状态已变化，请刷新核对后再办理。");
            return false;
        }
        bool submitted = false;
        try
        {
            var owner = WorkspacePanel.DialogOwner(this);
            if (!StudyActionConfirmation.Ask(owner, "停止本场监测",
                $"{target.Name} · {StudySchedulePresentation.Span(target.Window)}\n{ClassroomActionAccess.Describe(ClassroomActionAccess.Mode(access, "stop"))}\n停止这一场的噪音采集。后台保持运行，后续场次仍按原排程办理。")) return;
            if (!Current() || !await RecheckAsync()) return;
            var request = new HostRequest(1, Guid.NewGuid(), "noise.command", Noise: new("stop", target.Instance, target.Revision));
            var authorized = await NoiseManagementDialog.AuthorizeAsync(owner, _pipe, request, expectedSession: target.Session, expectedAccess: access);
            if (!Current()) return;
            if (authorized is null) { StopFeedback("未取得原场次的管理授权，未发送停止请求。"); return; }
            if (!await RecheckAsync()) return;
            using var stopDeadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            stopDeadline.CancelAfter(4000);
            submitted = true;
            var result = await HostClient.RequestAsync(_pipe, authorized, stopDeadline.Token);
            if (Current()) StopFeedback(result.Outcome is "Accepted" or "Succeeded"
                ? $"已受理 {StudySchedulePresentation.Span(target.Window)} 的停止请求。请核对实际采集状态，后台继续运行。" : result.Message);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (Current()) StopFeedback(submitted ? $"{StudySchedulePresentation.Span(target.Window)} 的停止结果待确认，请先刷新实际采集状态。" : "无法核对原场次，未发送停止请求。");
        }
        finally
        {
            _stopping = false;
            if (Current()) { ShowStopTarget(null); await ReadAsync(); }
        }
    }
    internal void Render(StudyHostState? state)
    {
        bool enabled = state is
            { PolicyState: "APPLIED", Phase: not "Disabled", Reason: not ("NOT_CONFIGURED" or "INVALID_STUDY_CALENDAR") };
        bool clockConfirmed = enabled && state is { ClockReady: true, SchoolNow: not null };
        bool clockPending = enabled && !clockConfirmed;
        _nextFirst = clockConfirmed && state is
            { Phase: "Outside" or "Ended", Next: not null, Reason: not ("OFFLINE_CONTINUING" or "EXAM_PAUSED") };
        ArrangeOverview(PageScroll.ActualWidth);

        _returnDisplay.Observe(clockConfirmed ? state : null);
        ReturnStudyButton.Visibility = _returnDisplay.Available ? Visibility.Visible : Visibility.Collapsed;
        ReturnStudyButton.IsEnabled = !_returning && _returnDisplay.Available;
        StudyPhase.Text = state switch
        {
            null or { PolicyState: not "APPLIED" } => StudySchedulePresentation.Unavailable(state),
            { Reason: "NOT_CONFIGURED" } => "学校尚未配置本班自习",
            { Reason: "INVALID_STUDY_CALENDAR" } => "学校自习安排无效",
            { Phase: "Disabled" } => "当前模式未启用定时自习",
            { Mode: "Exam" } when clockPending => "考试模式时间待确认",
            _ when clockPending => "学校时间待确认",
            { Phase: "Preparing" } => "准备自习",
            { Phase: "Active" } => "自习进行中",
            { Phase: "Ended" } => "自习结束",
            { Phase: "Outside" } => "当前不在自习时段",
            _ => "自习暂不可执行"
        };
        StudyConfiguration.Text = StudySchedulePresentation.Configuration(state);
        StudyPhase.ToolTip = state?.Reason;
        StudySchedule.Text = state switch
        {
            { Mode: "Exam" } when clockPending => "已收到学校安排，Windows 时间尚未确认。当前安排不会自行启动。",
            _ when clockPending => "已收到学校安排，ClassIsland 时间尚未确认。当前安排不会按 Windows 时间自行启动。",
            { Reason: "INVALID_STUDY_CALENDAR" } => "请联系学校管理员核对本班安排。",
            { SchoolNow: { } now } when clockConfirmed =>
                (state.Window is { } w ? $"当前时段 {StudySchedulePresentation.Span(w)}\n" : "") +
                $"{(state.Mode == "Exam" ? "考试模式 · Windows 时间" : "日常模式 · ClassIsland 时间")} {now:yyyy-MM-dd HH:mm:ss}",
            _ => ""
        };
        StudySchedule.Visibility = StudySchedule.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        StudyNext.Text = StudySchedulePresentation.Next(state);
        StudyNextBanner.Visibility = clockConfirmed ? Visibility.Visible : Visibility.Collapsed;
        StudyNextBanner.SetResourceReference(Border.BackgroundProperty, _nextFirst ? "TeacherSurfaceBrush" : "TeacherPageBrush");
        StudyNext.FontWeight = _nextFirst ? FontWeights.SemiBold : FontWeights.Normal;
        StudyCapture.Text = state?.Execution?.Reason == "MICROPHONE_DISCONNECTED" ? "麦克风已断开 · 等待原设备重连" :
            clockConfirmed && state is { Phase: "Active", Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } } ? "正在采集 · 已收到新鲜采样" :
            clockConfirmed && state is { Display.Selected: true, Window: { } window,
                Execution: { Owner: "None", Reason: "WINDOW_SKIPPED", State: not ("Active" or "Starting" or "Stopping") } stopped } && MatchesWindow(state, window, stopped) ? "本场监测已停止" :
            state?.Execution is { Owner: "Manual" } ? "当前为手动采样" : clockConfirmed && state?.Execution is { State: "Starting" } ? "启动中 · 等待有效采样" :
            "采集状态待确认";
        StudyPresentation.Text = state?.ProjectionPriority == true ? "考试放映优先 · 自习展示暂缓" : clockConfirmed && state?.Display is { Selected: true, HiddenRemainingSeconds: > 0 } d ? d.IdleReady == false ?
            "已收起 · 输入状态待确认" :
            $"已收起 · 再闲置约 {Math.Ceiling(d.HiddenRemainingSeconds / 60)} 分钟返回" :
            clockConfirmed && state?.Display is { Available: true } ? "满足展示条件 · 可见性待回执确认" : "等待有效安排与时间";
        StudyCapture.ToolTip = state?.Execution is { Owner: "Manual" } ? "学校定时自习没有接管该手动会话。" : "只有当前执行状态与新鲜采样均确认，才显示正在采集。";
        StudyPresentation.ToolTip = "临时收起后连续无操作十分钟返回，操作会重新计时；后台采集和照片策略继续。满足展示条件不等于窗口已经可见。";
    }
    internal void RenderCalibration(NoiseCalibrationStatus? status, NoiseState? noise)
    {
        var label = NoiseCalibrationPresentation.Describe(status, noise);
        CalibrationStatus.Text = label.Text; CalibrationDetail.Text = label.Detail;
        CalibrationStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            label.Tone == "Warning" ? "TeacherWarningBrush" : "TeacherMutedBrush");
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        await ReadAsync();
        if (!_lifetime.IsCancellationRequested && WorkspacePanel.Visible(this)) await Devices.RefreshAsync();
    }
    private void NoiseClicked(object sender, RoutedEventArgs e) => _noise();
    private async void ReturnStudyClicked(object sender, RoutedEventArgs e)
    {
        if (_returning || _returnDisplay.Request(false) is not { } request) return;
        _returning = true; ReturnStudyButton.IsEnabled = false;
        long generation = ++_viewGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(4000);
        try
        {
            var reply = await HostClient.RequestAsync(_pipe, request, deadline.Token);
            if (_lifetime.IsCancellationRequested || generation != _viewGeneration) return;
            Render(reply.Study); StudyPresentation.Text = reply.Message;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested && generation == _viewGeneration) { Render(null); StudyPresentation.Text = "返回结果待确认，请刷新状态后重试；后台监测继续。"; } }
        finally { _returning = false; ReturnStudyButton.IsEnabled = _returnDisplay.Available; }
    }
    internal Action? OpenSeatingWorkspace { get; init; }
    private void SeatingClicked(object sender, RoutedEventArgs e)
    {
        if (OpenSeatingWorkspace is { } open) open();
        else new StudySeatingUploadWindow(_pipe) { Owner = WorkspacePanel.DialogOwner(this) }.ShowDialog();
    }
    internal Action? OpenQuickCalibration { get; init; }
    private void CalibrationClicked(object sender, RoutedEventArgs e)
    {
        if (OpenQuickCalibration is { } open) open();
        else new NoiseCalibrationWindow(_pipe) { Owner = WorkspacePanel.DialogOwner(this) }.ShowDialog();
    }
    internal Action? OpenManualReference { get; init; }
    private void ReferenceClicked(object sender, RoutedEventArgs e)
    {
        if (OpenManualReference is { } open) open();
        else new NoiseReferenceWindow(_pipe) { Owner = WorkspacePanel.DialogOwner(this) }.ShowDialog();
    }
    internal Action? OpenCalibration { get; set; }
    private void MeterClicked(object sender, RoutedEventArgs e) { if (OpenCalibration is { } open) open(); else new SoundMeterWindow(_pipe) { Owner = WorkspacePanel.DialogOwner(this) }.ShowDialog(); }
}
