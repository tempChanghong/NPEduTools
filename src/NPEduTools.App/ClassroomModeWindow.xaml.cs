using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ClassroomModeWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private ClassroomModeState? _state;
    private bool _sending, _closing;
    public ClassroomModeWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private async Task<HostResponse> RequestAsync(string capability, string? target = null, long? revision = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExpectedRevision: revision, ClassroomMode: target is null ? null : new(target)), deadline.Token);
    }
    private static string ModeName(string mode) => mode switch { "Daily" => "日常模式", "Exam" => "考试模式", _ => "尚未设置模式" };
    private static string Enabled(bool value) => value ? "开启" : "关闭";
    private void Render(ClassroomModeState state)
    {
        _state = state;
        ModeTitle.Text = ModeName(state.Mode) + (state.Busy ? " · 切换／核实中" : state.Phase == "Incomplete" ? " · 上次操作未完成" :
            state.Phase == "Unavailable" ? " · 记录不可用" : state.MatchesMode == false ? " · 设置不一致" : "");
        StatusText.Text = state.Message;
        PauseText.Text = state.AutomaticPaused ? "自动录课已由课堂模式暂停；原录制规则保留。" : "自动录课按原有启用状态、学校时间与录制计划运行。";
        ActualText.Text = state.Actual is { } actual ?
            $"核实于 {state.CheckedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}（仅代表该时刻）\nClassIsland 管理员自启动：{Enabled(actual.ClassIslandEnabled)}\nExamAware2 登录自启动登记：{Enabled(actual.ExamAwareEnabled)}" : "实际设置尚未核实。只读核实不会启动软件。";
        RecoveryText.Text = state.Recovery is { } recovery ?
            $"恢复目标：{ModeName(recovery.PreviousMode)}；ClassIsland 自启动{Enabled(recovery.Startup.ClassIslandEnabled)}，ExamAware2 自启动{Enabled(recovery.Startup.ExamAwareEnabled)}。恢复完成前保持自动录课暂停。" : "暂无待恢复的切换记录。";
        bool ready = !_sending && !state.Busy && state.Phase != "Unavailable";
        DailyButton.IsEnabled = ExamButton.IsEnabled = ready && state.Recovery is null;
        RestoreButton.IsEnabled = ready && state.Recovery is not null;
        RefreshButton.IsEnabled = ready;
    }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (IsVisible && !_sending)
            {
                try
                {
                    var response = await RequestAsync("classroom.status");
                    if (response.ClassroomMode is { } state) Render(state); else Disconnected(response.Message);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
                { Disconnected("暂时无法连接后台。请求可能已被受理，重新连接后请核实结果。"); }
            }
            try { await Task.Delay(1000, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private void Disconnected(string message)
    {
        _state = null;
        DailyButton.IsEnabled = ExamButton.IsEnabled = RestoreButton.IsEnabled = RefreshButton.IsEnabled = false;
        StatusText.Text = message; ActualText.Text = "后台未连接，实际状态未知。";
    }
    private async Task SendAsync(string capability, string? target = null, long? confirmedRevision = null)
    {
        if (_sending || _state is null) return;
        long revision = confirmedRevision ?? _state.Revision;
        _sending = true; Render(_state);
        try
        {
            var response = await RequestAsync(capability, target, capability == "classroom.refresh" ? null : revision);
            _sending = false;
            if (response.ClassroomMode is { } state) Render(state);
            if (response.Outcome != "Accepted") StatusText.Text = response.ErrorCode switch
            {
                "RevisionConflict" => "状态已改变，请核对当前显示后再操作。",
                "ClassroomBusy" => "另一项课堂模式操作正在执行，请等待完成。",
                "RestoreRequired" => "请先恢复未完成的切换，再选择模式。",
                _ => response.Message
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
        { Disconnected("未收到后台结果。请等待重新连接并核实状态，不要重复切换。"); }
        finally { _sending = false; }
    }
    private async void DailyClicked(object sender, RoutedEventArgs e) => await SwitchAsync("Daily");
    private async void ExamClicked(object sender, RoutedEventArgs e) => await SwitchAsync("Exam");
    private async Task SwitchAsync(string target)
    {
        if (_state is null || _state.Busy) return;
        long confirmedRevision = _state.Revision;
        string changes = target == "Exam" ? "暂停 ClassIsland 管理员自启动，开启 ExamAware2 自启动，并暂停自动录课。" :
            "开启 ClassIsland 管理员自启动，关闭 ExamAware2 自启动，自动录课恢复按原配置判断。";
        if (MessageBox.Show(this, changes + "\n\n可能会打开 ExamAware2，并请求 Windows 管理员授权。当前软件不会自动退出。",
            "切换到" + ModeName(target), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await SendAsync("classroom.set", target, confirmedRevision);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await SendAsync("classroom.refresh");
    private async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_state is null || _state.Busy) return;
        long confirmedRevision = _state.Revision;
        if (MessageBox.Show(this, RecoveryText.Text + "\n\n恢复仅处理自启动与录课模式，软件运行状态保持现状。",
            "恢复切换前设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await SendAsync("classroom.restore", confirmedRevision: confirmedRevision);
    }
}
