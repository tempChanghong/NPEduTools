using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class ExamAwareWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private long _revision;
    private bool _loadedPath, _busy, _closing;
    public ExamAwareWindow(string pipe)
    {
        _pipe = pipe;
        InitializeComponent();
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private async Task<HostResponse> RequestAsync(string capability, bool save = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExecutablePath: save ? Executable.Text.Trim() : null, ExpectedRevision: save ? _revision : null), deadline.Token);
    }
    private void Render(ExamAwareStatus? state)
    {
        if (state is null) { Connection.Text = "后台尚不支持 ExamAware，请重启 NPEduTools 后重试。"; AutoStartText.Text = "登录自启动登记：未知"; VersionText.Text = "版本：未知"; return; }
        _revision = state.Revision;
        if (!_loadedPath) { Executable.Text = state.ExecutablePath ?? ""; _loadedPath = true; }
        Connection.Text = state.Message;
        VersionText.Text = state.AppVersion is null ? "版本：未知" : $"版本：{state.AppVersion}" + (state.Packaged == false ? "（开发环境）" : "");
        AutoStartText.Text = "登录自启动登记：" + (state.AutoStartRegistered switch { true => "已登记", false => "未登记", _ => "未知" });
    }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (IsVisible && !_busy)
            {
                try { Render((await RequestAsync("examaware.status")).ExamAware); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
                { Connection.Text = "暂时无法连接 NPEduTools 后台。"; VersionText.Text = "版本：未知"; AutoStartText.Text = "登录自启动登记：未知"; }
            }
            try { await Task.Delay(1500, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RunAsync(string capability, bool save = false)
    {
        if (_busy) return;
        _busy = true; Actions.IsEnabled = PairingActions.IsEnabled = false;
        try
        {
            var response = await RequestAsync(capability, save);
            Render(response.ExamAware); Message.Text = response.Message;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException)
        { Message.Text = "未收到后台结果，请先检查软件窗口及连接状态，再决定是否重试。"; }
        finally { _busy = false; Actions.IsEnabled = PairingActions.IsEnabled = true; }
    }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 ExamAware2", Filter = "ExamAware 程序|ExamAware.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) Executable.Text = dialog.FileName;
    }
    private async void SaveClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.config.set", true);
    private async void StartClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.start");
    private async void SettingsClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.settings");
    private async void PluginsClicked(object sender, RoutedEventArgs e) => await RunAsync("examaware.plugins");
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new SaveFileDialog { Title = "导出本机配对文件", FileName = "NPEduTools-ExamAware-pairing.json", Filter = "配对文件|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        _busy = true; Actions.IsEnabled = PairingActions.IsEnabled = false;
        try
        {
            var response = await RequestAsync("examaware.pairing.get");
            if (response.ExamAwarePairing is not { } pairing) { Message.Text = response.Message; return; }
            await File.WriteAllTextAsync(dialog.FileName, JsonSerializer.Serialize(pairing, Protocol.Json), _lifetime.Token);
            Message.Text = "已导出。请在 ExamAware 中导入；配对文件含连接凭据，导入后可删除。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or JsonException)
        { Message.Text = "未能导出，请检查后台连接与保存位置。"; }
        finally { _busy = false; Actions.IsEnabled = PairingActions.IsEnabled = true; }
    }
}
