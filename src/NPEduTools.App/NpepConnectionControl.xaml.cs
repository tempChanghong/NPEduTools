using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class NpepConnectionControl : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private NpepConnectionSession? _session;
    private int _providerRevision;
    private int _classSelectionRevision;
    private int _classInputRevision;
    private bool _wasBound, _wasOccupied;
    private (string? Origin, string Instance, string Epoch, string? Binding, string? Device)? _replacementTarget;
    private bool _diagnosticBusy;
    private int _diagnosticViewRevision;
    private CancellationTokenSource? _comparisonCancellation;
    private string? _comparisonBindingContext;
    public NpepConnectionControl()
    {
        InitializeComponent();
        DataContext = null;
        RequestedClass.SelectionChanged += (_, _) => { ClearClassInput(); UpdateClassJourneyLayout(); };
        ClassJourneyGrid.SizeChanged += (_, _) => UpdateClassJourneyLayout();
        ClassManagementSecret.PasswordChanged += (_, _) => ClearClassMessage();
        ClassManagementSecret.IsVisibleChanged += (_, _) => { if (!ClassManagementSecret.IsVisible) ClearClassInput(); };
        ReplacementSecret.PasswordChanged += (_, _) => ReplacementMessage.Text = "";
        ReplacementSecret.IsVisibleChanged += (_, _) => { if (!ReplacementSecret.IsVisible) ClearReplacementInput(); };
        _timer.Tick += async (_, _) => { if (IsVisible && _session is { } session) await session.RefreshAsync(); };
        Loaded += (_, _) => { ObserveSession(); _timer.Start(); };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            if (_session is { } session) session.PropertyChanged -= SessionChanged;
            LeaveClassSelection();
            ClearComparison();
        };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsLoaded) return;
            if (!IsVisible) LeaveClassSelection();
            else if (_session is { } session) await session.RefreshAsync();
        };
    }
    private void UpdateClassJourneyLayout()
    {
        bool split = Window.GetWindow(this) is MainWindow && ClassJourneyGrid.ActualWidth >= 800 && RequestedClass.SelectedItem is not null;
        ClassJourneyGap.Width = new GridLength(split ? 12 : 0);
        ClassActionColumn.Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ClassActionCard, split ? 0 : 1);
        Grid.SetColumn(ClassActionCard, split ? 2 : 0);
        ClassActionHeading.Visibility = ClassActionDivider.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        bool inlineApplication = !split && ClassJourneyGrid.ActualWidth >= 580 &&
            RequestedClass.SelectedItem is NpepConnectionSession.ClassChoice { Preauthorized: not false };
        Grid.SetRow(ClassApplyAction, inlineApplication ? 0 : 1);
        Grid.SetColumn(ClassApplyAction, inlineApplication ? 1 : 0);
        ClassApplyAction.Margin = inlineApplication ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }
    public void Bind(NpepConnectionSession session)
    {
        if (_session is { } previous)
        {
            previous.PropertyChanged -= SessionChanged;
            previous.LeaveClassSelection();
        }
        ClearComparison(); ClearClassSelection();
        ClearReplacementInput();
        _session = session; _providerRevision = session.ProviderRevision;
        _classSelectionRevision = session.ClassSelectionRevision; DataContext = session;
        _wasBound = session.ShowBindingManagement;
        _wasOccupied = session.ScreenOccupied;
        _replacementTarget = session.ReplacementTarget;
        if (IsLoaded) ObserveSession();
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
        if (_session is { } session && (session.ProviderRevision != _providerRevision || session.ClassSelectionRevision != _classSelectionRevision))
        {
            _providerRevision = session.ProviderRevision;
            _classSelectionRevision = session.ClassSelectionRevision;
            ClearClassSelection();
        }
        if (_comparisonBindingContext is { } context && context != ConnectionContext()) ClearComparison();
        if (_session is { } current)
        {
            if (_replacementTarget != current.ReplacementTarget)
            {
                _replacementTarget = current.ReplacementTarget;
                ClearReplacementInput();
            }
            bool showResult = (!_wasBound && current.ShowBindingManagement) || (!_wasOccupied && current.ScreenOccupied);
            _wasBound = current.ShowBindingManagement;
            _wasOccupied = current.ScreenOccupied;
            if (showResult && IsVisible)
                Dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(current, _session) && IsVisible && (current.ShowBindingManagement || current.ScreenOccupied))
                        ConnectionSummary.BringIntoView();
                }, DispatcherPriority.Loaded);
        }
    }
    private void ClearClassSelection()
    {
        RequestedClass.SelectedIndex = -1;
        ClearClassInput();
    }
    private void LeaveClassSelection()
    {
        ClearClassSelection();
        ClearReplacementInput();
        ClassLookupPanel.IsExpanded = false;
        _session?.LeaveClassSelection();
    }
    private void ClearReplacementInput()
    {
        ReplacementSecret.Clear();
        ReplacementMessage.Text = "";
    }
    private void ClearClassInput()
    {
        ClassManagementSecret.Clear();
        ClearClassMessage();
    }
    private void ClearClassMessage()
    {
        _classInputRevision++;
        ClassInputMessage.Text = "";
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
    private void EditSchoolClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { CanEdit: true } session) return;
        session.ServerConfirmed = false;
        Dispatcher.InvokeAsync(() => { if (OriginInput.IsVisible) OriginInput.Focus(); }, DispatcherPriority.Input);
    }
    private void SchoolReviewed(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_session?.SchoolVerified != true || !IsVisible) return;
            if (RequestedClass.IsVisible) RequestedClass.Focus();
            else if (ClassLookupPanel.IsVisible) ClassLookupPanel.MoveFocus(new(System.Windows.Input.FocusNavigationDirection.First));
            else WebPairingOptions.MoveFocus(new(System.Windows.Input.FocusNavigationDirection.First));
        }, DispatcherPriority.Input);
    }
    private async void FindClassClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.FindClassAsync(); }
    private async void PreviousClassPageClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.FindPreviousClassPageAsync(); }
    private async void NextClassPageClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.FindNextClassPageAsync(); }
    private async void RetryClassPageClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.RetryClassPageAsync(); }
    private async void PairClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.PairAsync(); }
    private async void ClaimClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.ClaimAsync(); }
    private async void ApplyClassClicked(object sender, RoutedEventArgs e) => await ConnectSelectedClassAsync(null);
    private async void PasswordClassClicked(object sender, RoutedEventArgs e)
    {
        string secret = ClassManagementSecret.Password;
        ClearClassInput();
        await ConnectSelectedClassAsync(secret);
    }
    private async Task ConnectSelectedClassAsync(string? secret)
    {
        if (_session is not { } session) return;
        ClearClassMessage();
        int revision = _classInputRevision;
        if (RequestedClass.SelectedValue is not string binding)
            ClassInputMessage.Text = "请先选择要连接的班级与大屏。";
        else if (secret is not null && !NoiseManagementContract.SecretValid(secret))
            ClassInputMessage.Text = "请输入 8 至 128 位管理口令。";
        else if (!await session.ConnectClassAsync(binding, secret) && revision == _classInputRevision)
            ClassInputMessage.Text = session.Message;
    }
    private async void ConfirmClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.ConfirmAsync(); }
    private async void RequestReplacementClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { CanRequestReplacement: true } session) return;
        string secret = ReplacementSecret.Password;
        ClearReplacementInput();
        if (!NoiseManagementContract.SecretValid(secret))
        {
            ReplacementMessage.Text = "请输入 8 至 128 位班级管理口令。";
            ReplacementSecret.Focus();
            return;
        }
        await session.RequestReplacementAsync(secret);
    }
    private async void RecoverClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.RecoverAsync(); }
    private async void PauseClicked(object sender, RoutedEventArgs e) { if (_session is { } session) await session.PauseOrResumeAsync(); }
    private async void UnpairClicked(object sender, RoutedEventArgs e) => await ConfirmUnpairAsync(false);
    private async void ChangeProviderClicked(object sender, RoutedEventArgs e) => await ConfirmUnpairAsync(true);
    private async Task ConfirmUnpairAsync(bool changeProvider)
    {
        if (_session is not { CanUnpair: true, Revision: { } revision } session) return;
        string introduction = changeProvider ? "更换服务提供商需要先取消当前申请或解除绑定，之后填写新地址并重新配对。\n\n" : "";
        bool cancellingReplacement = session.CancellingReplacement;
        string message = cancellingReplacement
            ? "将取消这台新机的换机申请，并清除本机保存的候选凭据。继续取消吗？"
            : "将停止学校通知、远程操作和状态上报，并删除本机配对凭据。若远端无法连接，还需要学校管理员撤销登记。\n继续取消申请或解除绑定吗？";
        if (MessageBox.Show(Window.GetWindow(this), introduction + message,
            changeProvider ? "更换服务提供商" : cancellingReplacement ? "取消换机申请" : "解除学校互联", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
