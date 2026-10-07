using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class SecRandomWindow : Window
{
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private SecRandomState? _state;
    private SecRandomState? _lastKnownState;
    private long _generation;
    private bool _loaded, _busy, _closing;
    public SecRandomWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();
        Closing += (_, e) => { if (!_closing) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _lifetime.Cancel();
        Render(null);
        _ = PollAsync();
    }
    public void Shutdown() { _closing = true; Close(); }
    private string ShowGuidance(string message)
    {
        Message.Text = message;
        if (!_closing)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
        return message;
    }
    public async Task<string> QuickDrawFromSidebarAsync()
    {
        if (_closing || _busy) return ShowGuidance("正在处理 SecRandom 操作，请稍候再闪抽。");
        _generation++;
        _busy = true; Render(_state);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            Render((await RequestAsync("secrandom.status")).SecRandom);
            if (_state is null) return ShowGuidance("无法连接后台。请重启新版 NPEduTools 后再试。");
            if (_state.Error is { } error) return ShowGuidance(error);
            if (string.IsNullOrWhiteSpace(_state.ExecutablePath))
                return ShowGuidance("尚未配置 SecRandom。请选择程序位置并保存，再使用侧栏闪抽。");
            if (_state.Operation?.State == "Unknown")
                return ShowGuidance("上次操作结果待核实。请先查看 SecRandom 窗口及历史，再解除待核实状态。");
            if (_state.Operation?.State == "Running") return ShowGuidance("SecRandom 正在执行操作，请稍候再闪抽。");
            var response = await RequestAsync("secrandom.command", command: new("quick-draw"));
            Render(response.SecRandom);
            if (response.Outcome != "Accepted") return ShowGuidance(response.Message);
            while (_state?.Operation is { State: "Running" } operation && operation.RequestId == response.RequestId)
            {
                await Task.Delay(250, deadline.Token);
                Render((await RequestAsync("secrandom.status")).SecRandom);
            }
            if (_state?.Operation is not { } result || result.RequestId != response.RequestId)
                return ShowGuidance("未能确认本次闪抽回执。请查看 SecRandom 窗口及历史，勿立即重复抽取。");
            Message.Text = result.Message;
            if (result.State != "Succeeded") return ShowGuidance(result.Message);
            return result.Winner is { } winner ? $"SecRandom 闪抽一人 · 上次抽中：{winner.Name}" : "SecRandom 闪抽一人 · 上次已完成";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or TimeoutException or JsonException)
        { Render(null); return ShowGuidance("未收到可靠回执。请核实 SecRandom 窗口及历史，勿立即重复抽取。"); }
        finally { _busy = false; Render(_state); }
    }
    private void Render(SecRandomState? state)
    {
        _state = state;
        if (state is not null) _lastKnownState = state;
        if (!_loaded && state is not null) { Executable.Text = state.ExecutablePath ?? ""; _loaded = true; }
        Connection.Text = state?.Error ?? state?.Connection switch
        {
            "Ready" => "上次检查：SecRandom 接口已就绪", "Unavailable" => "上次检查：接口未就绪，请查看执行结果",
            _ => state is null ? "后台连接未确认，等待重新连接。" : "尚未检查 SecRandom 接口"
        };
        var display = state ?? _lastKnownState;
        CheckedAt.Text = state is null
            ? display?.CheckedAt is { } last ? $"上次接口检查：{last.ToLocalTime():MM-dd HH:mm:ss} · 当前后台状态未知" : "收到后台状态后恢复操作。"
            : state.CheckedAt is { } at ? $"{at.ToLocalTime():MM-dd HH:mm:ss} · 此状态不是实时订阅" : "点击“打开点名页”启动软件，或在软件已运行时检查接口。";
        bool running = state?.Operation?.State == "Running", uncertain = state?.Operation?.State == "Unknown";
        ConfigActions.IsEnabled = !_busy && !running && !uncertain && state is { Error: null };
        bool available = !_busy && !running && !uncertain && state is { ExecutablePath: not null, Error: null };
        OpenButton.IsEnabled = ShowFloatButton.IsEnabled = available;
        HideFloatButton.IsEnabled = DrawButton.IsEnabled = available && state?.Connection == "Ready";
        CheckButton.IsEnabled = !_busy && !running && state is { ExecutablePath: not null, Error: null };
        AcknowledgeButton.Visibility = uncertain ? Visibility.Visible : Visibility.Collapsed;
        AcknowledgeButton.IsEnabled = !_busy && !running && state?.Error is null;
        OperationText.Text = display?.Operation is { } op
            ? (state is null ? "上次已确认的回执（仅供核对，不代表本次结果）：\n" : "") +
                $"{op.UpdatedAt.ToLocalTime():MM-dd HH:mm:ss} · {StateName(op.State)}\n{op.Message}" +
                (op.ErrorCode is null ? "" : $"（{op.ErrorCode}）")
            : state is null ? "当前执行结果未知，请等待连接恢复。" : "尚未执行操作。";
        WinnerText.Text = display?.Operation?.Winner is { } winner ? (state is null ? "上次回执：" : "") + $"{winner.Name} · {winner.Id}" : "";
    }
    private static string StateName(string state) => state switch
    { "Running" => "正在执行", "Succeeded" => "已完成", "Failed" => "未完成", "Unknown" => "结果待核实", "Acknowledged" => "已核实", _ => "未知" };
    private async Task<HostResponse> RequestAsync(string capability, string? path = null, SecRandomCommand? command = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), capability,
            ExecutablePath: path, ExpectedRevision: path is not null || command is not null ? _state?.Revision ?? 0 : null, SecRandom: command), deadline.Token);
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
                    var response = await RequestAsync("secrandom.status");
                    if (!_closing && !_busy && generation == _generation) Render(response.SecRandom);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or TimeoutException or JsonException)
                { if (!_closing && !_busy && generation == _generation) Render(null); }
            }
            try { await Task.Delay(800, _lifetime.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task RunAsync(string? action = null, bool save = false, Guid? acknowledge = null)
    {
        if (_closing || _busy || _state is null) return;
        _generation++;
        _busy = true; Render(_state);
        try
        {
            var response = await RequestAsync(save ? "secrandom.config.set" : "secrandom.command", save ? Executable.Text.Trim() : null,
                save ? null : new(action!, acknowledge));
            Render(response.SecRandom); Message.Text = response.Message;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or TimeoutException or JsonException)
        { Render(null); Message.Text = "未收到后台回执。请刷新执行记录并核实 SecRandom；不要立即重复抽取。"; }
        finally { _busy = false; Render(_state); }
    }
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 SecRandom V3", Filter = "SecRandom V3|SecRandomLauncher.exe;SecRandom.Desktop.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) Executable.Text = dialog.FileName;
    }
    private void DiscoverClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\secrandom\shell\open\command");
            string? line = key?.GetValue("") as string;
            int end = line?.StartsWith('"') == true ? line.IndexOf('"', 1) : -1;
            if (end <= 1) { Message.Text = "没有找到带引号的 SecRandom URL 登记，请浏览选择程序。"; return; }
            Executable.Text = line![1..end]; Message.Text = "已填入 URL 登记位置；请核对后保存。";
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Message.Text = "无法读取 URL 登记，请浏览选择程序。"; }
    }
    private async void SaveClicked(object sender, RoutedEventArgs e) => await RunAsync(save: true);
    private async void OpenClicked(object sender, RoutedEventArgs e) => await RunAsync("open");
    private async void ShowFloatClicked(object sender, RoutedEventArgs e) => await RunAsync("show-float");
    private async void HideFloatClicked(object sender, RoutedEventArgs e) => await RunAsync("hide-float");
    private async void CheckClicked(object sender, RoutedEventArgs e) => await RunAsync("check");
    private async void DrawClicked(object sender, RoutedEventArgs e) => await RunAsync("quick-draw");
    private async void AcknowledgeClicked(object sender, RoutedEventArgs e)
    {
        if (_state?.Operation is not { State: "Unknown" } op) return;
        if (MessageBox.Show(this, "请先在 SecRandom 核实窗口和抽取历史，确认上次请求是否完成。\n解除后可以发起新的操作，但不会重新执行旧请求。确认已经核实？",
            "核实操作结果", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await RunAsync("acknowledge", acknowledge: op.RequestId);
    }
}
