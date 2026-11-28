using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
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
    private bool _workspaceSetupPending, _awaitingVerification;
    private DateTimeOffset? _verificationAfter;
    private Action? _returnToWorkspace;
    private long _workspaceVisit;
    private long _generation;
    private sealed record PendingSwitch(Guid RequestId, string Target, long Revision, bool SwitchRunning);
    private PendingSwitch? _pendingSwitch;
    public ClassroomModeWindow(string pipe, Action configureClassIsland, Action configureAdmin, Action configureExamAware)
        : this(pipe, configureClassIsland, configureAdmin, configureExamAware, null) { }
    internal ClassroomModeWindow(string pipe, Action configureClassIsland, Action configureAdmin, Action configureExamAware, ClassroomSetupCheck? setupCheck)
    {
        _pipe = pipe; InitializeComponent();
        DesktopDialogSizing.Attach(this, pipe);
        _configureClassIsland = configureClassIsland; _configureAdmin = configureAdmin; _configureExamAware = configureExamAware;
        _setupCheck = setupCheck ?? new(ReadSetupAsync, path => AdminClient.RunAsync(
            AdminHelperPath.FromAppDirectory(AppContext.BaseDirectory), "status", path), File.Exists);
        IsVisibleChanged += async (_, _) => { if (IsVisible) await CheckSetupAsync(); };
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        Disconnected("后台状态尚未确认，连接后自动读取。");
        _ = PollAsync();
    }
    public void Shutdown()
    {
        if (_closing) return;
        _closing = true;
        _lifetime.Cancel();
        Close();
    }

    internal void SetWorkspaceReturn(Action returnTo)
    {
        _returnToWorkspace = returnTo;
        ModeBackButton.Visibility = Visibility.Visible;
    }

    private void ModeBackClicked(object sender, RoutedEventArgs e) => _returnToWorkspace?.Invoke();

    internal void FocusWorkspaceEntry(string? check)
    {
        if (check is not null) SetupDetails.IsExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!WorkspacePanel.Visible(this) || !WorkspacePanel.DialogOwner(this).IsActive) return;
            if (check is not null && FindName(check) is FrameworkElement item && item.IsVisible)
            {
                item.BringIntoView();
                item.Focus();
            }
            else
            {
                ModeScroll.ScrollToTop();
                ModeBackButton.Focus();
            }
        });
    }

    internal void WorkspaceVisibilityChanged(bool visible)
    {
        if (_closing) return;
        _workspaceVisit++;
        _generation++;
        _workspaceSetupPending = visible;
        if (visible && !_sending) Disconnected("正在重新核实后台状态…");
    }

    private bool CanPoll() => !_closing && !_sending && WorkspacePanel.Visible(this) &&
        WorkspacePanel.DialogOwner(this).WindowState != WindowState.Minimized;

    private async Task<HostResponse> RequestAsync(string capability, string? target = null, long? revision = null,
        bool running = false, Action? beforeSend = null)
    {
        var request = new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExpectedRevision: revision, ClassroomMode: target is null ? null : new(target, running, running && target == "Daily"));
        string reason = "本次课堂模式操作尚未授权。";
        long visit = _workspaceVisit;
        bool mutation = Protocol.NoiseInterruption(request);
        bool StillCurrent() => !_closing && WorkspacePanel.Visible(this) && visit == _workspaceVisit;
        var authorized = await NoiseManagementDialog.AuthorizeAsync(WorkspacePanel.DialogOwner(this), _pipe, request,
            reportBlocked: message => reason = message, stillCurrent: mutation ? StillCurrent : null);
        if (authorized is null || mutation && !StillCurrent())
            return new(Protocol.Version, request.RequestId, "Rejected", "MANAGEMENT_CANCELLED", reason);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        beforeSend?.Invoke();
        return await HostClient.RequestAsync(_pipe, authorized, deadline.Token);
    }
    private static string ModeName(string mode) => mode switch { "Daily" => "日常模式", "Exam" => "考试模式", _ => "尚未设置模式" };
    private static string Enabled(bool value) => value ? "开启" : "关闭";
    private void Render(ClassroomModeState state)
    {
        bool layoutChanged = _state?.Mode != state.Mode;
        _state = state;
        if (layoutChanged) UpdateModeLayout();
        if (_verificationAfter is { } before && !state.Busy && state.Actual is not null && state.CheckedAt > before)
        {
            _verificationAfter = null;
            _awaitingVerification = false;
            SetOperationFeedback("已核实当前状态。请查看实际结果与可用操作。");
        }
        RenderActualResults(state);
        ModeTitle.Text = ClassroomModePresentation.From(state).Title;
        StatusText.Text = state.Message;
        TrackSwitchResult(state);
        PauseText.Text = state.AutomaticPaused ? "自动录课已由课堂模式暂停；原录制规则保留。" : "自动录课按原有启用状态、学校时间与录制计划运行。";
        ActualText.Text = state.Actual is { } actual ?
            $"核实于 {state.CheckedAt?.ToLocalTime():yyyy-MM-dd HH:mm:ss}（仅代表该时刻）\nClassIsland 管理员自启动：{Enabled(actual.ClassIslandEnabled)}\nExamAware2 登录自启动登记：{Enabled(actual.ExamAwareEnabled)}" : "实际设置尚未核实。只读核实不会启动软件。";
        RecoveryText.Text = state.Recovery is { } recovery ?
            $"恢复目标：{ModeName(recovery.PreviousMode)}；ClassIsland 自启动{Enabled(recovery.Startup.ClassIslandEnabled)}，ExamAware2 自启动{Enabled(recovery.Startup.ExamAwareEnabled)}。恢复完成前保持自动录课暂停。" : "暂无待恢复的切换记录。";
        bool ready = !_sending && !_setupBusy && !state.Busy && state.Phase != "Unavailable";
        SetupRefresh.IsEnabled = ready;
        DailyButton.IsEnabled = ExamButton.IsEnabled = ready && !_awaitingVerification && state.Recovery is null;
        RestoreButton.IsEnabled = ready && !_awaitingVerification && state.Recovery is not null;
        RefreshButton.IsEnabled = ready;
        SwitchRunning.IsEnabled = DailyButton.IsEnabled;
        RetryButton.Visibility = state.Runtime is not null ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = ready && !_awaitingVerification && state.Runtime is not null;
        UpdatePrimaryAction(state);
    }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (CanPoll())
            {
                long generation = _generation;
                try
                {
                    var response = await RequestAsync("classroom.status");
                    if (generation == _generation && CanPoll())
                    {
                        if (response.ClassroomMode is { } state)
                        {
                            Render(state);
                            if (_workspaceSetupPending && !_setupBusy && !state.Busy && state.Phase != "Unavailable")
                            {
                                _workspaceSetupPending = false;
                                _ = CheckSetupAsync();
                            }
                        }
                        else Disconnected(response.Message);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
                {
                    if (generation == _generation && CanPoll())
                        Disconnected("暂时无法连接后台。请求可能已被受理，重新连接后请核实结果。");
                }
            }
            try { await Task.Delay(1000, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private void Disconnected(string message)
    {
        _generation++;
        _state = null;
        RenderActualResults(null);
        var presentation = ClassroomModePresentation.From(null);
        ModeTitle.Text = presentation.Title;
        PauseText.Text = presentation.Detail;
        RecoveryText.Text = "恢复记录状态未知，连接恢复后重新读取。";
        SetupSummary.Text = "后台未连接，配置检查结果已失效；连接恢复后请重新检查。";
        SetupItems.ItemsSource = null;
        DailyButton.IsEnabled = ExamButton.IsEnabled = RestoreButton.IsEnabled = RefreshButton.IsEnabled = RetryButton.IsEnabled = SwitchRunning.IsEnabled = false;
        SetupRefresh.IsEnabled = !_sending && !_setupBusy;
        StatusText.Text = message; ActualText.Text = "后台未连接，实际状态未知。";
        UpdatePrimaryAction(null);
    }
    private async Task SendAsync(string capability, string? target = null, long? confirmedRevision = null, bool running = false)
    {
        bool refresh = capability == "classroom.refresh";
        if (_closing || _sending || _state is null || _awaitingVerification && !refresh) return;
        long revision = confirmedRevision ?? _state.Revision;
        var checkedBefore = _state.CheckedAt ?? DateTimeOffset.MinValue;
        bool sendingCommand = false;
        _generation++;
        _pendingSwitch = null;
        if (!_awaitingVerification)
        {
            OperationFeedbackText.Text = "";
            OperationFeedbackText.Visibility = Visibility.Collapsed;
        }
        if (target is not null) SetOperationFeedback(running
            ? "正在提交即时切换请求，等待管理验证或后台确认。"
            : "正在提交设置，等待管理验证或后台确认。");
        _sending = true; Render(_state);
        try
        {
            var response = await RequestAsync(capability, target, refresh ? null : revision, running, () => sendingCommand = true);
            _sending = false;
            if (_closing) return;
            if (refresh && _awaitingVerification && response is { Outcome: "Accepted" or "Succeeded", ClassroomMode: not null })
                _verificationAfter = checkedBefore;
            if (!refresh && response is { Outcome: "Accepted" or "Succeeded", ClassroomMode: null })
                RequireVerification("收到的结果不完整。请先核实实际设置，暂勿重复操作。");
            if (response.ClassroomMode is { } state) Render(state);
            else if (_state is not null && (response.ErrorCode == "MANAGEMENT_CANCELLED" || response.NoiseProtection is not null))
                Render(_state);
            else Disconnected(response.Message);
            if (response.Outcome != "Accepted") StatusText.Text = response.ErrorCode switch
            {
                "RevisionConflict" => "状态已改变，请核对当前显示后再操作。",
                "ClassroomBusy" => "另一项课堂模式操作正在执行，请等待完成。",
                "RestoreRequired" => "请先恢复未完成的切换，再选择模式。",
                _ => response.Message
            };
            if (response.Outcome is "Rejected" or "Failed") ShowOperationFeedback(StatusText.Text);
            else if (response is { Outcome: "Accepted", ClassroomMode: { } accepted } && target is not null)
            {
                SetOperationFeedback(running ? "后台已受理即时切换，正在等待软件切换结果。"
                    : "后台已受理设置，正在等待读回。");
                _pendingSwitch = new(response.RequestId, target, accepted.Revision, running);
                TrackSwitchResult(accepted);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        {
            _sending = false;
            if (!_closing)
            {
                string message = refresh ? "未能核实实际设置，请等后台连接恢复后重新核实。" : sendingCommand
                    ? "未收到后台结果。请等待重新连接并核实状态，不要重复切换。"
                    : "无法核实学校管理权限，本次未提交切换。";
                Disconnected(message);
                if (!refresh && sendingCommand) RequireVerification(message);
                else ShowOperationFeedback(message);
            }
        }
        finally { _sending = false; }
    }

    private void RequireVerification(string message)
    {
        _awaitingVerification = true;
        _verificationAfter = null;
        SetOperationFeedback(message);
        if (WorkspacePanel.Visible(this)) OperationFeedbackText.BringIntoView();
    }
    private async void DailyClicked(object sender, RoutedEventArgs e) => await SwitchAsync("Daily");
    private void ShowOperationFeedback(string message)
    {
        SetOperationFeedback("最近一次请求未完成：" + message);
        if (WorkspacePanel.Visible(this)) OperationFeedbackText.BringIntoView();
    }
    private void SetOperationFeedback(string message)
    {
        OperationFeedbackText.Text = message;
        OperationFeedbackText.Visibility = Visibility.Visible;
    }
    private void TrackSwitchResult(ClassroomModeState state)
    {
        if (_pendingSwitch is not { } pending || state.Revision < pending.Revision) return;
        if (state.RecentRequests?.Contains(pending.RequestId) != true) return;
        if (state.RecentRequests.Last() != pending.RequestId)
        {
            _pendingSwitch = null;
            SetOperationFeedback("本次切换结果待核对：后续模式操作已改变状态，请核对当前模式与后台说明。");
            return;
        }
        if (state.Busy)
        {
            SetOperationFeedback(SwitchActionName(pending.Target, pending.SwitchRunning) + "进行中：" + state.Message);
            return;
        }
        _pendingSwitch = null;
        if (state.Mode == pending.Target && state.Phase == "Idle" && state.Actual is not null &&
            state.CheckedAt is not null && state.MatchesMode == true)
            SetOperationFeedback((pending.SwitchRunning ? "本次即时切换已完成：" : "本次仅更新设置：") +
                ModeName(pending.Target) + "。" + state.Message);
        else ShowOperationFeedback(state.Message);
    }
    private async void ExamClicked(object sender, RoutedEventArgs e) => await SwitchAsync("Exam");
    private async Task SwitchAsync(string target)
    {
        if (_state is null || _state.Busy || _awaitingVerification) return;
        long visit = _workspaceVisit;
        bool running = SwitchRunning.IsChecked == true;
        if (!await CheckSetupAsync())
        {
            if (!_closing && visit == _workspaceVisit && WorkspacePanel.Visible(this))
            { ShowOperationFeedback(SetupSummary.Text); SetupSummary.BringIntoView(); }
            return;
        }
        if (_state is null || _state.Busy || _state.Recovery is not null || _awaitingVerification ||
            visit != _workspaceVisit || !WorkspacePanel.Visible(this)) return;
        long confirmedRevision = _state.Revision;
        string changes = target == "Exam" ? "暂停 ClassIsland 管理员自启动，开启 ExamAware2 自启动，并暂停自动录课。" :
            "开启 ClassIsland 管理员自启动，关闭 ExamAware2 自启动，自动录课恢复按原配置判断。";
        string runtimeText = running ? target == "Daily" ?
            "\n\n请先保存并关闭 ExamAware2 的所有编辑器、结束放映。点击确定表示已完成这些操作。随后正常退出考试看板，并通过管理员任务启动 ClassIsland。" :
            "\n\n准备好 ExamAware2 后将正常退出 ClassIsland；请先处理 ClassIsland 的未保存内容或弹窗。" :
            "\n\n本次仅应用设置，当前软件不会自动退出。为核实自启动登记，可能打开 ExamAware2。";
        if (ConfirmModeAction(changes + runtimeText + "\n可能需要 Windows 管理员授权。", SwitchActionName(target, running)))
            await SendAsync("classroom.set", target, confirmedRevision, running);
    }
    private async void RetryClicked(object sender, RoutedEventArgs e)
    {
        if (_awaitingVerification || _state is not { Busy: false, Runtime: { } intent } state) return;
        string message = intent.Target == "Daily" ?
            "请确认已保存并关闭所有考试编辑器、结束放映。点击确定表示已完成。将核实当前进程，再继续返回日常；已退出的考试看板不会重新打开。" :
            "将重新核实考试看板就绪，再请求 ClassIsland 正常退出。请先处理阻止退出的弹窗或未保存内容。";
        if (ConfirmModeAction(message, "重试即时切换"))
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
        long generation = _generation;
        _setupBusy = true; SetupRefresh.IsEnabled = false;
        DailyButton.IsEnabled = ExamButton.IsEnabled = SwitchRunning.IsEnabled = false;
        SetupItems.ItemsSource = null;
        SetupSummary.Text = "正在逐项检查程序位置、管理员任务与桥接…";
        try
        {
            var report = await _setupCheck.RunAsync(_lifetime.Token);
            if (_closing) return false;
            if (generation != _generation)
            {
                if (_state is not null) SetupSummary.Text = "检查期间状态已改变，请重新检查配置。";
                return false;
            }
            SetupItems.ItemsSource = report.Items;
            if (!report.CanProceed) SetupDetails.IsExpanded = true;
            SetupSummary.Text = report.Summary +
                $" 检查于 {report.CheckedAt:HH:mm:ss}。";
            return report.CanProceed;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            _setupBusy = false;
            if (!_closing)
            {
                SetupRefresh.IsEnabled = true;
                if (_state is not null)
                {
                    string feedback = StatusText.Text;
                    Render(_state);
                    StatusText.Text = feedback;
                }
            }
        }
    }
    private async void SetupRefreshClicked(object sender, RoutedEventArgs e) => await CheckSetupAsync();
    private void SetupClassIslandClicked(object sender, RoutedEventArgs e) => OpenConfiguration(_configureClassIsland);
    private void SetupAdminClicked(object sender, RoutedEventArgs e) => OpenConfiguration(_configureAdmin);
    private void SetupExamAwareClicked(object sender, RoutedEventArgs e) => OpenConfiguration(_configureExamAware);
    private void OpenConfiguration(Action open)
    {
        if (!WorkspacePanel.Embedded(this)) Hide();
        open();
    }
    private async void RestoreClicked(object sender, RoutedEventArgs e)
    {
        if (_state is not { Busy: false, Recovery: not null } || _awaitingVerification) return;
        long confirmedRevision = _state.Revision;
        if (ConfirmModeAction(RecoveryText.Text + "\n\n恢复仅处理自启动与录课模式，软件运行状态保持现状。", "恢复切换前设置"))
            await SendAsync("classroom.restore", confirmedRevision: confirmedRevision);
    }

    private bool ConfirmModeAction(string message, string title)
    {
        long visit = _workspaceVisit;
        if (!WorkspacePanel.Visible(this)) return false;
        return MessageBox.Show(WorkspacePanel.DialogOwner(this), message, title, MessageBoxButton.OKCancel,
            MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK &&
            visit == _workspaceVisit && WorkspacePanel.Visible(this);
    }
}
