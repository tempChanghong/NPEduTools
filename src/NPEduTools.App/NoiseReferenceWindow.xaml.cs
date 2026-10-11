using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class NoiseReferenceWindow : Window
{
    private sealed record Choice(string Id, string Name);
    private sealed record SampleRow(Guid Id, string Description);
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NoiseReferenceState? _state;
    private bool _busy, _reading, _connected;
    public NoiseReferenceWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();
        TimeMode.ItemsSource = new[] { new Choice("Slow", "SLOW · 1 秒"), new Choice("Fast", "FAST · 125 毫秒") };
        TimeMode.SelectedIndex = 0;
        RoleChoice.ItemsSource = new[] { new Choice("Primary", "第一电平"), new Choice("Check", "复核电平") };
        RoleChoice.SelectedIndex = 0;
        _timer.Tick += async (_, _) => await ReadAsync();
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); };
        Closed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); ManagementSecret.Clear(); };
        UpdateButtons();
    }
    private async Task ReadAsync()
    {
        if (_reading || _busy || _lifetime.IsCancellationRequested) return;
        _reading = true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            var reply = await HostClient.RequestAsync(_pipe, "noise.reference.status", deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            _connected = reply.Outcome == "Succeeded" && reply.NoiseReference is not null;
            if (_connected) Render(reply.NoiseReference!); else OperationMessage.Text = reply.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { _connected = false; if (!_lifetime.IsCancellationRequested) OperationMessage.Text = "后台连接不可用；请刷新，旧状态不能用于保存。"; }
        finally { _reading = false; UpdateButtons(); }
    }
    private static string Number(double? value) => value?.ToString("F2", CultureInfo.CurrentCulture) ?? "—";
    private static string Role(string role) => role == "Primary" ? "第一电平" : "复核电平";
    private static string Quality(string value) => value switch
    { "Good" => "采样有效", "Interrupted" => "已中断，须重采", "Clipping" => "削波，须重采", "Incomplete" => "采样不足，须重采", _ => "信号异常，须重采" };
    internal void Render(NoiseReferenceState state)
    {
        bool changed = _state?.Revision != state.Revision || _state.InstanceId != state.InstanceId;
        bool newDraft = _state?.Draft?.Id != state.Draft?.Id;
        var previousSelected = (SampleList.SelectedItem as SampleRow)?.Id;
        _state = state;
        DeviceStatus.Text = state.DeviceId is null ? "请先保存监测麦克风" : state.DeviceName is { } name ? "监测麦克风：" + name : "监测麦克风已选择";
        ProfileStatus.Text = state.Profile is not { } p ? "尚无已确认的外部参考配置" :
            $"已保存估算参考 · {p.Instrument} · A/{p.TimeWeighting} · 偏移 {p.OffsetDb:+0.00;-0.00;0.00} dB\n" +
            (state.Error is not null || state.InvalidationReason is not null ? "参考不可用：" + (state.Error ?? state.InvalidationReason) + "；请新建草稿重新校准。" : "正式监测会核对所选设备、输入设置与格式；匹配后另行显示估算 dB(A)，原始 dBFS 保留。");
        DraftStatus.Text = state.Draft is { } d ? $"当前草稿：{d.Instrument} · A/{d.TimeWeighting} · 波动 ≤ {d.StabilityDb:F1} dB · 复核差值 ≤ {d.CheckToleranceDb:F1} dB" : "请先明确检查门槛并新建草稿。";
        if (newDraft && state.Draft is { } draft)
        {
            Instrument.Text = draft.Instrument; TimeMode.SelectedValue = draft.TimeWeighting;
            Stability.Text = draft.StabilityDb.ToString(CultureInfo.CurrentCulture);
            CheckTolerance.Text = draft.CheckToleranceDb.ToString(CultureInfo.CurrentCulture);
        }
        Progress.Value = state.MeasuredSeconds;
        SamplingStatus.Text = state.State == "Sampling" ? $"预热／采样中 · 已采有效统计 {state.MeasuredSeconds:F1} / 10 秒 · 总过程 {state.ProgressSeconds:F1} / 最多 20 秒。关闭此窗口不会无限占用设备，到时自动释放。" :
            state.State == "Busy" ? "当前采集占用或运行保护生效（准备／自习／考试、暂停或状态未确认），暂不可操作参考校准。" :
            state.State == "Unsupported" ? "当前后台尚不支持 A 加权参考采样，请更新后台。" : state.CaptureMessage ?? "请选择电平开始采样；重采同一电平会替换其旧统计和参考读数。";
        if (changed)
        {
            var samples = state.Draft?.Samples ?? [];
            SampleList.ItemsSource = samples.Select(s => new SampleRow(s.Id,
                $"{Role(s.Role)} · {Quality(s.Quality)} · {s.WindowStartedAt.ToLocalTime():HH:mm:ss}–{s.WindowEndedAt.ToLocalTime():HH:mm:ss}\n" +
                $"A 加权数字统计 {Number(s.DigitalDbfs)} dBFS · 波动 {Number(s.MaximumDbfs - s.MinimumDbfs)} dB · 覆盖 {s.Coverage:P0} · 削波 {s.ClippedPercent:F2}% · 参考 {Number(s.ReferenceDbA)} dB(A)")).ToArray();
            SampleList.SelectedItem = ((IEnumerable<SampleRow>)SampleList.ItemsSource).FirstOrDefault(s => s.Id == previousSelected);
            if (SampleList.SelectedItem is null && samples.Count > 0) SampleList.SelectedIndex = samples.Count - 1;
            if (samples.Count == 1) RoleChoice.SelectedValue = samples[0].Role == "Primary" ? "Check" : "Primary";
        }
        var evaluation = state.Evaluation;
        EvaluationStatus.Text = evaluation.Message + (evaluation.OffsetDb is { } offset ?
            $"\n候选偏移 {offset:+0.00;-0.00;0.00} dB · 第二电平复核差值 {Number(evaluation.CheckErrorDb)} dB" : "");
        if (state.Error is not null) OperationMessage.Text = "外部参考文件不可用，原文件已保留；未应用任何新值。";
        UpdateButtons();
    }
    private void UpdateButtons()
    {
        if (BeginDraft is null) return;
        bool idle = !_busy && !_reading && _connected && _state is { State: "Ready", Error: null };
        bool draft = idle && _state!.Draft is not null;
        BeginDraft.IsEnabled = idle && _state!.DeviceId is not null;
        StartSample.IsEnabled = draft;
        CancelSample.IsEnabled = !_busy && _connected && _state is { State: "Sampling" };
        AttachReference.IsEnabled = RemoveSample.IsEnabled = draft && SampleList.SelectedItem is SampleRow;
        ConfirmProfile.IsEnabled = draft && _state!.Evaluation.Ready;
        ClearProfile.IsEnabled = idle && _state!.Profile is not null;
    }
    private async Task SendAsync(string action, string? role = null, Guid? sample = null,
        double? reference = null, double? minimum = null, double? maximum = null)
    {
        if (_busy || _state is null) return;
        double? stability = null, tolerance = null;
        if (action == "begin")
        {
            if (!double.TryParse(Stability.Text, out double s) || !double.TryParse(CheckTolerance.Text, out double t) ||
                !NoiseReferenceContract.Limit(s) || !NoiseReferenceContract.Limit(t))
            { OperationMessage.Text = "请明确填写 0.1–5 dB 的允许波动与复核差值；它们不是精度承诺。"; return; }
            stability = s; tolerance = t;
        }
        var command = new NoiseReferenceCommand(action, _state.InstanceId, _state.Revision, ManagementSecret.Password,
            action is "begin" or "clear" ? null : _state.Draft?.Id,
            action == "begin" ? Instrument.Text.Trim() : null, action == "begin" ? TimeMode.SelectedValue as string : null,
            stability, tolerance, role, sample, reference, minimum, maximum, action == "reference" && SameWindow.IsChecked == true);
        if (!NoiseReferenceContract.Valid(command)) { OperationMessage.Text = "请检查口令、草稿和输入值，并确认参考读数的时段与仪表设置。"; return; }
        _busy = true; UpdateButtons();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(3500);
            var reply = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.reference.command", NoiseReference: command), deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            _connected = reply.NoiseReference is not null;
            if (reply.NoiseReference is { } state) Render(state);
            OperationMessage.Text = reply.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { _connected = false; if (!_lifetime.IsCancellationRequested) OperationMessage.Text = "操作结果未确认，请刷新核对；不会自动重复采样或保存。"; }
        finally { _busy = false; UpdateButtons(); }
    }
    private void SampleSelected(object sender, SelectionChangedEventArgs e)
    {
        var sample = _state?.Draft?.Samples.FirstOrDefault(s => s.Id == (SampleList.SelectedItem as SampleRow)?.Id);
        ReferenceValue.Text = sample?.ReferenceDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        ReferenceMinimum.Text = sample?.ReferenceMinimumDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        ReferenceMaximum.Text = sample?.ReferenceMaximumDbA?.ToString(CultureInfo.CurrentCulture) ?? "";
        SameWindow.IsChecked = sample?.SameWindowConfirmed == true; UpdateButtons();
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private async void ManagementClicked(object sender, RoutedEventArgs e) { await NoiseManagementDialog.ConfigureAsync(this, _pipe); await ReadAsync(); }
    private async void BeginClicked(object sender, RoutedEventArgs e)
    {
        if (_state?.Draft?.Samples.Count > 0 && MessageBox.Show(this, "新草稿会替换已有参考统计，已确认参考保留。继续吗？",
            "新建外部参考草稿", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await SendAsync("begin");
    }
    private async void StartClicked(object sender, RoutedEventArgs e) => await SendAsync("start", role: RoleChoice.SelectedValue as string);
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
