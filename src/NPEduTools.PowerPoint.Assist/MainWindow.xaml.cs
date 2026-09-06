using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using NPEduTools.PowerPoint.Diagnostics;

namespace NPEduTools.PowerPoint.Assist;

public partial class MainWindow : Window
{
    private readonly PowerPointTouchAssist _assist = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _running;
    private bool _closing, _stopped;
    public MainWindow()
    {
        InitializeComponent();
        _assist.StatusChanged += status => Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = status.Error ?? status.State;
            CountText.Text = $"已补发 {status.Sent} 次 · 已跳过重复补发 {status.NativeAdvances} 次";
        });
        Loaded += (_, _) => _running = RunAsync();
        Closing += OnClosing;
    }
    private static ProcessStartInfo StartWorker()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
        if (System.IO.Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--probe-worker");
        return start;
    }
    private async Task RunAsync()
    {
        try { await Task.Run(() => _assist.RunAsync(StartWorker, _stop.Token)); }
        catch (Exception error) { StatusText.Text = $"辅助已停止：{error.GetType().Name}"; ToggleButton.IsEnabled = false; }
    }
    private void ToggleClicked(object sender, RoutedEventArgs e)
    { _assist.Enabled = !_assist.Enabled; ToggleButton.Content = _assist.Enabled ? "暂停辅助" : "继续辅助"; }
    private void CompatibilityChanged(object sender, RoutedEventArgs e) => _assist.AllowUnmarkedMouse = Compatibility.IsChecked == true;
    private void ExitClicked(object sender, RoutedEventArgs e) => Close();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_stopped) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        _assist.Enabled = false;
        ToggleButton.IsEnabled = false;
        StatusText.Text = "正在停止辅助…";
        _stop.Cancel();
        if (_running is not null) await _running;
        _stop.Dispose();
        _stopped = true;
        Close();
    }
}
