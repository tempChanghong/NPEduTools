using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;
using Forms = System.Windows.Forms;

namespace NPEduTools.App;

public partial class MainWindow : Window
{
    private readonly string _pipe;
    private readonly string? _upstream;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StatusViewModel _model = new();
    private Task? _watch;
    private DateTimeOffset _nextStart;
    private Task? _management;
    private long _configurationRevision;
    private string? _savedPath;
    private bool _configurationLoaded;
    private bool _actionInProgress;
    private Guid? _pendingStartId;
    private Guid? _hostStream;
    private Task? _touchPoll;
    private TouchAssistState? _touchState;
    private bool _touchBusy, _updatingTouch, _exitBusy, _exiting, _entryHint;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayPause;
    private QuickAccessWindow? _quick;

    public MainWindow(string pipe, string? upstream)
    {
        _pipe = pipe;
        _upstream = upstream;
        InitializeComponent();
        DataContext = _model;
        InitializeOnboarding();
        InitializeTray();
        InitializeStartupPreferences();
        InitializeShortcuts();
        InitializeRecording();
        Activated += (_, _) => { RefreshLoginStartup(); RefreshToday(); };
        RefreshToday();
        Closing += (_, e) =>
        {
            if (_exiting) return;
            e.Cancel = true;
            if (_quick is not null || _tray is not null) HideToEdge();
            else StopClicked(this, new RoutedEventArgs());
        };
        Closed += (_, _) => { _lifetime.Cancel(); _onboardingWindow?.Shutdown(); _quick?.Shutdown(); _tray?.Dispose(); _trayIcon?.Dispose(); _recordingWindow?.Shutdown(); _autoRecordingWindow?.Shutdown(); _recording.Detach(); };
        _model.PropertyChanged += (_, _) =>
        {
            // After success, the next live snapshot owns the quick panel status again.
            if (_launchSucceeded && !_actionInProgress && _unifiedVerification is null) _launchMessage = null;
            RefreshQuick();
        };
    }

