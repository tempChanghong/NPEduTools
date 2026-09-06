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

    public MainWindow(string pipe, string? upstream)
    {
        _pipe = pipe;
        _upstream = upstream;
        InitializeComponent();
        DataContext = _model;
        Loaded += (_, _) => _watch ??= WatchAsync(_lifetime.Token);
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
                        return;
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

    private async void StopClicked(object sender, RoutedEventArgs e)
    {
        // Stop reconnecting before requesting shutdown, so this App cannot restart the Host it just stopped.
        _lifetime.Cancel();
        try
        {
            if (_watch is not null) await _watch;
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
