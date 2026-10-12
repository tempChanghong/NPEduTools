using System.IO;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class StudyMonitoringWindow : Window
{
    private readonly string _pipe;
    private readonly Action _noise;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reading;
    public StudyMonitoringWindow(string pipe, Action noise)
    {
        _pipe = pipe; _noise = noise; InitializeComponent(); Devices.Bind(pipe);
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); await Devices.RefreshAsync(); };
        Closed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); Devices.CancelPending(); };
    }
    public void Shutdown() => Close();
    private async Task ReadAsync()
    {
        if (_reading || _lifetime.IsCancellationRequested) return;
        _reading = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
        try
        {
            var response = await HostClient.RequestAsync(_pipe, "study.status", deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            Render(response.Outcome == "Succeeded" ? response.Study : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) Render(null); }
        finally { _reading = false; }
    }
    internal void Render(StudyHostState? state)
    {
        StudyPhase.Text = state is null ? "学校安排状态待确认" : state.PolicyState != "APPLIED" ? "尚无已应用的学校自习安排" :
            state.Phase switch { "Preparing" => "准备自习", "Active" => "自习进行中", "Ended" => "自习结束", "Outside" => "当前不在自习时段", _ => "自习暂不可执行" };
        StudySchedule.Text = state?.ClockReady == true && state.SchoolNow is { } now ? $"{(state.Mode == "Exam" ? "考试模式 · Windows 时间" : "日常模式 · ClassIsland 时间")} {now:yyyy-MM-dd HH:mm:ss}" +
            (state.Window is { } w ? $"　当前时段 {StudySchedulePresentation.Span(w)}" : "") : state?.Mode == "Exam" ? "考试模式 Windows 时间尚未确认；不会自行启动。" : "学校时间未确认；不会按 Windows 时间自行启动。";
        StudyNext.Text = StudySchedulePresentation.Next(state);
        StudyCapture.Text = state is { PolicyState: "APPLIED", ClockReady: true, Phase: "Active", Execution: { Owner: "Schedule", State: "Active", SignalFresh: true } } ? "学校自习正在采集 · 已收到新鲜采样" :
            state?.Execution is { Owner: "Manual" } ? "当前是手动采样，学校自习没有接管该会话" : state?.Execution is { State: "Starting" } ? "采集启动中，尚未确认有效采样" : "学校自习采集状态待确认";
        StudyPresentation.Text = state?.ProjectionPriority == true ? "考试放映优先；继续后台采集与按策略拍照，自习提示音不补播" : state?.Display is { Selected: true, HiddenRemainingSeconds: > 0 } d ? $"全屏展示临时收起，约 {Math.Ceiling(d.HiddenRemainingSeconds)} 秒后按有效安排返回" :
            state?.Display is { Available: true } ? "全屏展示具备执行条件；这不等于已经显示" : "全屏展示等待有效安排、学校时间及运行条件";
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private void NoiseClicked(object sender, RoutedEventArgs e) => _noise();
    private void SeatingClicked(object sender, RoutedEventArgs e) => new StudySeatingUploadWindow(_pipe) { Owner = this }.ShowDialog();
    private void CalibrationClicked(object sender, RoutedEventArgs e) => new NoiseCalibrationWindow(_pipe) { Owner = this }.ShowDialog();
    private void ReferenceClicked(object sender, RoutedEventArgs e) => new NoiseReferenceWindow(_pipe) { Owner = this }.ShowDialog();
    private void MeterClicked(object sender, RoutedEventArgs e) => new SoundMeterWindow(_pipe) { Owner = this }.ShowDialog();
}