    private async Task WatchAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                EnsureHostStarted();
                await foreach (var snapshot in HostClient.WatchAsync(_pipe, token))
                {
                    if (snapshot.Outcome == "Stopped")
                    {
                        _model.Disconnected(snapshot.Message);
                        _lifetime.Cancel();
                        _touchState = null;
                        TouchStatusText.Text = "后台已停止，请退出后重新打开。";
                        RefreshTouchControls();
                        return;
                    }
                    if (_hostStream != snapshot.StreamId)
                    {
                        _hostStream = snapshot.StreamId;
                        _configurationLoaded = false;
                    }
                    _model.Apply(snapshot);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or TimeoutException or
                OperationCanceledException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _model.Disconnected(ex switch
                {
                    FileNotFoundException => "后台程序缺失，请重新构建或补齐应用目录。",
                    UnauthorizedAccessException => "连接被拒绝，请检查当前用户与进程权限。",
                    InvalidDataException or JsonException => "后台协议不兼容，请关闭旧版后台后重新打开。",
                    _ => "暂时无法连接后台，正在自动重试。"
                });
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void EnsureHostStarted()
    {
        // Mutex existence prevents duplicate starts when a Host is healthy, starting, or incompatible.
        if (Mutex.TryOpenExisting($@"Local\{_pipe}.Host", out var instance))
        {
            instance.Dispose();
            return;
        }
        if (DateTimeOffset.UtcNow < _nextStart) return;
        _nextStart = DateTimeOffset.UtcNow.AddSeconds(10);
        string executable = Path.Combine(AppContext.BaseDirectory, "Host", "NPEduTools.Host.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Host bundle is missing.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(_pipe);
        if (_upstream is not null)
        {
            start.ArgumentList.Add("--classisland-pipe");
            start.ArgumentList.Add(_upstream);
        }
        using var process = Process.Start(start);
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    private void InitializeTray()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开 NPEduTools", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
            _trayPause = new Forms.ToolStripMenuItem("暂停触摸辅助") { Enabled = false };
            _trayPause.Click += (_, _) => Dispatcher.Invoke(() => TouchPauseClicked(this, new RoutedEventArgs()));
            menu.Items.Add(_trayPause);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("停止后台并退出", null, (_, _) => Dispatcher.Invoke(() => StopClicked(this, new RoutedEventArgs())));
            using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/npedutools.ico")).Stream;
            using var source = new System.Drawing.Icon(resource, Forms.SystemInformation.SmallIconSize);
            _trayIcon = (System.Drawing.Icon)source.Clone();
            _tray = new Forms.NotifyIcon { Text = "NPEduTools", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(RestoreWindow); };
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _tray?.Dispose(); _tray = null; _trayIcon?.Dispose(); _trayIcon = null;
            HomeMessage.Text = "托盘不可用，仍可通过屏幕侧边入口操作。";
        }
    }

    public void RestoreWindow()
    {
        _quick?.Collapse(false);
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowSettings() { SelectPage(true); RestoreWindow(); }
    private void HomeNavClicked(object sender, RoutedEventArgs e) => SelectPage(false);
    private void SettingsNavClicked(object sender, RoutedEventArgs e) => SelectPage(true);
    private void SelectPage(bool settings)
    {
        PageBreadcrumb.Text = settings ? "偏好设置" : "概览";
        ShortcutPage.Visibility = Visibility.Collapsed;
        ShortcutNav.Tag = null;
        HomePage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        HomeNav.Tag = settings ? null : "active";
        SettingsNav.Tag = settings ? "active" : null;
        if (settings) _ = RefreshAdminAsync();
    }
    private void RefreshToday() => TodayText.Text = DateTime.Now.ToString("M月d日 dddd", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
    private void OpenQuickClicked(object sender, RoutedEventArgs e) { Hide(); _quick?.OpenPanel(); }
    private void MinimizeClicked(object sender, RoutedEventArgs e) => HideToEdge();
    private void DockLeftClicked(object sender, RoutedEventArgs e) => _quick?.SetSide(true);
    private void DockRightClicked(object sender, RoutedEventArgs e) => _quick?.SetSide(false);

    private void HideToEdge()
    {
        Hide();
        _quick?.Collapse(false);
        _quick?.Show();
        if (!_entryHint && _tray is not null)
        {
            _entryHint = true;
            _tray.ShowBalloonTip(3000, "NPEduTools 已收起", "点击屏幕侧边的快捷入口即可操作，后台继续运行。", Forms.ToolTipIcon.Info);
        }
    }

    private async Task TouchPollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (!_touchBusy)
            {
                try
                {
                    var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "presentation.touch.status"));
                    if (!_touchBusy) ApplyTouch(response);
                }
                catch (Exception ex) when (IsManagementError(ex))
                {
                    if (!_touchBusy) { _touchState = null; TouchStatusText.Text = "后台未连接，正在重连…"; RefreshTouchControls(); }
                }
            }
            await TryStartupTouchAsync();
            try { await Task.Delay(800, token); } catch (OperationCanceledException) { break; }
        }
    }

    private void ApplyTouch(HostResponse response)
    {
        _touchState = response.TouchAssist;
        TouchStatusText.Text = _touchState?.Error ?? _touchState?.State ?? "后台版本不支持触摸辅助，请停止后台后重新打开。";
        _updatingTouch = true;
        TouchCompatibility.IsChecked = _touchState?.AllowUnmarkedMouse ?? false;
        _updatingTouch = false;
        RefreshTouchControls();
    }

    private void RefreshTouchControls()
    {
        TouchPowerButton.IsEnabled = !_touchBusy && !_exitBusy && !_lifetime.IsCancellationRequested && _touchState is not null;
        TouchPowerButton.Content = _touchState?.Running == true ? "停止辅助" : "开启辅助";
        TouchPauseButton.Visibility = _touchState?.Running == true ? Visibility.Visible : Visibility.Collapsed;
        TouchPauseButton.IsEnabled = TouchPowerButton.IsEnabled;
        TouchPauseButton.Content = _touchState?.Paused == true ? "继续辅助" : "暂停辅助";
        TouchCompatibility.IsEnabled = TouchPowerButton.IsEnabled;
        if (_trayPause is not null)
        {
            _trayPause.Enabled = TouchPowerButton.IsEnabled && _touchState?.Running == true;
            _trayPause.Text = _touchState?.Paused == true ? "继续触摸辅助" : "暂停触摸辅助";
        }
        TouchStatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                _touchState?.Error is not null ? "#CD7552" : _touchState?.Running != true ? "#9BA8AD" : _touchState.Paused ? "#C69A4F" : "#147D68"));
        RefreshQuick();
    }

    private void RefreshQuick() => _quick?.Update(_touchState, TouchStatusText.Text, TouchPowerButton.IsEnabled,
        _launchMessage ?? _model.Connection, StartButton.IsEnabled && !_actionInProgress && !_adminBusy && !_exitBusy && !_lifetime.IsCancellationRequested,
        _restartOfferedFor is null ? "启动" : "管理员重启");

    private async Task ChangeTouchAsync(string action)
    {
        if (_touchBusy || _exitBusy || _lifetime.IsCancellationRequested) return;
        _touchBusy = true; RefreshTouchControls();
        TouchStatusText.Text = action == "disable" ? "正在停止辅助…" : "正在应用操作…";
        RefreshQuick();
        try { ApplyTouch(await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "presentation.touch." + action))); }
        catch (Exception ex) when (IsManagementError(ex))
        { _touchState = null; TouchStatusText.Text = "暂未确认结果，正在重新读取状态…"; }
        finally { _touchBusy = false; RefreshTouchControls(); }
    }

    private async void TouchPowerClicked(object sender, RoutedEventArgs e)
    {
        _startupTouchPending = false;
        await ChangeTouchAsync(_touchState?.Running == true ? "disable" : "enable");
    }
    private async void TouchPauseClicked(object sender, RoutedEventArgs e)
    {
        _startupTouchPending = false;
        await ChangeTouchAsync(_touchState?.Paused == true ? "resume" : "pause");
    }
    private async void TouchCompatibilityChanged(object sender, RoutedEventArgs e)
    {
        if (!_updatingTouch && IsLoaded) await ChangeTouchAsync(TouchCompatibility.IsChecked == true ? "compat.on" : "compat.off");
    }

    private async Task<HostResponse> ManagementRequestAsync(HostRequest request)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, request, deadline.Token);
    }

    private async Task ManagementLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.execution.get", OperationId: _pendingStartId));
                if (!_actionInProgress) ShowLaunchData(response);
            }
            catch (Exception ex) when (IsManagementError(ex))
            {
                StartButton.IsEnabled = false;
                if (!token.IsCancellationRequested && !_actionInProgress) LaunchResultText.Text = "暂时无法读取启动结果，连接恢复后继续查询。";
            }
            RefreshQuick();
            try { await Task.Delay(1000, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ShowLaunchData(HostResponse response)
    {
        if (response.Launch is not { } data)
        {
            ConfigurationMessage.Text = response.Message;
            StartButton.IsEnabled = false;
            return;
        }
        if (!_configurationLoaded)
        {
            _configurationRevision = data.Settings.Revision;
            _savedPath = data.Settings.ExecutablePath;
            if (_restartOfferedFor != _savedPath) { _restartOfferedFor = null; StartButton.Content = "启动"; }
            ExecutablePathBox.Text = _savedPath ?? "";
            _configurationLoaded = true;
            ConfigurationMessage.Text = _savedPath is null ? "请选择本机 ClassIsland 程序，然后保存路径。" : "已读取保存的程序路径。";
        }
        if (data.StorageWarning is not null) ConfigurationMessage.Text = data.StorageWarning;
        var operation = data.Execution;
        if (_unifiedVerification is { } verifying && operation?.RequestId == verifying)
        {
            if (operation.Outcome == "Running") SetLaunchMessage("ClassIsland 进程已确认，正在等待课程接口…");
            else { SetLaunchMessage(operation.Message); _launchSucceeded = operation.Outcome == "Succeeded"; _unifiedVerification = null; }
        }
        else if (_unifiedVerification is not null && response.ErrorCode == "ExecutionNotFound")
        {
            _unifiedVerification = _pendingStartId = null;
            SetLaunchMessage("连接验证请求未记录，请重新核实当前实例。再次点击会先检查是否已运行。");
        }
        if (_pendingStartId is null || operation?.RequestId == _pendingStartId)
        {
            LaunchResultText.Text = operation?.Message ?? "暂无启动记录";
            LaunchTimeText.Text = operation is null ? "" :
                $"{operation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {OutcomeName(operation.Outcome)}";
            if (operation?.Outcome != "Running") _pendingStartId = null;
        }
        StartButton.IsEnabled = !_actionInProgress && _pendingStartId is null && operation?.Outcome != "Running" && data.StorageWarning is null && response.Outcome != "Failed";
        if (_adminBusy) StartButton.IsEnabled = false;
        RefreshAdminControls();
    }

    private static string OutcomeName(string outcome) => outcome switch
    {
        "Running" => "正在验证", "Succeeded" => "已就绪", "Failed" => "失败", "TimedOut" => "就绪超时", _ => "结果待核实"
    };

    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 ClassIsland 程序", Filter = "ClassIsland 程序|ClassIsland.exe;ClassIsland.Desktop.exe|可执行文件|*.exe",
            CheckFileExists = true, Multiselect = false
        };
        if (picker.ShowDialog(this) == true) ExecutablePathBox.Text = picker.FileName;
    }

    private async void SavePathClicked(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress || _adminBusy) return;
        _actionInProgress = true;
        StartButton.IsEnabled = false;
        try
        {
            var request = new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.config.set",
                ExecutablePath: ExecutablePathBox.Text.Trim(), ExpectedRevision: _configurationRevision);
            if (Protocol.Validate(request) is not null) { ConfigurationMessage.Text = "请先选择有效的程序路径。"; return; }
            var response = await ManagementRequestAsync(request);
            if (response.Outcome == "Succeeded")
            {
                _configurationLoaded = false;
                ShowLaunchData(response);
            }
            ConfigurationMessage.Text = response.Message;
        }
        catch (Exception ex) when (IsManagementError(ex)) { ConfigurationMessage.Text = "未能确认路径已保存，请重新读取配置后核对。"; }
        finally { _actionInProgress = false; }
    }

    private async void ReloadClicked(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress) return;
        try
        {
            var response = await ManagementRequestAsync(new(Protocol.Version, Guid.NewGuid(), "classisland.config.get"));
            _configurationLoaded = false;
            ShowLaunchData(response);
        }
        catch (Exception ex) when (IsManagementError(ex)) { ConfigurationMessage.Text = "后台未连接，暂时无法读取配置。"; }
    }

    private static bool IsManagementError(Exception ex) => ex is IOException or InvalidDataException or JsonException or
        TimeoutException or OperationCanceledException or UnauthorizedAccessException;

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        if (_exitBusy || _adminBusy || _actionInProgress) return;
        _exitBusy = true;
        ExitButton.IsEnabled = false;
        HomeMessage.Text = "正在停止辅助与后台…";
        RefreshTouchControls();
        // Stop reconnecting before requesting shutdown, so this App cannot restart the Host it just stopped.
        try
        {
            if (_recording.Automatic.Enabled) await _recording.SetAutomaticAsync("disable");
            if (_recording.State.Active)
            {
                HomeMessage.Text = "正在保存微课，完成后退出…";
                if (!await _recording.StopAndSaveAsync())
                { HomeMessage.Text = "微课未能完成保存，请先查看录制状态和保留片段。"; ShowRecording(); return; }
            }
            _lifetime.Cancel();
            if (_watch is not null) await _watch;
            if (_management is not null) await _management;
            if (_touchPoll is not null) await _touchPoll;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            if (!Mutex.TryOpenExisting($@"Local\{_pipe}.Host", out var existing)) { await _recording.DisposeAsync(); _exiting = true; Close(); return; }
            existing.Dispose();
            var response = await HostClient.RequestAsync(_pipe, "host.stop", deadline.Token);
            if (response.Outcome != "Succeeded") throw new InvalidOperationException(response.Message);
            // The response acknowledges acceptance; wait for the instance to release its ownership handle.
            while (Mutex.TryOpenExisting($@"Local\{_pipe}.Host", out var instance))
            {
                instance.Dispose();
                await Task.Delay(100, deadline.Token);
            }
            await _recording.DisposeAsync();
            _exiting = true; Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or TimeoutException or
            OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.Disconnected("未能确认后台已停止。可重试停止，或关闭窗口后重新打开。 ");
            HomeMessage.Text = "未能确认后台已停止，请重试“停止后台并退出”。";
            RestoreWindow();
        }
        finally { _exitBusy = false; ExitButton.IsEnabled = true; }
    }
}
