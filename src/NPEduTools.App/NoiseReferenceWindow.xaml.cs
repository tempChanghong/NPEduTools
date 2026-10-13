using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class NoiseReferenceWindow : Window
{
    private sealed record Choice(string Id, string Name);
    private sealed record SampleRow(Guid Id, string Description);
    private NoiseProtectionState? _access;
    private NoiseProtectionState? _verifiedAccess;
    internal void UseVerifiedAccess(NoiseProtectionState state)
    {
        _verifiedAccess = state;
        ReferenceCredentials.Visibility = Visibility.Collapsed;
        VerifiedCredentialHint.Visibility = Visibility.Visible;
    }
    private void RestoreCredentials(bool clear)
    {
        _verifiedAccess = null;
        ReferenceCredentials.Visibility = Visibility.Visible;
        VerifiedCredentialHint.Visibility = Visibility.Collapsed;
        if (clear) ManagementSecret.Clear();
    }
    private async Task ReadAccessAsync(CancellationToken token, long stamp)
    {
        var reply = await HostClient.RequestAsync(_pipe, "noise.management.status", token);
        if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
        ShowAccess(reply.NoiseProtection);
    }
    private void ShowAccess(NoiseProtectionState? state)
    {
        if (_verifiedAccess is { } verified && (state?.ManagementSource != verified.ManagementSource ||
            state?.CredentialRevision != verified.CredentialRevision || state?.InstanceId != verified.InstanceId))
            RestoreCredentials(true);
        _access = state;
        ManagementLabel.Text = CalibrationAccess.Label(_access); ManagementHint.Text = CalibrationAccess.Hint(_access);
        UpdateButtons();
    }
    private readonly CalibrationUiOrder _order = new();
    private long _activeRead;
    private readonly string _pipe;
    private readonly string? _meterPath;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NoiseReferenceState? _state;
    private Guid? _ownedSampleDraft;
    private long? _ownedSamplingRevision;
    private bool _busy, _reading, _connected, _closing, _allowClose, _verifying;
    private TaskCompletionSource? _pendingUsbStart;
    private Guid? _autoFieldDraft;
    private long? _autoPrimaryRevision;
    private bool _autoCheckQueued, _autoCheckStarted, _renderingPreferences;
    private int _step = 1;
    private int _displayedStep;
    private string SelectedWorkflow => _meterPath is not null && WorkflowChoice.SelectedValue as string == "Field" ? "Field" : "Stable";
    public NoiseReferenceWindow(string pipe, string? meterPath = null)
    {
        _pipe = pipe; _meterPath = meterPath; InitializeComponent(); DesktopDialogSizing.Attach(this, pipe);
        if (meterPath is not null)
        {
            Title = "USB 同期校准 (Beta) · NPEduTools"; Heading.Text = "USB 同期校准 (Beta)";
            StepNavigation.Visibility = Visibility.Visible;
            BeginDraft.Content = "下一步：准备采样";
            SampleDetails.IsExpanded = false;
            WorkflowNote.Text = "连续读取所选仪表，与麦克风在同一 Host 时钟下同期统计。现场模式自动采两段各 30 秒，计算偏移并复核，无需刻意制造安静或第二音量。仪表内部采样时间没有确认的时间戳；保留 Beta，不宣称精确硬件同步或 LAeq。设备、输入设置变化后参考失效。";
        }
        else AdvancedSettings.IsExpanded = true;
        WorkflowChoice.ItemsSource = new[] {new Choice("Field","现场校准 · 两段自动（Beta）"),new Choice("Stable","严格校准 · 稳定声场")};
        WorkflowOptions.Visibility = meterPath is not null ? Visibility.Visible : Visibility.Collapsed;
        WorkflowChoice.SelectedValue = meterPath is not null ? "Field" : "Stable";
        TimeMode.ItemsSource = new[] { new Choice("Slow", "SLOW · 慢响应"), new Choice("Fast", "FAST · 快响应") };
        ReferenceInstructions.Text = ManualReferenceInstructions;
        TimeMode.SelectedIndex = 0;
        RoleChoice.ItemsSource = new[] { new Choice("Primary", "第一电平"), new Choice("Check", "复核电平") };
        RoleChoice.SelectedIndex = 0;
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); };
        Closed += (_, _) => { _timer.Stop(); _order.Advance(); _lifetime.Cancel(); ManagementSecret.Clear(); };
        Closing += async (_, e) =>
        {
            _autoFieldDraft = null;
            bool OwnsSample() => _state is { State: "Sampling", Draft.MeterDevicePath: not null } &&
                _ownedSampleDraft == _state.Draft.Id && _ownedSamplingRevision == _state.Revision;
            if (_allowClose || !_closing && _pendingUsbStart is null && !OwnsSample()) return;
            e.Cancel = true; if (_closing) return; _closing = true;
            _timer.Stop(); UpdateButtons();
            // A separate deadline permits cancellation even if a status poll is stalled.
            try
            {
                // Keep the start transport alive until its bounded response settles. Only its
                // Accepted receipt can establish ownership; never infer ownership from a poll.
                if (_pendingUsbStart is { } pending) await pending.Task.WaitAsync(TimeSpan.FromSeconds(4));
                _autoFieldDraft = null;
                if (!OwnsSample() || _state is not { Draft: { } currentDraft } current) return;
                var command = new NoiseReferenceCommand("cancel", current.InstanceId, current.Revision,
                    ManagementSecret.Password, currentDraft.Id);
                using var deadline = new CancellationTokenSource(3500);
                await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: command), deadline.Token);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException) { }
            finally { _allowClose = true; Close(); } // Host's per-segment 20/40 s limit is independent of this window.
        };
        UpdateButtons();
    }
    private Task ReadAsync() => ReadCoreAsync(false);
    private async Task ReadCoreAsync(bool force)
    {
        if ((!force && (_reading || _busy)) || _closing || _lifetime.IsCancellationRequested) return;
        long stamp = _order.Advance(); _activeRead = stamp; _reading = true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            await ReadAccessAsync(deadline.Token, stamp);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            var reply = await HostClient.RequestAsync(_pipe, "noise.reference.status", deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            _connected = reply.Outcome == "Succeeded" && reply.NoiseReference is not null;
            if (_connected) Render(reply.NoiseReference!);
            else { _autoFieldDraft = null; OperationMessage.Text = CalibrationAccess.Message(reply.ErrorCode, reply.Message, _access); }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (_order.Current(stamp) && !_lifetime.IsCancellationRequested) { _autoFieldDraft=null; _connected = false; OperationMessage.Text = "后台连接不可用；已停止自动续采，请刷新，旧状态不能用于保存。"; } }
        finally { if (_activeRead == stamp) _reading = false; UpdateButtons(); }
    }
    private static string Number(double? value) => value?.ToString("F2", CultureInfo.CurrentCulture) ?? "—";
    private static string Role(string role) => role == "Primary" ? "第一电平" : "复核电平";
    private const string ManualReferenceInstructions = "看 DLY-2202 屏幕中央的 SPL 大数字，右侧须为 A，并与草稿 FAST／SLOW 一致、关闭 MAX 保持。这里没有单独的‘稳定读数’模式：填入本段同期采样期间持续小幅波动的代表读数，范围 30–130 dB(A)；明显起伏时请重采，不要只抄结束瞬间或把最低／最高取中点。最低／最高可成对填写，仅检查波动，本流程不称为 LAeq。";
    private static string Quality(string value) => value switch
    { "Good" => "采样有效", "Interrupted" => "已中断，须重采", "Clipping" => "削波，须重采", "Incomplete" => "采样不足，须重采", _ => "信号异常，须重采" };
    internal void Render(NoiseReferenceState state)
    {
        bool finishedSample = _state?.State == "Sampling" && state.State != "Sampling";
        bool changed = _state?.Revision != state.Revision || _state.InstanceId != state.InstanceId;
        bool newDraft = _state?.Draft?.Id != state.Draft?.Id;
        if (changed && !_busy && !_verifying) OperationMessage.Text = "";
        var previousSelected = (SampleList.SelectedItem as SampleRow)?.Id;
        _state = state;
        bool usb = state.Draft?.MeterDevicePath is not null || _meterPath is not null;
        bool field = state.Draft?.Workflow == "Field";
        if (changed) AcceptReferenceWarning.IsChecked=false;
        AcceptReferenceWarning.Visibility = state.Evaluation.Assessment?.WarningRequired == true ? Visibility.Visible : Visibility.Collapsed;
        SamplingHeading.Text = field ? "2　自动采集两段现场统计" : usb ? "2　采样与自动取得同期读数" : "2　采样与填入同期读数";
        ManualReferenceInputs.Visibility = SameWindow.Visibility = AttachReference.Visibility = usb ? Visibility.Collapsed : Visibility.Visible;
        ReferenceInstructions.Text = usb ? "同期读数由 USB 自动取得，不能手动覆盖；预热、跨窗口、迟到和不完整数据不用于参考。模式、MAX、低电量、量程变化或断线会使本段作废。" :
            ManualReferenceInstructions;
        DeviceStatus.Text = state.DeviceId is null ? "请先保存监测麦克风" : state.DeviceName is { } name ? "监测麦克风：" + name : "监测麦克风已选择";
        ReferenceHealth.Text = state.Error is not null || state.InvalidationReason is not null ? "参考不可用 · 请重新校准；原配置保留" :
            state.Profile is null ? "尚未保存估算参考" : state.FreshSignatureMatched ? "已保存估算参考 · 本次输入已核对" : "已保存估算参考 · 正式采样时核对输入设置";
        ReferenceHealth.ToolTip = state.Error ?? state.InvalidationReason;
        ProfileStatus.Text = state.Profile is not { } p ? "尚无已确认的外部参考配置" :
            $"已保存估算参考 · {(p.Method == "UsbReferenceBeta" ? "USB (Beta) · " : "")}{p.Instrument} · A/{p.TimeWeighting} · 偏移 {p.OffsetDb:+0.00;-0.00;0.00} dB\n" +
            (NoiseReferenceCalibration.Describe(p) is {} note?note+"\n":"") +
            (state.Error is not null || state.InvalidationReason is not null ? "参考不可用：" + (state.Error ?? state.InvalidationReason) + "；请新建草稿重新校准。" : "正式监测会核对所选设备、输入设置与格式；匹配后另行显示估算 dB(A)，原始 dBFS 保留。");
        DraftStatus.Text = state.Draft is { } d ? $"当前草稿：{d.Instrument} · A/{d.TimeWeighting} · 波动 ≤ {d.StabilityDb:F1} dB · " +
            (field?$"自动通过 ≤ {d.CheckToleranceDb:F1} dB，复核硬上限 5 dB":$"复核差值 ≤ {d.CheckToleranceDb:F1} dB") : "请先明确检查门槛并新建草稿。";
        if (newDraft && state.Draft is { } draft)
        {
            _renderingPreferences=true;
            WorkflowChoice.SelectedValue = draft.Workflow;
            Instrument.Text = draft.Instrument; TimeMode.SelectedValue = draft.TimeWeighting;
            Stability.Text = draft.StabilityDb.ToString(CultureInfo.CurrentCulture);
            CheckTolerance.Text = draft.CheckToleranceDb.ToString(CultureInfo.CurrentCulture);
            _renderingPreferences=false;
        }
        UpdateWorkflowInstructions(_meterPath is not null && _step == 1 && state.State != "Sampling" ? SelectedWorkflow : state.Draft?.Workflow??SelectedWorkflow);
        Progress.Maximum = NoiseReferenceCalibration.Duration(state.Draft?.Workflow??SelectedWorkflow);
        Progress.Value = state.MeasuredSeconds;
        MeterStatus.Text = state.Meter?.Reading is { } reading ?
            $"USB {reading.Decibels:F1} dB(A) · {reading.TimeWeighting} · 往返 {reading.RoundTripMilliseconds:F0} ms · 读数年龄 {state.Meter.AgeSeconds:F2} 秒\n{state.Meter.Message}" : state.Meter?.Message ??
            (usb ? "USB 仅在明确开始本段采样时连接，到时／取消后释放；不会自动重连。" : "");
        SamplingStatus.Text = state.State == "Sampling" ? (field ? state.Draft!.Samples.Any(s => s.Role == "Primary") ? "第二段复核 · " : "第一段参考 · " : "") + $"预热／采样中 · 已采有效统计 {state.MeasuredSeconds:F1} / {Progress.Maximum:g} 秒 · 总过程 {state.ProgressSeconds:F1} / 最多 {NoiseReferenceCalibration.CaptureLimit(state.Draft?.Workflow??"Stable"):g} 秒。关闭此窗口不会无限占用设备，到时自动释放。" :
            state.State == "Busy" ? "当前采集占用或运行保护生效（准备／自习／考试、暂停或状态未确认），暂不可操作参考校准。" :
            state.State == "Unsupported" ? "当前后台尚不支持 A 加权参考采样，请更新后台。" : state.CaptureMessage ?? "请选择电平开始采样；重采同一电平会替换其旧统计和参考读数。";
        if (changed)
        {
            var samples = state.Draft?.Samples ?? [];
            SampleList.ItemsSource = samples.Select(s => new SampleRow(s.Id,
                $"{(field?s.Role=="Primary"?"第一段":"复核段":Role(s.Role))} · {Quality(s.Quality)} · {s.WindowStartedAt.ToLocalTime():HH:mm:ss}–{s.WindowEndedAt.ToLocalTime():HH:mm:ss}\n" +
                $"A 加权数字统计 {Number(s.DigitalDbfs)} dBFS · 波动 {Number(s.MaximumDbfs - s.MinimumDbfs)} dB · 覆盖 {s.Coverage:P0} · 削波 {s.ClippedPercent:F2}% · 参考 {Number(s.ReferenceDbA)} dB(A)" +
                (s.MeterEvidence is { } evidence ? $"\nUSB (Beta) · {evidence.Readings} 次 · 连续跨度 {evidence.SpanSeconds:F2} 秒 · 最大间隔 {evidence.MaximumGapSeconds:F2} 秒" : ""))).ToArray();
            SampleList.SelectedItem = ((IEnumerable<SampleRow>)SampleList.ItemsSource).FirstOrDefault(s => s.Id == previousSelected);
            if (SampleList.SelectedItem is null && samples.Count > 0) SampleList.SelectedIndex = samples.Count - 1;
            if (samples.Count == 1) RoleChoice.SelectedValue = samples[0].Role == "Primary" ? "Check" : "Primary";
        }
        var evaluation = state.Evaluation;
        EvaluationStatus.Text = evaluation.Message + (evaluation.OffsetDb is { } offset ?
            $"\n候选偏移 {offset:+0.00;-0.00;0.00} dB · {(field?"第二段偏移复核差值":"第二电平复核差值")} {Number(evaluation.CheckErrorDb)} dB" : "");
        if (state.Error is not null) OperationMessage.Text = "外部参考文件不可用，原文件已保留；未应用任何新值。";
        if (_meterPath is not null)
        {
            if (state.Draft?.MeterDevicePath != _meterPath) _step = 1;
            else if (state.State == "Sampling") _step = 2;
            else if (newDraft) _step = state.Evaluation.Ready ? 3 : 2;
            else if (finishedSample && (state.Evaluation.Ready || state.Draft.Samples.Any(s => s.Role == "Check" || s.Quality != "Good"))) _step = 3;
        }
        UpdateButtons();
        ContinueFieldSequence(state);
    }
    private void UpdateWorkflowInstructions(string workflow)
    {
        bool field=workflow=="Field";
        ToleranceLabel.Text=field?"自动通过差值（dB）":"复核允许差值（dB）";
        LimitInstructions.Text=field ? "现场模式：允许声场波动 0.1–20 dB，默认 20；自动通过差值 0.1–3 dB，默认 3。超过自动门槛但不超过 5 dB 需明确确认，超过 5 dB 不能保存。这些是工程门槛，不是精度保证。修改后须新建草稿。" :
            "严格模式：允许波动与复核差值均为 0.1–5 dB，须明确填写。两段参考至少相差 6 dB；这些不是设备精度。修改后须新建草稿。";
        SamplingInstructions.Text=field ? "一次点击，先预热约 5 秒并采第一段 30 秒；第一段有效后自动再预热并采第二段 30 秒，约 70 秒完成两段。保持仪表、麦克风位置与设置不变，使用自然环境，不必让学生配合或人为制造 6 dB 差距。失败、取消、连接中断或关闭窗口会停止自动续采；不会自动保存。" :
            "先采第一电平，再换一个稳定电平作复核。两段参考至少相差 6 dB；此间隔用于检查输入链路，不是精度承诺。每次先预热 5 秒，再收集约 10 秒统计，总过程最多 20 秒。";
        RoleChoice.Visibility=field?Visibility.Collapsed:Visibility.Visible;
        StartSample.Content=field?"开始现场校准（两段自动）":"开始本段采样";
    }
    private void WorkflowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (Stability is null || _renderingPreferences) return;
        _autoFieldDraft=null;
        Stability.Text=SelectedWorkflow=="Field"?"20":"";
        CheckTolerance.Text=SelectedWorkflow=="Field"?"3":"";
        if (SelectedWorkflow == "Stable") AdvancedSettings.IsExpanded = true;
        UpdateWorkflowInstructions(SelectedWorkflow);UpdateButtons();
    }
    private void WarningSelected(object sender,RoutedEventArgs e)=>UpdateButtons();
    private void ContinueFieldSequence(NoiseReferenceState state)
    {
        if (_autoFieldDraft is not {} owned) return;
        if (state.Draft is not {Workflow:"Field"} draft || draft.Id!=owned || state.Error is not null || state.State is "Busy" or "Unsupported")
        { _autoFieldDraft=null; return; }
        if (!_autoCheckStarted && (state.State=="Sampling" ? state.Revision!=_autoPrimaryRevision :
            state.State=="Ready" && state.Revision!=_autoPrimaryRevision+1)) {_autoFieldDraft=null;return;}
        if (state.State!="Ready") return;
        if (draft.Samples.Any(s=>s.Quality!="Good") || draft.Samples.Any(s=>s.Role=="Check")) {_autoFieldDraft=null;return;}
        if (draft.Samples.SingleOrDefault(s=>s.Role=="Primary") is not {} first || _autoCheckQueued || _busy) return;
        if (!NoiseReferenceContract.MeterEvidenceValid(first.MeterEvidence,"Field") || first.SameWindowConfirmed!=true) {_autoFieldDraft=null;return;}
        if (first.MaximumDbfs-first.MinimumDbfs>draft.StabilityDb || first.ReferenceMaximumDbA-first.ReferenceMinimumDbA>draft.StabilityDb)
        { _autoFieldDraft=null;OperationMessage.Text="第一段声场波动超过本草稿允许值，自动续采已停止；可调整门槛并新建草稿，原确认参考保留。";return; }
        _autoCheckQueued=true;
        Dispatcher.BeginInvoke(new Action(async()=> {
            if (_lifetime.IsCancellationRequested || !WorkspacePanel.Visible(this) || _autoFieldDraft!=owned || _autoPrimaryRevision is not {} revision ||
                _state is not {} current || !OwnsFieldContinuation(current,owned,revision,first.Id))
            { _autoFieldDraft=null;return; }
            await SendAsync("start",role:"Check");
        }));
    }
    internal static bool OwnsFieldContinuation(NoiseReferenceState state,Guid draft,long primaryRevision,Guid sample) =>
        primaryRevision>=0 && primaryRevision<long.MaxValue && state.State=="Ready" && state.Revision==primaryRevision+1 && state.Error is null &&
        state.Draft is {Workflow:"Field"} d && d.Id==draft && d.Samples.Count==1 && d.Samples[0].Id==sample && d.Samples[0].Role=="Primary";
    private void UpdateButtons()
    {
        if (BeginDraft is null) return;
        bool idle = !_busy && !_closing && _connected && _state is { State: "Ready", Error: null };
        RefreshStatus.IsEnabled = !_busy && !_closing && !_verifying && !_lifetime.IsCancellationRequested;
        ConfigureManagement.IsEnabled = !_busy && !_closing && !_verifying && _access?.ManagementSource != "School";
        bool draft = idle && _state!.Draft is not null;
        bool matchingDraft = draft && _state!.Draft!.MeterDevicePath == _meterPath && _state.Draft.Workflow==SelectedWorkflow;
        WorkflowChoice.IsEnabled=idle;
        BeginDraft.IsEnabled = idle && _state!.DeviceId is not null;
        StartSample.IsEnabled = matchingDraft;
        CancelSample.IsEnabled = !_busy && !_closing && _connected && _state is { State: "Sampling" };
        RemoveSample.IsEnabled = matchingDraft && SampleList.SelectedItem is SampleRow;
        AttachReference.IsEnabled = RemoveSample.IsEnabled && _state!.Draft!.MeterDevicePath is null;
        ConfirmProfile.IsEnabled = matchingDraft && _state!.Evaluation.Ready && (_state.Evaluation.Assessment?.WarningRequired!=true || AcceptReferenceWarning.IsChecked==true);
        ClearProfile.IsEnabled = idle && _state!.Profile is not null;
        if (!_connected) ReferenceHealth.Text = "后台连接待确认 · 旧状态不能用于保存";
        UpdateSteps();
    }
    private void UpdateSteps()
    {
        if (_meterPath is null) return; // Keep manual reference entry and all its controls available.
        PreparePanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        SamplePanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        if (_displayedStep != _step) { _displayedStep = _step; ReferencePageScroll.ScrollToTop(); }
        bool navigating = _connected && !_busy && !_closing && !_verifying && _state?.State != "Sampling" && _autoFieldDraft is null;
        SetupStep.IsEnabled = !_busy && !_closing && !_verifying && (!_connected || navigating);
        SamplingStep.IsEnabled = ReviewSamples.IsEnabled = navigating && _state?.Draft?.MeterDevicePath == _meterPath;
        ResultStep.IsEnabled = navigating && _state?.Draft?.MeterDevicePath == _meterPath && _state?.Draft?.Samples.Count is > 0;
        SetupStep.Style = TryFindResource(_step == 1 ? (object)"Primary" : typeof(Button)) as Style;
        SamplingStep.Style = TryFindResource(_step == 2 ? (object)"Primary" : typeof(Button)) as Style;
        ResultStep.Style = TryFindResource(_step == 3 ? (object)"Primary" : typeof(Button)) as Style;
    }
    private void Navigate(int step)
    {
        if (_busy || _closing || _verifying || _connected && (_state?.State == "Sampling" || _autoFieldDraft is not null)) return;
        if (!_connected && step != 1) return;
        if (step != 1 && _state?.Draft?.MeterDevicePath != _meterPath) return;
        _step = step; UpdateSteps(); ReferencePageScroll.ScrollToTop();
    }
    private void SetupClicked(object sender, RoutedEventArgs e) => Navigate(1);
    private void SamplingClicked(object sender, RoutedEventArgs e) => Navigate(2);
    private void ResultClicked(object sender, RoutedEventArgs e) => Navigate(3);
    private async Task SendAsync(string action, string? role = null, Guid? sample = null,
        double? reference = null, double? minimum = null, double? maximum = null)
    {
        if (_busy || _closing || _verifying || _state is null || _lifetime.IsCancellationRequested) return;
        double? stability = null, tolerance = null;
        if (action == "begin")
        {
            if (!double.TryParse(Stability.Text, out double s) || !double.TryParse(CheckTolerance.Text, out double t) ||
                !NoiseReferenceContract.StabilityLimit(s,SelectedWorkflow) || !NoiseReferenceContract.CheckLimit(t,SelectedWorkflow))
            { OperationMessage.Text = SelectedWorkflow=="Field"?"现场模式允许波动为 0.1–20 dB，自动通过差值为 0.1–3 dB；不是精度承诺。":"请明确填写 0.1–5 dB 的允许波动与复核差值；它们不是精度承诺。"; return; }
            stability = s; tolerance = t;
        }
        var command = new NoiseReferenceCommand(action, _state.InstanceId, _state.Revision, ManagementSecret.Password,
            action is "begin" or "clear" ? null : _state.Draft?.Id,
            action == "begin" ? Instrument.Text.Trim() : null, action == "begin" ? TimeMode.SelectedValue as string : null,
            stability, tolerance, role, sample, reference, minimum, maximum, action == "reference" && SameWindow.IsChecked == true,
            action == "begin" ? _meterPath : null, action=="begin"?SelectedWorkflow:null,
            action=="confirm" && AcceptReferenceWarning.IsChecked==true);
        if (!NoiseReferenceContract.Valid(command)) { OperationMessage.Text = "请检查口令、草稿和输入值，并确认参考读数的时段与仪表设置。"; return; }
        long stamp = _order.Advance(); _busy = true; UpdateButtons();
        TaskCompletionSource? pendingStart = action == "start" && _meterPath is not null ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        if (pendingStart is not null) _pendingUsbStart = pendingStart;
        if (action is "begin" or "cancel" or "remove" or "clear") _autoFieldDraft=null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            var reply = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: command), deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            _connected = reply.NoiseReference is not null;
            if (reply.NoiseProtection is not null) ShowAccess(reply.NoiseProtection);
            if (action == "start" && reply.Outcome == "Accepted" && reply.NoiseReference is { State: "Sampling", Draft: { } owned } started)
            { _ownedSampleDraft = owned.Id; _ownedSamplingRevision = started.Revision;
              if (!_closing && owned.Workflow=="Field" && role=="Primary") {_autoFieldDraft=owned.Id;_autoPrimaryRevision=started.Revision;_autoCheckQueued=false;_autoCheckStarted=false;}
              if (owned.Workflow=="Field" && role=="Check") _autoCheckStarted=true;
            }
            else if(action=="start") _autoFieldDraft=null;
            if (action == "cancel" && reply.Outcome == "Succeeded")
            { _ownedSampleDraft = null; _ownedSamplingRevision = null; }
            if (reply.NoiseReference is { } state) Render(state);
            OperationMessage.Text = CalibrationAccess.Message(reply.ErrorCode, reply.Message, _access);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (_order.Current(stamp) && !_lifetime.IsCancellationRequested) { _autoFieldDraft=null; _connected = false; OperationMessage.Text = "操作结果未确认，已停止自动续采；请刷新核对，不会自动重复采样或保存。"; } }
        finally
        {
            _busy = false;
            if (ReferenceEquals(_pendingUsbStart, pendingStart)) _pendingUsbStart = null;
            pendingStart?.TrySetResult();
            UpdateButtons();
        }
    }
    private void SampleSelected(object sender, SelectionChangedEventArgs e)
    {
        var sample = _state?.Draft?.Samples.FirstOrDefault(s => s.Id == (SampleList.SelectedItem as SampleRow)?.Id);
        ReferenceValue.Text = sample?.ReferenceDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        ReferenceMinimum.Text = sample?.ReferenceMinimumDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        ReferenceMaximum.Text = sample?.ReferenceMaximumDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        SameWindow.IsChecked = sample?.SameWindowConfirmed == true; UpdateButtons();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _verifying || _lifetime.IsCancellationRequested) return;
        _busy = _verifying = true; _order.Advance(); UpdateButtons();
        await ReadCoreAsync(true);
        long stamp = _order.Advance();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
        try
        {
            var reply = await CalibrationAccess.VerifyAsync(_pipe, ManagementSecret.Password, deadline.Token);
            if (!_order.Current(stamp) || _lifetime.IsCancellationRequested) return;
            ShowAccess(reply.NoiseProtection ?? _access);
            OperationMessage.Text = reply.Outcome == "Succeeded" ? _connected ? "管理口令验证通过；校准状态已刷新。" : "管理口令验证通过；采样状态仍待确认，请重试刷新。" :
                CalibrationAccess.Message(reply.ErrorCode, reply.Message, _access);
            if (reply.Outcome != "Succeeded") RestoreCredentials(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or TimeoutException or System.Text.Json.JsonException)
        { if (!_lifetime.IsCancellationRequested) OperationMessage.Text = "管理验证响应未确认，请检查后台连接后重试。"; }
        finally { _busy = _verifying = false; UpdateButtons(); }
    }
    private async void ManagementClicked(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || _verifying || _lifetime.IsCancellationRequested) return;
        _busy = true; _order.Advance(); UpdateButtons();
        try { await NoiseManagementDialog.ConfigureAsync(WorkspacePanel.DialogOwner(this), _pipe); }
        finally { _busy = false; }
        await ReadCoreAsync(true); UpdateButtons();
    }
    private async void BeginClicked(object sender, RoutedEventArgs e)
    {
        if (_state?.Draft?.Samples.Count > 0 && MessageBox.Show(WorkspacePanel.DialogOwner(this), "新草稿会替换已有参考统计，已确认参考保留。继续吗？",
            "新建外部参考草稿", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await SendAsync("begin");
    }
    private async void StartClicked(object sender, RoutedEventArgs e) => await SendAsync("start", role: SelectedWorkflow=="Field"?"Primary":RoleChoice.SelectedValue as string);
    private async void CancelClicked(object sender, RoutedEventArgs e) => await SendAsync("cancel");
    private async void RemoveClicked(object sender, RoutedEventArgs e) => await SendAsync("remove", sample: (SampleList.SelectedItem as SampleRow)?.Id);
    private async void ReferenceClicked(object sender, RoutedEventArgs e)
    {
        bool ParseOptional(string text, out double? value)
        { value = null; if (string.IsNullOrWhiteSpace(text)) return true; if (!double.TryParse(text, out double v)) return false; value = v; return true; }
        if (!double.TryParse(ReferenceValue.Text, out double reference) || !ParseOptional(ReferenceMinimum.Text, out var minimum) ||
            !ParseOptional(ReferenceMaximum.Text, out var maximum) || !NoiseReferenceContract.ReferenceRange(reference, minimum, maximum) || SameWindow.IsChecked != true)
        { OperationMessage.Text = "请填写 30–130 dB(A) 的同期参考值并勾选确认；最低／最高须成对且包含参考值。"; return; }
        await SendAsync("reference", sample: (SampleList.SelectedItem as SampleRow)?.Id, reference: reference, minimum: minimum, maximum: maximum);
    }
    private async void ConfirmClicked(object sender, RoutedEventArgs e) => await SendAsync("confirm");
    private async void ClearClicked(object sender, RoutedEventArgs e) => await SendAsync("clear");
}
