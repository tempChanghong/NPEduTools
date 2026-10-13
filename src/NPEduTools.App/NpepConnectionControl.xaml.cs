using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace NPEduTools.App;

public partial class NpepConnectionControl : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NpepConnectionSession? _session;
    private bool _diagnosticBusy;
    private int _diagnosticViewRevision;
    private CancellationTokenSource? _comparisonCancellation;
    private string? _comparisonBindingContext;
    public NpepConnectionControl()
    {
        InitializeComponent();
        DataContext = null;
        _timer.Tick += async (_, _) => { if (IsVisible && _session is { } session) await session.RefreshAsync(); };
        Loaded += (_, _) => { ObserveSession(); _timer.Start(); };
        Unloaded += (_, _) => { _timer.Stop(); if (_session is { } session) session.PropertyChanged -= SessionChanged; ClearComparison(); };
        IsVisibleChanged += async (_, _) => { if (IsLoaded && IsVisible && _session is { } session) await session.RefreshAsync(); };
    }
    public void Bind(NpepConnectionSession session)
    {
        if (_session is { } previous) previous.PropertyChanged -= SessionChanged;
        ClearComparison(); _session = session; DataContext = session;
        if (IsLoaded) ObserveSession();
    }
    internal void UseGuidedSetup()
    {
        ServerIdentityDetails.IsExpanded = false;
        WebPairingOptions.IsExpanded = false;
    }
    private void ObserveSession()
    {
        if (_session is not { } session) return;
        session.PropertyChanged -= SessionChanged;
        session.PropertyChanged += SessionChanged;
    }
    private string ConnectionContext() => (_session?.BoundOrigin ?? "") + "\n" + (_session?.BindingIdentity ?? "");
    private void SessionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_comparisonBindingContext is { } context && context != ConnectionContext()) ClearComparison();
    }
    private void ClearComparison()
    {
        _diagnosticViewRevision++;
        _comparisonCancellation?.Cancel();
        _comparisonBindingContext = null;
        ComparisonMessage.Text = "";
        ComparisonPanel.Visibility = Visibility.Collapsed;
    }
    private static string AppVersion() => typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
    private async void RefreshClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.RefreshAsync(); }
    private async void InspectClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.InspectAsync(); }
    private async void PairClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.PairAsync(); }
    private async void ClaimClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.ClaimAsync(); }
    private async void ApplyClassClicked(object sender,RoutedEventArgs e)
    {if(_session is {} session && !await session.ConnectClassAsync(RequestedClass.SelectedValue as string,null))LocalMessage.Text="请选择申请连接的班级并核对学校服务。";}
    private async void PasswordClassClicked(object sender,RoutedEventArgs e)
    {
        string secret=ClassManagementSecret.Password;ClassManagementSecret.Clear();
        if(_session is {} session && !await session.ConnectClassAsync(RequestedClass.SelectedValue as string,secret))LocalMessage.Text="请选择预授权班级，输入8至128位管理口令并核对学校服务。";
    }
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
        {
            ClearComparison();
            await session.UnpairAsync(revision);
        }
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
        if (_diagnosticBusy || _session is not { CanRefresh: true } session) return;
        _diagnosticBusy = true;
        int viewRevision = _diagnosticViewRevision;
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出学校互联诊断摘要", Filter = "JSON 诊断摘要 (*.json)|*.json",
                DefaultExt = ".json", AddExtension = true,
                FileName = $"NPEduTools-NPEP-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            var summary = await session.CollectDiagnosticsAsync(AppVersion());
            if (!IsLoaded || viewRevision != _diagnosticViewRevision) return;
            if (summary is null) { LocalMessage.Text = "正在处理其他操作或窗口已关闭，请稍后再导出。"; return; }
            await System.IO.File.WriteAllTextAsync(dialog.FileName, summary.ToJsonString(new() { WriteIndented = true }), new System.Text.UTF8Encoding(false));
            LocalMessage.Text = "诊断摘要已保存；缺失项不代表正常，请核对观测时间后再分享。文件未自动上传。";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { LocalMessage.Text = "无法保存诊断摘要，请检查目标目录权限或磁盘连接后重试。"; }
        finally { _diagnosticBusy = false; }
    }
    private async void CompareDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        if (_diagnosticBusy || _session is not { CanRefresh: true } session) return;
        ClearComparison();
        _diagnosticBusy = true;
        int viewRevision = _diagnosticViewRevision;
        using var cancellation = new CancellationTokenSource();
        _comparisonCancellation = cancellation;
        bool Current() => IsLoaded && viewRevision == _diagnosticViewRevision && ReferenceEquals(session, _session);
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择学校运维概览导出的诊断摘要", Filter = "JSON 诊断摘要 (*.json)|*.json",
                CheckFileExists = true, Multiselect = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true || !Current()) return;
            await using var input = new System.IO.FileStream(dialog.FileName, System.IO.FileMode.Open, System.IO.FileAccess.Read,
                System.IO.FileShare.Read, 32768, useAsync: true);
            var school = await NpepDiagnosticComparison.ReadSchoolSummaryAsync(input, cancellation.Token);
            _comparisonBindingContext = ConnectionContext();
            var desktop = await session.CollectDiagnosticsAsync(AppVersion(), cancellation.Token);
            if (!Current()) return;
            if (desktop is null) { LocalMessage.Text = "后台正在处理其他操作，请稍后再对照。"; return; }
            ComparisonMessage.Text = NpepDiagnosticComparison.Compare(desktop, school, DateTimeOffset.UtcNow).DisplayText;
            ComparisonPanel.Visibility = Visibility.Visible;
            LocalMessage.Text = "对照完成。文件未上传；结果不会自动刷新，重新对照会再次读取本机缓存。";
        }
        catch (OperationCanceledException) { }
        catch (System.IO.InvalidDataException)
        { if (Current()) LocalMessage.Text = "文件不是支持的学校诊断摘要，或超过 2 MiB。请在学校运维概览重新导出 JSON，原文件未修改。"; }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { if (Current()) LocalMessage.Text = "无法读取学校诊断摘要，请检查文件和磁盘权限后重试。"; }
        finally
        {
            if (ReferenceEquals(_comparisonCancellation, cancellation)) _comparisonCancellation = null;
            _diagnosticBusy = false;
        }
    }
}
