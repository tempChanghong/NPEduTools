using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class NoiseCalibrationWindow : Window
{
    private sealed record Choice(string Id, string Name);
    private sealed record SampleRow(Guid Id, string Description);
    private static readonly Choice[] Scenes = [
        new("RelativeQuiet", "教室相对安静"), new("NormalStudy", "正常自习 · 翻书、书写"),
        new("LowConversation", "少量同学低声交流"), new("SustainedConversation", "多人持续聊天"),
        new("AdministratorVoice", "管理员约三米处正常说话")];
    private static readonly Choice[] Labels = [
        new("Acceptable", "可接受"), new("Visual", "希望出现视觉提醒"), new("Discipline", "希望出现纪律提醒")];
    private NoiseProtectionState? _access;
    private const string ConnectionLost = "后台连接不可用，请刷新；旧状态不能用于保存。";
    private const string CommandUnconfirmed = "结果未确认，请刷新后核对；不会自动重复采样或保存。";
    private async Task ReadAccessAsync(CancellationToken token, long stamp)
    {
        var reply = await HostClient.RequestAsync(_pipe, "noise.management.status", token);
        if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
        ShowAccess(reply.NoiseProtection);
    }
    private void ShowAccess(NoiseProtectionState? state)
    {
        if (_access is not null && (state?.ManagementSource != _access.ManagementSource ||
            state?.CredentialRevision != _access.CredentialRevision))
            ManagementSecret.Clear();
        _access = state;
        ManagementLabel.Text = CalibrationAccess.Label(_access); ManagementHint.Text = CalibrationAccess.Hint(_access);
        CredentialsExpander.Header = state is { Configured: false, ManagementSource: "Local", ErrorCode: null }
            ? "设置本机管理口令" : "管理口令与设备说明";
        UpdateButtons();
    }
    private readonly CalibrationUiOrder _order = new();
    private long _activeRead;
    private CancellationTokenSource? _readCancellation;
    private CancellationTokenSource? _refreshCancellation;
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NoiseCalibrationState? _state;
    private NoiseCalibrationState? _draftOrigin;
    private bool _busy, _reading, _connected, _verifying;
    private bool _started, _review, _draftOutOfScope;
    public NoiseCalibrationWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent(); DesktopDialogSizing.Attach(this, pipe);
        SceneChoice.ItemsSource = Scenes; SceneChoice.SelectedIndex = 0;
        LabelChoice.ItemsSource = Labels; LabelChoice.SelectedIndex = 0;
        ManagementSecret.PasswordChanged += (_, _) =>
        {
            string label = ManagementSecret.Password.Length == 0 ? "刷新状态" : "刷新并验证口令";
            RefreshStatus.Content = label;
            System.Windows.Automation.AutomationProperties.SetName(RefreshStatus, label);
        };
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += (_, _) =>
        {
            _started = true;
            if (WorkspacePanel.Visible(this)) WorkspaceVisibilityChanged(true);
        };
        Closed += (_, _) => { _timer.Stop(); _order.Advance(); _lifetime.Cancel(); ManagementSecret.Clear(); };
        UpdateButtons();
    }

    internal void WorkspaceVisibilityChanged(bool visible)
    {
        if (!_started || _lifetime.IsCancellationRequested) return;
        if (visible)
        {
            _connected = false;
            UpdateButtons();
            _timer.Start();
            _ = ReadAsync();
            return;
        }

        _timer.Stop();
        _readCancellation?.Cancel();
        _refreshCancellation?.Cancel();
        // Leave an already-sent calibration command alone. Returning reads its original draft.
        if (!_busy || _verifying) _order.Advance();
        _connected = false;
        ManagementSecret.Clear();
        CredentialsExpander.IsExpanded = false;
        UpdateButtons();
    }

    private void CalibrationLayoutChanged(object sender, SizeChangedEventArgs e) => ArrangeCalibration(e.NewSize.Width);
    private void SamplingTabClicked(object sender, RoutedEventArgs e) => ShowStage(false);
    private void ThresholdTabClicked(object sender, RoutedEventArgs e) => ShowStage(true);
    private void ShowStage(bool review)
    {
        _review = review;
        ArrangeCalibration(CalibrationRoot.ActualWidth);
        CalibrationScroll.ScrollToTop();
    }

    private void ArrangeCalibration(double width)
    {
        if (ThresholdPane is null || CompactTabs is null) return;
        bool wide = width >= 744 && WorkspacePanel.DialogOwner(this).ActualWidth >= 930;
        CalibrationLayout.ColumnDefinitions[1].Width = new GridLength(wide ? 1 : 0, GridUnitType.Star);
        Grid.SetColumn(ThresholdPane, wide ? 1 : 0);
        ThresholdPane.Margin = wide ? new Thickness(12, 0, 0, 0) : new Thickness();
        CompactTabs.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        SamplingPane.Visibility = wide || !_review ? Visibility.Visible : Visibility.Collapsed;
        ThresholdPane.Visibility = wide || _review ? Visibility.Visible : Visibility.Collapsed;
        ThresholdHeading.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        SamplingTab.Tag = _review ? null : "active";
        ThresholdTab.Tag = _review ? "active" : null;
        System.Windows.Automation.AutomationProperties.SetItemStatus(SamplingTab, _review ? "" : "当前页");
        System.Windows.Automation.AutomationProperties.SetItemStatus(ThresholdTab, _review ? "当前页" : "");
    }

    private Task ReadAsync() => ReadCoreAsync(false);
    private async Task ReadCoreAsync(bool force, CancellationToken cancellationToken = default)
    {
        if ((!force && (_reading || _busy)) || _lifetime.IsCancellationRequested) return;
        long stamp = _order.Advance(); _activeRead = stamp; _reading = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _readCancellation = deadline;
        try
        {
            deadline.CancelAfter(3500);
            await ReadAccessAsync(deadline.Token, stamp);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            var r = await HostClient.RequestAsync(_pipe, "noise.calibration.status", deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            _connected = r.Outcome == "Succeeded" && r.NoiseCalibration is not null;
            if (_connected)
            {
                if (OperationMessage.Text == ConnectionLost) OperationMessage.Text = "";
                if (OperationMessage.Text == CommandUnconfirmed)
                    OperationMessage.Text = "已重新读取后台状态，请核对原草稿、采样记录或当前阈值。";
                Render(r.NoiseCalibration!);
            }
            else OperationMessage.Text = CalibrationAccess.Message(r.ErrorCode, r.Message, _access);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        {
            if (_order.Current(stamp) && !_lifetime.IsCancellationRequested)
            {
                _connected = false;
                if (OperationMessage.Text != CommandUnconfirmed) OperationMessage.Text = ConnectionLost;
            }
        }
        finally
        {
            if (ReferenceEquals(_readCancellation, deadline)) _readCancellation = null;
            if (_activeRead == stamp) _reading = false;
            UpdateButtons();
        }
    }
    internal void Render(NoiseCalibrationState state)
    {
        // Validate before caching or rendering. Unsupported sample identifiers
        // and incomplete suggestions must not reach First()/nullable Value or
        // be advertised as a calibration that can be confirmed.
        if (state.Samples is null || state.Suggestion is null || state.Samples.Any(sample => sample is null ||
            !NoiseCalibrationContract.Scenes.Contains(sample.Scene) || !NoiseCalibrationContract.Labels.Contains(sample.Label)) ||
            state.Suggestion.Ready && !NoiseCalibrationContract.Thresholds(state.Suggestion.MediumDbfs, state.Suggestion.HighDbfs))
            throw new InvalidDataException("校准状态格式不兼容，请刷新或更新后台。");
        bool scopeChanged = _state is { } previous && (previous.InstanceId != state.InstanceId ||
            previous.DraftId != state.DraftId || previous.DeviceId != state.DeviceId || previous.Context != state.Context);
        bool wasOutOfScope = _draftOutOfScope;
        if (_draftOrigin?.DraftId != state.DraftId) _draftOrigin = state.DraftId is null ? null : state;
        _draftOutOfScope = _draftOrigin is { } origin && (origin.DeviceId != state.DeviceId || origin.Context != state.Context);
        bool samplesChanged = _state?.Revision != state.Revision || scopeChanged || wasOutOfScope != _draftOutOfScope;
        if (scopeChanged)
        {
            MediumThreshold.Clear();
            HighThreshold.Clear();
        }
        _state = state;
        DeviceStatus.Text = state.DeviceId is null ? "请先在自习设备中保存麦克风" :
            state.DeviceName is { } name ? "监测麦克风：" + name : "当前监测麦克风已选择";
        bool invalidProfile = state.Profile is { } profile && (profile.DeviceId != state.DeviceId ||
            state.Context is not null && profile.Context != state.Context);
        ProfileStatus.Text = state.Error is not null ? "快速校准不可用 · 原文件保留" : state.State == "Busy" ?
            "采集占用或运行保护生效 · 暂不可校准" : state.Profile is null ? "未校准 · 尚未确认本机阈值" : invalidProfile ?
            "校准已失效 · 麦克风或学校配置已变化，请重新校准" : state.Applied ?
            "已快速校准 · 本机阈值适用" :
            "已快速校准 · 结果已保存；当前设备、学校配置或采集设置尚未匹配，暂不应用";
        SamplingProgress.Value = state.ProgressSeconds;
        SamplingStatus.Text = state.State == "Sampling" ? $"采样中 · {state.ProgressSeconds:F1} / 15 秒。返回后仍可核对本段。" :
            state.State == "Busy" ? "当前采集占用或运行保护生效（准备／自习／考试、暂停或状态未确认），暂不可校准。已有样本和阈值保留。" :
            _draftOutOfScope ? "设备或学校配置已变化，请新建校准草稿。" :
            state.DraftId is null ? "请先新建校准草稿" : $"已记录 {state.Samples.Count} 段，可继续补采。";
        IReadOnlyList<NoiseCalibrationSample> samples = _draftOutOfScope ? [] : state.Samples;
        bool acceptable = HasGoodSample(samples, "Acceptable");
        bool discipline = HasGoodSample(samples, "Discipline");
        SampleCoverage.Text = $"可接受：{(acceptable ? "已记录" : "待采")}  ·  纪律提醒：{(discipline ? "已记录" : "待采")}";
        if (samplesChanged)
        {
            var selected = (SampleList.SelectedItem as SampleRow)?.Id;
            SampleList.ItemsSource = samples.Select(s => new SampleRow(s.Id,
                $"{Scenes.First(x => x.Id == s.Scene).Name} · {Labels.First(x => x.Id == s.Label).Name}\n" +
                $"P10 / 中位 / P90：{Level(s.P10Dbfs)} / {Level(s.MedianDbfs)} / {Level(s.P90Dbfs)} dBFS　覆盖 {s.Coverage:P0}　削波 {s.ClippedPercent:F2}%　{Quality(s.Quality)}")).ToArray();
            if (selected is not null) SampleList.SelectedItem = ((IEnumerable<SampleRow>)SampleList.ItemsSource).FirstOrDefault(s => s.Id == selected);
        }
        AppliedValues.Text = state.Applied && !invalidProfile && state.Profile is { } applied
            ? FormatThresholds(applied.MediumDbfs, applied.HighDbfs) : "当前未应用本机校准";
        SuggestedValues.Text = !_draftOutOfScope && state.Suggestion.Ready
            ? FormatThresholds(state.Suggestion.MediumDbfs, state.Suggestion.HighDbfs) : "暂无建议";
        SuggestionStatus.Text = _draftOutOfScope ? "请先为当前范围新建草稿。" : state.Suggestion.Message;
        if (state.Error is not null) OperationMessage.Text = "校准文件暂不可用，原文件已保留；请联系管理员。";
        UpdateButtons();
    }
    private static string Level(double? value) => value?.ToString("F1", CultureInfo.CurrentCulture) ?? "—";
    private static string FormatThresholds(double? medium, double? high) =>
        $"视觉 {Level(medium)} · 纪律 {Level(high)} dBFS";
    private static bool HasGoodSample(IEnumerable<NoiseCalibrationSample> samples, string label) =>
        samples.Any(sample => sample.Label == label && sample.Quality == "Good");
    private static string Quality(string value) => value switch { "Good" => "有效", "Clipping" => "削波，需重采", "Interrupted" => "已中断，需重采", "Incomplete" => "采样不足，需重采", _ => "信号异常，需重采" };
    private void UpdateButtons()
    {
        if (BeginDraft is null) return;
        bool idle = !_busy && _connected && _state is { State: "Ready", Error: null };
        RefreshStatus.IsEnabled = !_busy && !_verifying && !_lifetime.IsCancellationRequested;
        ConfigureManagement.IsEnabled = !_busy && !_verifying && _access?.ManagementSource != "School";
        bool draft = idle && !_draftOutOfScope && _state!.DraftId is not null;
        BeginDraft.IsEnabled = idle && _state!.DeviceId is not null;
        string draftStyle = _state?.DraftId is null || _draftOutOfScope ? "TeacherPrimary" : "TeacherQuiet";
        if (!Equals(BeginDraft.Tag, draftStyle))
        {
            BeginDraft.SetResourceReference(StyleProperty, draftStyle);
            BeginDraft.Tag = draftStyle;
        }
        StartSample.IsEnabled = draft && _state!.Samples.Count < 20;
        CancelSample.IsEnabled = !_busy && _connected && _state is { State: "Sampling" };
        RemoveSample.IsEnabled = RelabelSample.IsEnabled = draft && SampleList.SelectedItem is SampleRow;
        ConfirmThresholds.IsEnabled = draft && _state!.Context is not null &&
            HasGoodSample(_state.Samples, "Acceptable") && HasGoodSample(_state.Samples, "Discipline");
        ClearThresholds.IsEnabled = draft && _state!.Profile is not null;
        UseSuggestion.IsEnabled = draft && _state!.Suggestion.Ready;
        if (!_connected)
        {
            ProfileStatus.Text = "校准状态待确认 · 旧信息不能证明当前已应用";
            SamplingStatus.Text = "采样状态待确认，请刷新核对；未宣称后台采样已停止。";
            AppliedValues.Text = "待核对";
            SuggestedValues.Text = "连接恢复后核对";
        }
    }
    private async Task SendAsync(string action, string? scene = null, string? label = null, Guid? sample = null, double? medium = null, double? high = null,
        NoiseCalibrationState? confirmedState = null)
    {
        if (_busy || _verifying || _state is null || _lifetime.IsCancellationRequested ||
            WorkspacePanel.Embedded(this) && (!_connected || _draftOutOfScope && action is not ("begin" or "cancel"))) return;
        var expected = confirmedState ?? _state;
        var c = new NoiseCalibrationCommand(action, expected.InstanceId, expected.Revision, ManagementSecret.Password,
            action == "begin" ? null : expected.DraftId, scene, label, sample, medium, high);
        if (!NoiseCalibrationContract.Valid(c))
        {
            if (!NoiseManagementContract.SecretValid(c.Secret))
            {
                OperationMessage.Text = "";
                RequestCredential();
            }
            else OperationMessage.Text = "请核对阈值或场景选择。";
            return;
        }
        long stamp = _order.Advance(); _busy = true; UpdateButtons();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(5000);
            var r = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.calibration.command", NoiseCalibration: c), deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            _connected = r.NoiseCalibration is not null && (!WorkspacePanel.Embedded(this) || WorkspacePanel.Visible(this));
            if (r.NoiseProtection is not null) ShowAccess(r.NoiseProtection);
            if (r.NoiseCalibration is { } state) Render(state);
            // Progress belongs to the live sampling row, so an old start reply cannot
            // continue to say “sampling” after the bounded recording has finished.
            OperationMessage.Text = action == "start" && r.Outcome is "Accepted" or "Succeeded"
                ? "" : CalibrationAccess.Message(r.ErrorCode, r.Message, _access);
            if (r.Outcome is "Accepted" or "Succeeded") CredentialsExpander.IsExpanded = false;
            if (action == "confirm" && r.Outcome == "Succeeded" && r.NoiseCalibration?.Profile is { } saved &&
                saved.MediumDbfs == medium && saved.HighDbfs == high &&
                double.TryParse(MediumThreshold.Text, out double editMedium) && editMedium == medium &&
                double.TryParse(HighThreshold.Text, out double editHigh) && editHigh == high)
            {
                MediumThreshold.Clear();
                HighThreshold.Clear();
            }
            if (r.ErrorCode is "MANAGEMENT_SECRET_INVALID" or "MANAGEMENT_NOT_CONFIGURED") RequestCredential();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (_order.Current(stamp) && !_lifetime.IsCancellationRequested) { _connected = false; OperationMessage.Text = CommandUnconfirmed; } }
        finally { _busy = false; UpdateButtons(); }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _verifying || _lifetime.IsCancellationRequested) return;
        _busy = _verifying = true; _order.Advance(); UpdateButtons();
        using var refresh = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshCancellation = refresh;
        try
        {
            await ReadCoreAsync(true, refresh.Token);
            if (refresh.IsCancellationRequested || WorkspacePanel.Embedded(this) && ManagementSecret.Password.Length == 0) return;
            long stamp = _order.Advance();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(refresh.Token);
            deadline.CancelAfter(3500);
            try
            {
                var reply = await CalibrationAccess.VerifyAsync(_pipe, ManagementSecret.Password, deadline.Token);
                if (refresh.IsCancellationRequested || !_order.Current(stamp)) return;
                ShowAccess(reply.NoiseProtection ?? _access);
                OperationMessage.Text = reply.Outcome == "Succeeded" ? "管理口令验证通过。" :
                    CalibrationAccess.Message(reply.ErrorCode, reply.Message, _access);
                if (reply.ErrorCode is "MANAGEMENT_SECRET_INVALID" or "MANAGEMENT_NOT_CONFIGURED") RequestCredential();
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
            {
                if (!refresh.IsCancellationRequested && _order.Current(stamp))
                    OperationMessage.Text = "管理验证响应未确认，请检查后台连接后重试。";
            }
        }
        finally
        {
            _refreshCancellation = null;
            _busy = _verifying = false;
            UpdateButtons();
        }
    }
    private async void ManagementClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _verifying || _lifetime.IsCancellationRequested) return;
        _busy = true; _order.Advance(); UpdateButtons();
        try { await NoiseManagementDialog.ConfigureAsync(WorkspacePanel.DialogOwner(this), _pipe); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { _connected = false; OperationMessage.Text = "无法读取后台管理设置，请检查后台连接后重试；未修改口令。"; return; }
        finally { _busy = false; UpdateButtons(); }
        await ReadCoreAsync(true); UpdateButtons();
    }
    private async Task BeginDraftAsync(Func<bool> confirm)
    {
        if (_busy || _verifying || _state is null || _lifetime.IsCancellationRequested) return;
        // Status polls continue inside a modal dialog; consent belongs to this snapshot.
        var confirmedState = _state;
        if (confirmedState.Samples.Count > 0 && !confirm()) return;
        await SendAsync("begin", confirmedState: confirmedState);
    }
    private async void BeginClicked(object sender, RoutedEventArgs e) =>
        await BeginDraftAsync(() => MessageBox.Show(WorkspacePanel.DialogOwner(this), "新草稿会替换已有采样草稿，已确认阈值保留。继续吗？",
            "新建校准草稿", MessageBoxButton.OKCancel) == MessageBoxResult.OK);
    private async void StartClicked(object sender, RoutedEventArgs e) => await SendAsync("start", SceneChoice.SelectedValue as string, LabelChoice.SelectedValue as string);
    private async void CancelClicked(object sender, RoutedEventArgs e) => await SendAsync("cancel");
    private async void RemoveClicked(object sender, RoutedEventArgs e) => await SendAsync("remove", sample: (SampleList.SelectedItem as SampleRow)?.Id);
    private async void RelabelClicked(object sender, RoutedEventArgs e) => await SendAsync("label", label: LabelChoice.SelectedValue as string, sample: (SampleList.SelectedItem as SampleRow)?.Id);
    private void SampleSelected(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void SuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (_state?.Suggestion is not { Ready: true } s) return;
        MediumThreshold.Text = s.MediumDbfs!.Value.ToString("F1", CultureInfo.CurrentCulture);
        HighThreshold.Text = s.HighDbfs!.Value.ToString("F1", CultureInfo.CurrentCulture);
    }
    private async void ConfirmClicked(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(MediumThreshold.Text, out double medium) || !double.TryParse(HighThreshold.Text, out double high) ||
            !NoiseCalibrationContract.Thresholds(medium, high))
        { OperationMessage.Text = "请输入 -120 至 0 的有限值；第一阈值须高于第二阈值至少 1 dB。"; return; }
        await SendAsync("confirm", medium: medium, high: high);
    }
    private async void ClearClicked(object sender, RoutedEventArgs e)
    {
        var expected = _state;
        if (WorkspacePanel.Embedded(this) &&
            MessageBox.Show(WorkspacePanel.DialogOwner(this), "恢复后，本机将使用学校配置的提醒阈值。当前采样草稿保留。",
                "恢复学校阈值", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await SendAsync("clear", confirmedState: expected);
    }

    private void RequestCredential()
    {
        if (!WorkspacePanel.Visible(this)) return;
        CredentialsExpander.IsExpanded = true;
        CalibrationRoot.UpdateLayout();
        ManagementSecret.Focus();
    }
}
