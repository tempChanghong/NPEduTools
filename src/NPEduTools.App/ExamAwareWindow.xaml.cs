using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ExamAwareWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private Func<string, bool> _fileExists = File.Exists;
    private long _revision;
    private long _generation;
    private ExamAwareStatus? _state;
    private string? _savedExecutablePath;
    private bool _loadedPath, _busy, _closing, _configurationPresented, _planPresented, _wasConnected, _protocolMismatch;
    public ExamAwareWindow(string pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        DesktopDialogSizing.Attach(this, pipe);
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        Render(null);
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = ActualWidth > 0 && ActualWidth < 600;
        PageStack.Margin = compact ? new Thickness(12, 10, 12, 14) : new Thickness(18, 14, 18, 18);
        PageTitle.FontSize = compact ? 21 : 24;
        PageSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        OverviewCard.Padding = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 16, 12);
        PlanCard.Padding = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 16, 12);
        SetupCard.Padding = compact ? new Thickness(12, 10, 12, 10) : new Thickness(16, 12, 16, 12);
    }
    private async Task<HostResponse> RequestAsync(string capability, bool save = false, bool? autoStartEnabled = null, long? expectedRevision = null, ExamAwarePlanInput? plan = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExecutablePath: save ? Executable.Text.Trim() : null,
            ExpectedRevision: expectedRevision ?? (save ? _revision : null), AutoStartEnabled: autoStartEnabled, ExamPlan: plan), deadline.Token);
    }
    private bool IsMissingLocalExecutable(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathRooted(path) &&
        !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        !_fileExists(path);
    private void SetBadge(string label, string brushKey)
    {
        BridgeStateLabel.Text = label;
        BridgeStateLabel.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        BridgeBadge.SetResourceReference(Border.BorderBrushProperty, brushKey);
    }
    private void RenderOverview(ExamAwareStatus? state, bool missingLocal)
    {
        if (state is null)
        {
            ProgramSummaryText.Text = "程序：状态待确认";
            ProgramSummaryText.ToolTip = null;
            VersionText.Text = "版本：未知（未连接后台）";
            if (_protocolMismatch)
            {
                SetBadge("协议不匹配", "TeacherErrorBrush");
                NextActionText.Text = "等待后台恢复兼容协议后再操作；旧回执不代表当前状态。";
            }
            else
            {
                SetBadge("后台未连接", "TeacherWarningBrush");
                NextActionText.Text = "等待后台连接后再操作；旧回执不代表当前状态。";
            }
            return;
        }

        bool hasPath = !string.IsNullOrWhiteSpace(state.ExecutablePath);
        string fileName = hasPath ? Path.GetFileName(state.ExecutablePath!) : "";
        ProgramSummaryText.Text = !hasPath
            ? "程序：未登记 ExamAware.exe"
            : missingLocal
                ? $"程序：{fileName}（未检测到文件）"
                : $"程序：{fileName}（已登记）";
        ProgramSummaryText.ToolTip = hasPath ? state.ExecutablePath : null;
        VersionText.Text = state.AppVersion is null
            ? "版本：未知（未连接桥接）"
            : state.BridgeState == "Connected"
                ? $"版本：{state.AppVersion}" + (state.Packaged == false ? "（开发环境 · 桥接已认证）" : "（正式版 · 桥接已认证）")
                : state.BridgeState == "UnsupportedVersion"
                    ? $"版本：{state.AppVersion}（未受支持）"
                    : $"版本：{state.AppVersion}" + (state.Packaged == false ? "（开发环境）" : "");

        if (state.BridgeState == "Unavailable")
        {
            SetBadge("连接服务不可用", "TeacherErrorBrush");
            NextActionText.Text = "本机桥接服务不可用，请检查配置目录或本地端口占用后重试；原配置已保留。";
            return;
        }
        if (!hasPath)
        {
            SetBadge("待登记程序", "TeacherWarningBrush");
            NextActionText.Text = "选择并保存 ExamAware.exe。若尚未安装，请先安装 ExamAware2（Windows 1.5.2 起的 1.x 正式版）。";
            return;
        }
        if (missingLocal)
        {
            SetBadge("程序文件缺失", "TeacherErrorBrush");
            NextActionText.Text = "请重新选择 ExamAware.exe 并保存位置。";
            return;
        }
        if (state.BridgeState == "UnsupportedVersion")
        {
            SetBadge("版本或平台不受支持", "TeacherErrorBrush");
            NextActionText.Text = "请确认当前运行的是 Windows 1.5.2 起的 1.x 正式版，再检查桥接插件。";
            return;
        }
        if (state.BridgeState != "Connected")
        {
            if (state.Quit?.State == "Exited")
            {
                SetBadge("桥接未连接", "TeacherMutedBrush");
                NextActionText.Text = "已确认上次退出。当前未收到桥接心跳，可打开考试看板后再核对连接。";
            }
            else
            {
                SetBadge("未运行或未配对", "TeacherWarningBrush");
                NextActionText.Text = "未收到桥接心跳（软件未运行，或尚未安装插件／完成配对）。可先点击“打开考试看板”；首次配对请在“连接配置”导出文件，若软件进入快速开始向导请先退出，再到主窗口点击“连接 NPEduTools”导入。";
            }
            return;
        }

        bool busyOperation = state.Quit?.State is "Sending" or "AwaitingExit" ||
            state.AutoStartChange?.State == "Sending" ||
            state.PlanOperation?.State == "Sending";
        if (busyOperation)
        {
            SetBadge("操作确认中", "TeacherAccentBrush");
            NextActionText.Text = "正在等待 ExamAware2 确认当前操作，请勿重复触发；结果会随桥接状态更新。";
            return;
        }
        if (!state.CanSetAutoStart || !state.CanPresent || state.Player is not { Known: true })
        {
            SetBadge("已连接 · 部分功能不可用", "TeacherWarningBrush");
            NextActionText.Text = !state.CanSetAutoStart && !state.CanPresent
                ? "桥接已连接，但缺少自启动与放映能力（需桥接插件 0.4.0 及以上并授权）。请在“连接配置”中打开插件设置升级或授权。"
                : !state.CanPresent
                    ? "桥接已连接，但暂不支持方案放映（需桥接插件 0.4.0 及以上并授权放映与状态读取）。请打开插件设置核对。"
                    : !state.CanSetAutoStart
                        ? "桥接已连接，但暂不支持修改自启动（需桥接插件 0.3.0+ 及 app.configure 权限）。请打开插件设置核对。"
                        : "桥接已连接，但无法读取实时放映状态；请打开插件设置核对 player.observe 权限。";
            return;
        }
        if (state.Packaged == false)
        {
            SetBadge("已连接（开发版）", "TeacherWarningBrush");
            NextActionText.Text = "桥接已连接（开发环境）。放映与自启动按当前授权判断；课堂模式切换需使用正式打包版本。";
            return;
        }
        SetBadge("已连接 · 就绪", "TeacherSuccessBrush");
        NextActionText.Text = "可选择并校验考试方案，核对摘要后开始放映。";
    }
    private void Render(ExamAwareStatus? state)
    {
        if (state is not null) _protocolMismatch = false;
        _state = state;
        RuntimeActions.IsEnabled = Actions.IsEnabled = PairingActions.IsEnabled = AutoStartActions.IsEnabled = !_busy && state is not null;
        bool missingLocal = IsMissingLocalExecutable(state?.ExecutablePath);
        bool needsExecutable = state is not null && (string.IsNullOrWhiteSpace(state.ExecutablePath) || missingLocal);
        StartButton.IsEnabled = SettingsButton.IsEnabled = !needsExecutable;
        ConfigureButton.Content = needsExecutable ? "登记程序位置" : "连接配置";
        ConfigureButton.Style = (Style)FindResource(needsExecutable ? "TeacherPrimary" : "TeacherQuiet");
        RenderOverview(state, missingLocal);
        if (state is not null && !_configurationPresented)
        {
            _configurationPresented = true;
            if (!ConnectionConfiguration.IsKeyboardFocusWithin)
                ConnectionConfiguration.IsExpanded = string.IsNullOrWhiteSpace(state.ExecutablePath) || state.BridgeState != "Connected";
        }
        bool connected = state?.BridgeState == "Connected";
        if (state is not null && !_planPresented)
        {
            _planPresented = true;
            if (!PlanConfiguration.IsKeyboardFocusWithin)
                PlanConfiguration.IsExpanded = connected || state.PreparedPlan is not null || state.PlanOperation is not null;
        }
        else if (connected && (!_wasConnected || state!.PreparedPlan is not null || state.PlanOperation is not null) &&
            !PlanConfiguration.IsExpanded && !PlanConfiguration.IsKeyboardFocusWithin)
        {
            PlanConfiguration.IsExpanded = true;
        }
        if (connected) _wasConnected = true;
        bool ready = !_busy && !needsExecutable && state?.BridgeState == "Connected" &&
            state.Quit?.State is not ("Sending" or "AwaitingExit") && state.AutoStartChange?.State != "Sending" && state.PlanOperation?.State != "Sending";
        RenderPlan(state, ready);
        QuitButton.IsEnabled = ready;
        EnableAutoStartButton.IsEnabled = ready && state!.CanSetAutoStart && state.AutoStartRegistered != true;
        DisableAutoStartButton.IsEnabled = ready && state!.CanSetAutoStart && state.AutoStartRegistered != false;
        AutoStartMessage.Text = state?.AutoStartChange is { } change ?
            (change.State == "Sending" ? change.Message : "上次设置结果：" + change.Message) :
            (state?.BridgeState == "Connected" && !state.CanSetAutoStart ? "修改自启动需要桥接 0.3.0 或兼容版本，并授权设置权限。" : "连接桥接并保存程序位置后，可设置当前程序的登录自启动。");
        QuitStatus.Text = state?.Quit is { } quit
            ? state.BridgeState == "Connected" && quit.State is not ("Sending" or "AwaitingExit")
                ? "上次退出：" + quit.Message
                : quit.Message
            : ready ? "退出前请保存并关闭编辑器、结束放映。" : "连接桥接并保存程序位置后可用。";
        if (state is null)
        {
            Connection.Text = _protocolMismatch ? "后台协议不匹配或回执无效，连接未确认。" : "后台连接未确认，等待重新连接。";
            AutoStartText.Text = "登录自启动登记：未知";
            AutoStartMessage.Text = "当前设置结果未知，恢复连接后重新读取。";
            QuitStatus.Text = "当前退出状态未知，不能据此判断软件已经退出。";
            return;
        }
        _revision = state.Revision;
        _savedExecutablePath = state.ExecutablePath;
        if (!_loadedPath) { Executable.Text = state.ExecutablePath ?? ""; _loadedPath = true; }
        Connection.Text = state.Message;
        AutoStartText.Text = "登录自启动登记：" + (state.AutoStartRegistered switch { true => "已登记", false => "未登记", _ => "未知" });
    }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (IsVisible && !_busy)
            {
                long generation = _generation;
                try
                {
                    var response = await RequestAsync("examaware.status");
                    if (!_closing && !_busy && generation == _generation)
                    {
                        _protocolMismatch = false;
                        Render(response.ExamAware);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
                {
                    if (!_closing && !_busy && generation == _generation)
                    {
                        _protocolMismatch = ex is InvalidDataException or JsonException;
                        Render(null);
                    }
                }
            }
            try { await Task.Delay(1500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RunAsync(string capability, bool save = false, bool? autoStartEnabled = null, long? expectedRevision = null, ExamAwarePlanInput? plan = null)
    {
        if (_closing || _busy || _state is null) return;
        _generation++;
        _busy = true; Render(_state);
        Message.Text = "正在等待后台确认…";
        try
        {
            var response = await RequestAsync(capability, save, autoStartEnabled, expectedRevision, plan);
            if (plan?.Action == "prepare") _allowPreparedPlan = response.Outcome == "Accepted";
            _protocolMismatch = false;
            Render(response.ExamAware); Message.Text = response.Message;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        {
            _protocolMismatch = ex is InvalidDataException or JsonException;
            Render(null);
            Message.Text = _protocolMismatch
                ? "未收到有效后台回执（协议不匹配或回执无效），请先检查软件窗口及连接状态，再决定是否重试。"
                : "未收到后台结果，请先检查软件窗口及连接状态，再决定是否重试。";
        }
        finally { _busy = false; Render(_state); }
    }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 ExamAware2", Filter = "ExamAware 程序|ExamAware.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) Executable.Text = dialog.FileName;
    }
    private void ConfigureClicked(object sender, RoutedEventArgs e)
    {
        ConnectionConfiguration.IsExpanded = true;
        _configurationPresented = true;
        Executable.BringIntoView(); Executable.Focus();
    }
    private async void SaveClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.config.set", true);
    private async void StartClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.start");
    private async void SettingsClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.settings");
    private async void PluginsClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.plugins");
    private async void EnableAutoStartClicked(object sender, RoutedEventArgs e) => await SetAutoStartAsync(true);
    private async void DisableAutoStartClicked(object sender, RoutedEventArgs e) => await SetAutoStartAsync(false);
    private async Task SetAutoStartAsync(bool enabled)
    {
        if (_busy) return;
        string action = enabled ? "开启" : "关闭";
        long confirmedRevision = _revision;
        if (MessageBox.Show(this, $"确定{action}以下程序的登录自启动吗？\n{_savedExecutablePath}\n\n设置针对当前 Windows 用户；结果以软件读回的登记状态为准。",
            "设置登录自启动", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await RunAsync("examaware.autostart.set", autoStartEnabled: enabled, expectedRevision: confirmedRevision);
    }
    private async Task ConfirmRevisionActionAsync(string capability, Func<bool> confirm)
    {
        if (_closing || _busy || _state is null) return;
        // A modal dialog keeps dispatching status polls; bind consent before it opens.
        long confirmedRevision = _revision;
        if (confirm()) await RunAsync(capability, expectedRevision: confirmedRevision);
    }
    private async void QuitClicked(object sender, RoutedEventArgs e) =>
        await ConfirmRevisionActionAsync("examaware.quit", () => MessageBox.Show(this,
            "请确认已保存并关闭考试编辑器、结束放映。\nExamAware 在编辑器仍打开时可能无法正常退出。现在继续退出吗？",
            "正常退出 ExamAware", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
    private async void ResetPairingClicked(object sender, RoutedEventArgs e) =>
        await ConfirmRevisionActionAsync("examaware.pairing.reset", () => MessageBox.Show(this,
            "撤销后，现有配对文件会失效，需要重新导出并在 ExamAware 中导入。",
            "撤销当前配对", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK);
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_closing || _busy || _state is null) return;
        var dialog = new SaveFileDialog { Title = "导出本机配对文件", FileName = "NPEduTools-ExamAware-pairing.json", Filter = "配对文件|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        await ExportPairingAsync(dialog.FileName);
    }
    private async Task ExportPairingAsync(string fileName)
    {
        if (_closing || _busy || _state is null) return;
        _generation++;
        _busy = true; Render(_state);
        Message.Text = "正在等待后台并导出配对文件…";
        bool received = false;
        try
        {
            var response = await RequestAsync("examaware.pairing.get");
            received = true;
            _protocolMismatch = false;
            Render(response.ExamAware);
            if (response.ExamAwarePairing is not { } pairing) { Message.Text = response.Message; return; }
            await File.WriteAllTextAsync(fileName, JsonSerializer.Serialize(pairing, Protocol.Json), _lifetime.Token);
            Message.Text = "已导出。请在 ExamAware 中导入；配对文件含连接凭据，导入后可删除。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        {
            if (!received)
            {
                _protocolMismatch = ex is InvalidDataException or JsonException;
                Render(null);
            }
            Message.Text = !received && _protocolMismatch
                ? "未能导出：后台协议不匹配或回执无效，未写入配对文件。"
                : "未能导出，请检查后台连接与保存位置。";
        }
        finally { _busy = false; Render(_state); }
    }
}
