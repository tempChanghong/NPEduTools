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
    private readonly CalibrationUiOrder _order = new();
    private NoiseProtectionState? _access;
    private bool ShowAccess(NoiseProtectionState? state, bool invalidateOnChange = false)
    {
        bool reset = (invalidateOnChange || CredentialsPanel.Visibility == Visibility.Collapsed) &&
            (state?.ManagementSource != _access?.ManagementSource || state?.CredentialRevision != _access?.CredentialRevision ||
                state?.InstanceId != _access?.InstanceId);
        if (reset) ResetCredentials();
        _access = state; ManagementLabel.Text = CalibrationAccess.Label(state); ManagementHint.Text = CalibrationAccess.Hint(state);
        ConfigureManagement.IsEnabled = !_busy && state?.ManagementSource != "School";
        return reset;
    }
    private void ResetCredentials()
    {
        CredentialsPanel.Visibility = Visibility.Visible; ResetVerification.Visibility = Visibility.Collapsed;
        ManagementSecret.Clear(); Devices.ItemsSource = null;
        ReadMeter.IsEnabled = OpenReference.IsEnabled = false;
        ClearReading("请重新验证管理口令并查找仪表。");
    }
    private void ResetVerificationClicked(object sender, RoutedEventArgs e)
    { if (!_busy) ResetCredentials(); }
    private async Task<bool> ReadAccessAsync(CancellationToken token, long stamp)
    {
        var reply = await HostClient.RequestAsync(_pipe, "noise.management.status", token);
        return _order.Current(stamp) && !_lifetime.IsCancellationRequested && ShowAccess(reply.NoiseProtection);
    }
    private long? _received;
    public SoundMeterWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent(); DesktopDialogSizing.Attach(this, pipe);
        _expiry.Tick += (_, _) => { if (_received is { } at && Stopwatch.GetElapsedTime(at).TotalSeconds >= 3) ClearReading("读数已过期，请显式重新读取。"); };
        Loaded += async (_, _) => { _expiry.Start(); long stamp = _order.Advance(); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            try { await ReadAccessAsync(deadline.Token, stamp); }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException) { if (_order.Current(stamp) && !_lifetime.IsCancellationRequested) ShowAccess(null); } }; // Never enumerates/opens hardware on load.
        Closed += (_, _) => { _expiry.Stop(); _order.Advance(); _lifetime.Cancel(); ManagementSecret.Clear(); ClearReading("窗口已关闭。"); };
    }
    private void ClearReading(string? message = null)
    { _received = null; Reading.Text = "—"; Mode.Text = ""; if (message is not null) Message.Text = message; }
    internal async Task ExecuteAsync(string action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        var selected = Devices.SelectedItem as SoundMeterDevice;
        var command = new SoundMeterCommand(action, ManagementSecret.Password, action == "read" ? selected?.Path : null);
        if (!SoundMeterContract.Valid(command)) { ClearReading("请填写管理口令，并选择声级计；已连接 NPEP 时使用学校口令。"); return; }
        long stamp = _order.Advance();
        _busy = true; ListDevices.IsEnabled = ReadMeter.IsEnabled = OpenReference.IsEnabled = Devices.IsEnabled = ConfigureManagement.IsEnabled = false;
        ClearReading(); Message.Text = "正在核对设备…";
        if (action == "list") Devices.ItemsSource = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(5000);
        try
        {
            bool reset = await ReadAccessAsync(deadline.Token, stamp);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested || reset) return;
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.meter.command", 4000, SoundMeter: command), deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            // A list/read reply belongs to the access context checked before sending it.
            // Even an initial list must be verified again if that context changed in flight.
            if (response.NoiseProtection is not null && ShowAccess(response.NoiseProtection, invalidateOnChange: true)) return;
            Message.Text = CalibrationAccess.Message(response.ErrorCode, response.Message, _access);
            if (response.SoundMeter?.Diagnostic is { } diagnostic)
                Mode.Text = $"阶段 {diagnostic.Stage ?? "未知"} · HID 输入/输出 {diagnostic.InputReportLength?.ToString() ?? "?"}/{diagnostic.OutputReportLength?.ToString() ?? "?"} 字节 · 系统错误 {diagnostic.NativeErrorCode?.ToString() ?? "无"}";
            if (response.Outcome != "Succeeded") return;
            if (action == "list")
            {
                Devices.ItemsSource = response.SoundMeter?.Devices ?? [];
                Message.Text = (_access?.ManagementSource == "School" ? "学校" : "本机") + "管理口令验证通过；" + response.Message;
                if (response.SoundMeter?.Devices is { Count: > 0 })
                { CredentialsPanel.Visibility = Visibility.Collapsed; ResetVerification.Visibility = Visibility.Visible; }
            }
            else if (response.SoundMeter?.Reading is { } value)
            {
                _received = Stopwatch.GetTimestamp(); Reading.Text = $"{value.Decibels:F1} dB({value.Weighting})";
                Mode.Text = $"{value.TimeWeighting} · 量程 {value.Range} · MAX {(value.MaximumHold ? "开" : "关")} · 低电量 {(value.LowBattery ? "是" : "否")}\nHID 输入/输出 {value.InputReportLength}/{value.OutputReportLength} 字节 · 查询往返 {value.RoundTripMilliseconds:F0} ms";
                Message.Text = value.Weighting != "A" || value.MaximumHold || value.LowBattery
                    ? "当前为 C 加权、MAX 保持或低电量；不能用于当前 A 加权参考流程。请在仪表上调整后重读。"
                    : "已读取一次，连接已释放。此值未自动填入参考，也不证明它与麦克风采样来自同一时段。";
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) ClearReading("后台响应未确认，请检查连接后重试；未沿用旧读数。"); }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested) { ListDevices.IsEnabled = Devices.IsEnabled = true; ConfigureManagement.IsEnabled = _access?.ManagementSource != "School"; ReadMeter.IsEnabled = OpenReference.IsEnabled = Devices.SelectedItem is SoundMeterDevice; }
        }
    }
    private async void ListClicked(object sender, RoutedEventArgs e) => await ExecuteAsync("list");
    private async void ReadClicked(object sender, RoutedEventArgs e) => await ExecuteAsync("read");
    private void DeviceSelected(object sender, SelectionChangedEventArgs e)
    { ClearReading(); if (!_busy) ReadMeter.IsEnabled = OpenReference.IsEnabled = Devices.SelectedItem is SoundMeterDevice; }
    internal Action<NoiseReferenceWindow>? OpenWorkspaceReference { get; set; }
    private void ReferenceClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || Devices.SelectedItem is not SoundMeterDevice selected) return;
        ClearReading("单次诊断已撤下；同期参考需新建草稿并明确开始采样。");
        var reference = new NoiseReferenceWindow(_pipe, selected.Path) { Owner = WorkspacePanel.DialogOwner(this) };
        ((PasswordBox)reference.FindName("ManagementSecret")).Password = ManagementSecret.Password;
        if (OpenWorkspaceReference is not null && _access is { } verified) reference.UseVerifiedAccess(verified);
        if (OpenWorkspaceReference is { } open) open(reference); else reference.ShowDialog();
    }
    private async void ManagementClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true; long stamp = _order.Advance(); ConfigureManagement.IsEnabled = ListDevices.IsEnabled = ReadMeter.IsEnabled = OpenReference.IsEnabled = Devices.IsEnabled = false;
        try
        {
            await NoiseManagementDialog.ConfigureAsync(WorkspacePanel.DialogOwner(this), _pipe);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            await ReadAccessAsync(deadline.Token, stamp);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) ShowAccess(null); }
        finally
        {
            _busy = false;
            if (!_lifetime.IsCancellationRequested) { ListDevices.IsEnabled = Devices.IsEnabled = true; ConfigureManagement.IsEnabled = _access?.ManagementSource != "School"; ReadMeter.IsEnabled = OpenReference.IsEnabled = Devices.SelectedItem is SoundMeterDevice; }
        }
    }
}
