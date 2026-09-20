using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;
using NPEduTools.ClassIsland.Admin;

namespace NPEduTools.App;

public partial class ClassroomModeWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private ClassroomModeState? _state;
    private readonly Action _configureClassIsland, _configureAdmin, _configureExamAware;
    private readonly ClassroomSetupCheck _setupCheck;
    private bool _sending, _closing, _setupBusy;
    public ClassroomModeWindow(string pipe, Action configureClassIsland, Action configureAdmin, Action configureExamAware)
    {
        _pipe = pipe; InitializeComponent();
        _configureClassIsland = configureClassIsland; _configureAdmin = configureAdmin; _configureExamAware = configureExamAware;
        _setupCheck = new(ReadSetupAsync, path => AdminClient.RunAsync(
            Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe"), "status", path), File.Exists);
        IsVisibleChanged += async (_, _) => { if (IsVisible) await CheckSetupAsync(); };
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private async Task<HostResponse> RequestAsync(string capability, string? target = null, long? revision = null, bool running = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExpectedRevision: revision, ClassroomMode: target is null ? null : new(target, running, running && target == "Daily")), deadline.Token);
    }
    private static string ModeName(string mode) => mode switch { "Daily" => "日常模式", "Exam" => "考试模式", _ => "尚未设置模式" };
    private static string Enabled(bool value) => value ? "开启" : "关闭";
    private void Render(ClassroomModeState state)
    {
        _state = state;
        ModeTitle.Text = ClassroomModePresentation.From(state).Title;
        StatusText.Text = state.Message;
        PauseText.Text = state.AutomaticPaused ? "自动录课已由课堂模式暂停；原录制规则保留。" : "自动录课按原有启用状态、学校时间与录制计划运行。";
        ActualText.Text = state.Actual is { } actual ?
            $"核实于 {state.CheckedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}（仅代表该时刻）\nClassIsland 管理员自启动：{Enabled(actual.ClassIslandEnabled)}\nExamAware2 登录自启动登记：{Enabled(actual.ExamAwareEnabled)}" : "实际设置尚未核实。只读核实不会启动软件。";
        RecoveryText.Text = state.Recovery is { } recovery ?
            $"恢复目标：{ModeName(recovery.PreviousMode)}；ClassIsland 自启动{Enabled(recovery.Startup.ClassIslandEnabled)}，ExamAware2 自启动{Enabled(recovery.Startup.ExamAwareEnabled)}。恢复完成前保持自动录课暂停。" : "暂无待恢复的切换记录。";
        bool ready = !_sending && !_setupBusy && !state.Busy && state.Phase != "Unavailable";
        SetupRefresh.IsEnabled = ready;
        DailyButton.IsEnabled = ExamButton.IsEnabled = ready && state.Recovery is null;
        RestoreButton.IsEnabled = ready && state.Recovery is not null;
        RefreshButton.IsEnabled = ready;
        SwitchRunning.IsEnabled = ready && state.Recovery is null;
        RetryButton.Visibility = state.Runtime is not null ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = ready && state.Runtime is not null;
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
        SetupSummary.Text = "后台未连接，配置检查结果已失效；连接恢复后请重新检查。";
        SetupItems.ItemsSource = null;
        DailyButton.IsEnabled = ExamButton.IsEnabled = RestoreButton.IsEnabled = RefreshButton.IsEnabled = RetryButton.IsEnabled = SwitchRunning.IsEnabled = false;
        StatusText.Text = message; ActualText.Text = "后台未连接，实际状态未知。";
    }
    private async Task SendAsync(string capability, string? target = null, long? confirmedRevision = null, bool running = false)
    {
        if (_sending || _state is null) return;
        long revision = confirmedRevision ?? _state.Revision;
        _sending = true; Render(_state);
        try
        {
            var response = await RequestAsync(capability, target, capability == "classroom.refresh" ? null : revision, running);
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
        if (!await CheckSetupAsync()) { SetupSummary.BringIntoView(); return; }
        if (_state is null || _state.Busy || _state.Recovery is not null || !IsVisible) return;
        long confirmedRevision = _state.Revision;
        string changes = target == "Exam" ? "暂停 ClassIsland 管理员自启动，开启 ExamAware2 自启动，并暂停自动录课。" :
            "开启 ClassIsland 管理员自启动，关闭 ExamAware2 自启动，自动录课恢复按原配置判断。";
        bool running = SwitchRunning.IsChecked == true;
        string runtimeText = running ? target == "Daily" ?
            "\n\n请先保存并关闭 ExamAware2 的所有编辑器、结束放映。点击确定表示已完成这些操作。随后正常退出考试看板，并通过管理员任务启动 ClassIsland。" :
            "\n\n准备好 ExamAware2 后将正常退出 ClassIsland；请先处理 ClassIsland 的未保存内容或弹窗。" :
            "\n\n当前软件不会自动退出。为设置自启动，可能打开 ExamAware2。";
        if (MessageBox.Show(this, changes + runtimeText + "\n可能需要 Windows 管理员授权。",
            "切换到" + ModeName(target), MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await SendAsync("classroom.set", target, confirmedRevision, running);
    }
    private async void RetryClicked(object sender, RoutedEventArgs e)
    {
        if (_state is not { Busy: false, Runtime: { } intent } state) return;
        string message = intent.Target == "Daily" ?
            "请确认已保存并关闭所有考试编辑器、结束放映。点击确定表示已完成。将核实当前进程，再继续返回日常；已退出的考试看板不会重新打开。" :
            "将重新核实考试看板就绪，再请求 ClassIsland 正常退出。请先处理阻止退出的弹窗或未保存内容。";
        if (MessageBox.Show(this, message, "重试即时切换", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await SendAsync("classroom.retry", intent.Target, state.Revision, true);
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await SendAsync("classroom.refresh");
    private async Task<HostResponse> ReadSetupAsync(string capability, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, capability, deadline.Token);
    }
    private async Task<bool> CheckSetupAsync()
    {
        if (_setupBusy || _sending || _closing || _state?.Busy == true) return false;
        _setupBusy = true; SetupRefresh.IsEnabled = false;
        DailyButton.IsEnabled = ExamButton.IsEnabled = SwitchRunning.IsEnabled = false;
        SetupItems.ItemsSource = null;
        SetupSummary.Text = "正在逐项检查程序位置、管理员任务与桥接…";
        try
        {
            var report = await _setupCheck.RunAsync(_lifetime.Token);
            if (_closing) return false;
            SetupItems.ItemsSource = report.Items;
            SetupSummary.Text = (report.AllReady ? "四项已就绪。" : report.CanProceed ? "程序与任务已就绪，桥接仍需连接确认。" : "还有配置需要处理，请按下列提示完成设置。") +
                $" 检查于 {report.CheckedAt:HH:mm:ss}。";
            return report.CanProceed;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            _setupBusy = false;
            if (!_closing) { SetupRefresh.IsEnabled = true; if (_state is not null) Render(_state); }
        }
    }
    private async void SetupRefreshClicked(object sender, RoutedEventArgs e) => await CheckSetupAsync();
    private void SetupClassIslandClicked(object sender, RoutedEventArgs e) { Hide(); _configureClassIsland(); }
    private void SetupAdminClicked(object sender, RoutedEventArgs e) { Hide(); _configureAdmin(); }
    private void SetupExamAwareClicked(object sender, RoutedEventArgs e) { Hide(); _configureExamAware(); }
    private async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_state is null || _state.Busy) return;
        long confirmedRevision = _state.Revision;
        if (MessageBox.Show(this, RecoveryText.Text + "\n\n恢复仅处理自启动与录课模式，软件运行状态保持现状。",
            "恢复切换前设置", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await SendAsync("classroom.restore", confirmedRevision: confirmedRevision);
    }
}
