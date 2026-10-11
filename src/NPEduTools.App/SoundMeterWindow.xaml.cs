using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class SoundMeterWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _busy;
    private long? _received;
    public SoundMeterWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();
        _expiry.Tick += (_, _) => { if (_received is { } at && Stopwatch.GetElapsedTime(at).TotalSeconds >= 3) ClearReading("读数已过期，请显式重新读取。"); };
        Loaded += (_, _) => _expiry.Start(); // Never enumerates/opens hardware on load.
        Closed += (_, _) => { _expiry.Stop(); _lifetime.Cancel(); ManagementSecret.Clear(); ClearReading("窗口已关闭。"); };
    }
    private void ClearReading(string? message = null)
    { _received = null; Reading.Text = "—"; Mode.Text = ""; if (message is not null) Message.Text = message; }
    internal async Task ExecuteAsync(string action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        var selected = Devices.SelectedItem as SoundMeterDevice;
        var command = new SoundMeterCommand(action, ManagementSecret.Password, action == "read" ? selected?.Path : null);
        if (!SoundMeterContract.Valid(command)) { ClearReading("请填写本机管理口令，并选择声级计。"); return; }
        _busy = true; ListDevices.IsEnabled = ReadMeter.IsEnabled = Devices.IsEnabled = false;
        ClearReading(); Message.Text = "正在核对设备…";
        if (action == "list") Devices.ItemsSource = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(5000);
        try
        {
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.meter.command", 4000, SoundMeter: command), deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            Message.Text = response.Message;
            if (response.Outcome != "Succeeded") return;
            if (action == "list") Devices.ItemsSource = response.SoundMeter?.Devices ?? [];
            else if (response.SoundMeter?.Reading is { } value)
            {
                _received = Stopwatch.GetTimestamp(); Reading.Text = $"{value.Decibels:F1} dB({value.Weighting})";
                Mode.Text = $"{value.TimeWeighting} · 量程 {value.Range} · MAX {(value.MaximumHold ? "开" : "关")} · 低电量 {(value.LowBattery ? "是" : "否")}\nHID 输入/输出 {value.InputReportLength}/{value.OutputReportLength} 字节 · 查询往返 {value.RoundTripMilliseconds:F0} ms";
                Message.Text = value.Weighting != "A" || value.MaximumHold || value.LowBattery
                    ? "当前为 C 加权、MAX 保持或低电量；不能用于当前 A 加权参考流程。请在仪表上调整后重读。"
                    : "已读取一次，连接已释放。此值未自动填入参考，也不证明它与麦克风采样来自同一时段。";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) ClearReading("后台响应未确认，请检查连接后重试；未沿用旧读数。"); }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested) { ListDevices.IsEnabled = Devices.IsEnabled = true; ReadMeter.IsEnabled = Devices.SelectedItem is SoundMeterDevice; }
        }
    }
    private async void ListClicked(object sender, RoutedEventArgs e) => await ExecuteAsync("list");
    private async void ReadClicked(object sender, RoutedEventArgs e) => await ExecuteAsync("read");
    private void DeviceSelected(object sender, SelectionChangedEventArgs e)
    { ClearReading(); if (!_busy) ReadMeter.IsEnabled = Devices.SelectedItem is SoundMeterDevice; }
    private async void ManagementClicked(object sender, RoutedEventArgs e) => await NoiseManagementDialog.ConfigureAsync(this, _pipe);
}
