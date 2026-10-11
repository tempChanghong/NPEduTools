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
    private readonly string _pipe;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NoiseCalibrationState? _state;
    private bool _busy, _reading, _connected;
    public NoiseCalibrationWindow(string pipe)
    {
        _pipe = pipe; InitializeComponent();
        SceneChoice.ItemsSource = Scenes; SceneChoice.SelectedIndex = 0;
        LabelChoice.ItemsSource = Labels; LabelChoice.SelectedIndex = 0;
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
            var r = await HostClient.RequestAsync(_pipe, "noise.calibration.status", deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            _connected = r.Outcome == "Succeeded" && r.NoiseCalibration is not null;
            if (_connected) Render(r.NoiseCalibration!);
            else OperationMessage.Text = r.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { _connected = false; if (!_lifetime.IsCancellationRequested) OperationMessage.Text = "后台连接不可用，请刷新；旧状态不能用于保存。"; }
        finally { _reading = false; UpdateButtons(); }
    }
    internal void Render(NoiseCalibrationState state)
    {
        bool samplesChanged = _state?.Revision != state.Revision || _state.InstanceId != state.InstanceId;
        _state = state;
        DeviceStatus.Text = state.DeviceId is null ? "请先在自习设备中保存麦克风" :
            state.DeviceName is { } name ? "监测麦克风：" + name : "当前监测麦克风已选择";
        ProfileStatus.Text = state.Profile is null ? "尚未确认本机阈值" : state.Applied ?
            $"本机阈值适用 · 视觉 {state.Profile.MediumDbfs:F1} / 纪律 {state.Profile.HighDbfs:F1} dBFS" :
            "已保存本机阈值；当前设备、学校配置或采集设置尚未匹配，暂不应用";
        SamplingProgress.Value = state.ProgressSeconds;
        SamplingStatus.Text = state.State == "Sampling" ? $"采样中 · {state.ProgressSeconds:F1} / 15 秒；关闭窗口后，本段仍会在 15 秒到时释放麦克风。" :
            state.State == "Busy" ? "当前采集占用或运行保护生效（准备／自习／考试、暂停或状态未确认），暂不可校准。已有样本和阈值保留。" :
            state.DraftId is null ? "请先新建校准草稿" : $"已有 {state.Samples.Count} 段；可以稍后补采，不必采齐全部场景。";
        if (samplesChanged)
        {
            var selected = (SampleList.SelectedItem as SampleRow)?.Id;
            SampleList.ItemsSource = state.Samples.Select(s => new SampleRow(s.Id,
                $"{Scenes.First(x => x.Id == s.Scene).Name} · {Labels.First(x => x.Id == s.Label).Name}\n" +
                $"P10 / 中位 / P90：{Level(s.P10Dbfs)} / {Level(s.MedianDbfs)} / {Level(s.P90Dbfs)} dBFS　覆盖 {s.Coverage:P0}　削波 {s.ClippedPercent:F2}%　{Quality(s.Quality)}")).ToArray();
            if (selected is not null) SampleList.SelectedItem = ((IEnumerable<SampleRow>)SampleList.ItemsSource).FirstOrDefault(s => s.Id == selected);
        }
        SuggestionStatus.Text = state.Suggestion.Message;
        if (state.Error is not null) OperationMessage.Text = "校准文件暂不可用，原文件已保留；请联系管理员。";
        UpdateButtons();
    }
    private static string Level(double? value) => value?.ToString("F1", CultureInfo.CurrentCulture) ?? "—";
    private static string Quality(string value) => value switch { "Good" => "有效", "Clipping" => "削波，需重采", "Interrupted" => "已中断，需重采", "Incomplete" => "采样不足，需重采", _ => "信号异常，需重采" };
    private void UpdateButtons()
    {
        if (BeginDraft is null) return;
        bool idle = !_busy && !_reading && _connected && _state is { State: "Ready", Error: null };
        bool draft = idle && _state!.DraftId is not null;
        BeginDraft.IsEnabled = idle && _state!.DeviceId is not null;
        StartSample.IsEnabled = draft && _state!.Samples.Count < 20;
        CancelSample.IsEnabled = !_busy && _connected && _state is { State: "Sampling" };
        RemoveSample.IsEnabled = RelabelSample.IsEnabled = draft && SampleList.SelectedItem is SampleRow;
        ConfirmThresholds.IsEnabled = draft && _state!.Context is not null;
        ClearThresholds.IsEnabled = draft && _state!.Profile is not null;
        UseSuggestion.IsEnabled = draft && _state!.Suggestion.Ready;
    }
    private async Task SendAsync(string action, string? scene = null, string? label = null, Guid? sample = null, double? medium = null, double? high = null)
    {
        if (_busy || _state is null) return;
        var c = new NoiseCalibrationCommand(action, _state.InstanceId, _state.Revision, ManagementSecret.Password,
            action == "begin" ? null : _state.DraftId, scene, label, sample, medium, high);
        if (!NoiseCalibrationContract.Valid(c)) { OperationMessage.Text = "请输入本机管理口令，并核对两个阈值和场景选择。"; return; }
        _busy = true; UpdateButtons();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(5000);
            var r = await HostClient.RequestAsync(_pipe, new HostRequest(1, Guid.NewGuid(), "noise.calibration.command", NoiseCalibration: c), deadline.Token);
            if (_lifetime.IsCancellationRequested) return;
            if (r.NoiseCalibration is { } state) Render(state);
            OperationMessage.Text = r.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { _connected = false; if (!_lifetime.IsCancellationRequested) OperationMessage.Text = "结果未确认，请刷新后核对；不会自动重复采样或保存。"; }
        finally { _busy = false; UpdateButtons(); }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private async void ManagementClicked(object sender, RoutedEventArgs e) { await NoiseManagementDialog.ConfigureAsync(this, _pipe); await ReadAsync(); }
    private async void BeginClicked(object sender, RoutedEventArgs e)
    {
        if (_state?.Samples.Count > 0 && MessageBox.Show(this, "新草稿会替换已有采样草稿，已确认阈值保留。继续吗？",
            "新建校准草稿", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await SendAsync("begin");
    }
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
    private async void ClearClicked(object sender, RoutedEventArgs e) => await SendAsync("clear");
}
