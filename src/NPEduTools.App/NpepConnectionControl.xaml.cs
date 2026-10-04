using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NPEduTools.App;

public partial class NpepConnectionControl : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NpepConnectionSession? _session;
    private bool _exporting;
    public NpepConnectionControl()
    {
        InitializeComponent();
        DataContext = null;
        _timer.Tick += async (_, _) => { if (IsVisible && _session is { } session) await session.RefreshAsync(); };
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += async (_, _) => { if (IsLoaded && IsVisible && _session is { } session) await session.RefreshAsync(); };
    }
    public void Bind(NpepConnectionSession session) { _session = session; DataContext = session; }
    private async void RefreshClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.RefreshAsync(); }
    private async void InspectClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.InspectAsync(); }
    private async void PairClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.PairAsync(); }
    private async void ClaimClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.ClaimAsync(); }
    private async void ConfirmClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.ConfirmAsync(); }
    private async void RecoverClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.RecoverAsync(); }
    private async void PauseClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.PauseOrResumeAsync(); }
    private async void UnpairClicked(object sender, RoutedEventArgs e) => await ConfirmUnpairAsync(false);
    private async void ChangeProviderClicked(object sender, RoutedEventArgs e) => await ConfirmUnpairAsync(true);
    private async Task ConfirmUnpairAsync(bool changeProvider)
    {
        if (_session is not { CanUnpair: true, Revision: { } revision } session) return;
        string introduction = changeProvider ? "更换服务提供商需要先取消当前申请或解除绑定，之后填写新地址并重新配对。\n\n" : "";
        if (MessageBox.Show(Window.GetWindow(this), introduction + "将停止学校通知、远程操作和状态上报，并删除本机配对凭据。若远端无法连接，还需要学校管理员撤销登记。\n继续取消申请或解除绑定吗？",
            changeProvider ? "更换服务提供商" : "解除学校互联", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await session.UnpairAsync(revision);
    }
    private void CopyCodeClicked(object sender, RoutedEventArgs e)
    {
        if (_session?.PairingCode is not { Length: > 0 } code) return;
        try { Clipboard.SetText(code); LocalMessage.Text = "已复制配对码，仅提供给学校管理员。"; }
        catch (Exception error) when (error is ExternalException or InvalidOperationException)
        { LocalMessage.Text = "暂时无法复制，请手动记录配对码。"; }
    }
    private async void ExportDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (_exporting || _session is not { CanRefresh: true } session) return;
        _exporting = true;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出学校互联诊断摘要", Filter = "JSON 诊断摘要 (*.json)|*.json",
                DefaultExt = ".json", AddExtension = true,
                FileName = $"NPEduTools-NPEP-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            string version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
            var summary = await session.CollectDiagnosticsAsync(version);
            if (summary is null) { LocalMessage.Text = "正在处理其他操作或窗口已关闭，请稍后再导出。"; return; }
            await System.IO.File.WriteAllTextAsync(dialog.FileName, summary.ToJsonString(new() { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            LocalMessage.Text = "诊断摘要已保存；缺失项不代表正常，请核对观测时间后再分享。文件未自动上传。";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { LocalMessage.Text = "无法保存诊断摘要，请检查目标目录权限或磁盘连接后重试。"; }
        finally { _exporting = false; }
    }
}
