using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using NPEduTools.Contracts;

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

    public MainWindow(string pipe, string? upstream)
    {
        _pipe = pipe;
        _upstream = upstream;
        InitializeComponent();
        DataContext = _model;
        Loaded += (_, _) =>
        {
            _watch ??= WatchAsync(_lifetime.Token);
            _management ??= ManagementLoopAsync(_lifetime.Token);
        };
        Closed += (_, _) => _lifetime.Cancel();
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
                ShowLaunchData(response);
            }
            catch (Exception ex) when (IsManagementError(ex))
            {
                StartButton.IsEnabled = false;
                if (!token.IsCancellationRequested) LaunchResultText.Text = "暂时无法读取启动结果，连接恢复后继续查询。";
            }
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
            ExecutablePathBox.Text = _savedPath ?? "";
            _configurationLoaded = true;
            ConfigurationMessage.Text = _savedPath is null ? "请选择本机 ClassIsland 程序，然后保存路径。" : "已读取保存的程序路径。";
        }
        if (data.StorageWarning is not null) ConfigurationMessage.Text = data.StorageWarning;
        var operation = data.Execution;
        if (_pendingStartId is null || operation?.RequestId == _pendingStartId)
        {
            LaunchResultText.Text = operation?.Message ?? "暂无启动记录";
            LaunchTimeText.Text = operation is null ? "" :
                $"{operation.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {OutcomeName(operation.Outcome)}";
            if (operation?.Outcome != "Running") _pendingStartId = null;
        }
        StartButton.IsEnabled = !_actionInProgress && operation?.Outcome != "Running" && data.StorageWarning is null && response.Outcome != "Failed";
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
        if (_actionInProgress) return;
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

    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        if (_actionInProgress) return;
        if (string.IsNullOrWhiteSpace(_savedPath) || !string.Equals(ExecutablePathBox.Text.Trim(), _savedPath, StringComparison.OrdinalIgnoreCase))
        {
            ConfigurationMessage.Text = "请先保存当前程序路径，再启动 ClassIsland。";
            return;
        }
        _actionInProgress = true;
        StartButton.IsEnabled = false;
        _pendingStartId ??= Guid.NewGuid();
        try
        {
            var response = await ManagementRequestAsync(new(Protocol.Version, _pendingStartId.Value, "classisland.start"));
            ShowLaunchData(response);
            LaunchResultText.Text = response.Message;
            if (response.Outcome == "Rejected") _pendingStartId = null;
        }
        catch (Exception ex) when (IsManagementError(ex))
        {
            LaunchResultText.Text = "尚未确认启动请求结果，将查询同一请求；重试不会重复启动。";
        }
        finally { _actionInProgress = false; }
    }

    private static bool IsManagementError(Exception ex) => ex is IOException or InvalidDataException or JsonException or
        TimeoutException or OperationCanceledException or UnauthorizedAccessException;

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        // Stop reconnecting before requesting shutdown, so this App cannot restart the Host it just stopped.
        _lifetime.Cancel();
        try
        {
            if (_watch is not null) await _watch;
            if (_management is not null) await _management;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await HostClient.RequestAsync(_pipe, "host.stop", deadline.Token);
            if (response.Outcome != "Succeeded") throw new InvalidOperationException(response.Message);
            // The response acknowledges acceptance; wait for the instance to release its ownership handle.
            while (Mutex.TryOpenExisting($@"Local\{_pipe}.Host", out var instance))
            {
                instance.Dispose();
                await Task.Delay(100, deadline.Token);
            }
            Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or TimeoutException or
            OperationCanceledException or UnauthorizedAccessException or InvalidOperationException)
        {
            _model.Disconnected("未能确认后台已停止。可重试停止，或关闭窗口后重新打开。 ");
        }
    }
}
