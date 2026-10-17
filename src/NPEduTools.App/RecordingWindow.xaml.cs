using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public partial class RecordingWindow : Window
{
    private sealed record Preferences(int Version, RecordingOptions Options);
    internal static RecordingOptions? ReadSavedOptions(string endpoint)
    {
        string path = StartupPreferencesStore.PathFor(endpoint).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("录制设置过大。");
        var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path));
        if (settings is not { Version: 1, Options: not null } || RecordingContract.Validate(settings.Options) is not null) throw new InvalidDataException("录制设置无效。");
        return settings.Options;
    }
    private readonly RecordingClient _client;
    private readonly Func<Task<RecordingEnvironment>> _probe;
    private readonly string _preferencesPath;
    private bool _shutdown, _readable = true, _probing, _viewReady, _editing, _probeWhenIdle;
    internal Action? WorkspaceReturn { get; init; }
    private string? _stateError;
    private string? _deviceSelectionError;
    private RecordingEnvironment? _environment;
    private RecordingOptions? _saved;
    internal RecordingWindow(RecordingClient client, string endpoint)
        : this(client, endpoint, RecordingClient.ProbeAsync) { }
    internal RecordingWindow(RecordingClient client, string endpoint, Func<Task<RecordingEnvironment>> probe)
    {
        _client = client;
        _probe = probe;
        _preferencesPath = StartupPreferencesStore.PathFor(endpoint).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        InitializeComponent();
        DesktopDialogSizing.Attach(this, endpoint);
        OutputDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "NPEduTools");
        try
        {
            if (File.Exists(_preferencesPath))
            {
                if (new FileInfo(_preferencesPath).Length > 16384) throw new InvalidDataException();
                var document = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(_preferencesPath));
                if (document is not { Version: 1 } || document.Options is null || RecordingContract.Validate(document.Options) is not null) throw new InvalidDataException();
                _saved = document.Options;
                OutputDirectory.Text = _saved.OutputDirectory;
                SystemSound.IsChecked = _saved.SystemAudio; MicrophoneSound.IsChecked = _saved.Microphone;
                FpsChoice.SelectedIndex = _saved.FramesPerSecond == 8 ? 0 : 1; QualityChoice.SelectedIndex = _saved.MaximumHeight == 1080 ? 0 : 1;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { _readable = false; RecordingError.Text = "保存的录制偏好无法读取，原文件已保留。本次仍可选择设置并录制。"; }
        _client.Changed += Apply;
        Loaded += async (_, _) => { _viewReady = true; _probeWhenIdle = _client.State.Active; await ProbeAsync(); Apply(_client.State); };
        Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _client.Changed -= Apply;
        Apply(_client.State);
    }
    public void Shutdown() { _shutdown = true; Close(); }
    private async Task ProbeAsync()
    {
        if (_probing || _client.State.Active) return;
        _probing = true;
        // Freeze the selection draft before awaiting enumeration so its result
        // cannot overwrite edits made while the device snapshot is in flight.
        RecordingSettings.IsEnabled = RecordStart.IsEnabled = SaveSettings.IsEnabled = false;
        try
        {
            string? display = DisplayChoice.SelectedValue as string ?? _saved?.Display;
            string speaker = SpeakerChoice.SelectedValue as string ?? _saved?.SpeakerId ?? "default";
            string microphone = MicrophoneChoice.SelectedValue as string ?? _saved?.MicrophoneId ?? "default";
            _environment = await _probe();
            var displays = _environment.Displays.ToList();
            if (display is not null && !displays.Any(d => d.Id == display))
                displays.Add(new(display, "上次选择的屏幕 · 当前不可用", 0, 0, 0, 0, false));
            DisplayChoice.ItemsSource = displays;
            DisplayChoice.SelectedValue = display ?? _environment.Displays.FirstOrDefault(d => d.Primary)?.Id;
            RecordingDevice[] AudioChoices(RecordingDevice[] devices, string selected, string defaultLabel, string unavailableLabel)
            {
                var choices = new List<RecordingDevice> { new("default", defaultLabel) };
                choices.AddRange(devices);
                if (!choices.Any(d => d.Id == selected)) choices.Add(new(selected, unavailableLabel));
                return choices.ToArray();
            }
            SpeakerChoice.ItemsSource = AudioChoices(_environment.Speakers, speaker, "系统默认输出设备", "上次选择的系统声音设备 · 当前不可用");
            MicrophoneChoice.ItemsSource = AudioChoices(_environment.Microphones, microphone, "系统默认麦克风", "上次选择的麦克风 · 当前不可用");
            SpeakerChoice.SelectedValue = speaker; MicrophoneChoice.SelectedValue = microphone;
            if (_environment.Error is not null) RecordingError.Text = _environment.Error;
            else if (_readable) RecordingError.Text = "";
            UpdateSummary(); UpdateSound(); UpdateReadySummary();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordingError.Text = error.Message; _environment = null; }
        finally { _probing = false; Apply(_client.State); }
    }
    private int Fps => int.Parse((string)((ComboBoxItem)FpsChoice.SelectedItem).Tag);
    private int MaximumHeight => int.Parse((string)((ComboBoxItem)QualityChoice.SelectedItem).Tag);
    private void UpdateSummary()
    {
        if (_environment is null) return;
        if (DisplayChoice.SelectedItem is not RecordingDisplay display || !_environment.Displays.Any(d => d.Id == display.Id))
        { CaptureSummary.Text = "所选屏幕当前不可用，请重新检测或明确选择其他屏幕。"; return; }
        var (width, height) = RecordingContract.OutputSize(display.Width, display.Height, MaximumHeight);
        CaptureSummary.Text = $"采集 {display.Width} × {display.Height} → 保存 {width} × {height} · {Fps} fps · MP4";
    }
    private void ChoicesChanged(object sender, SelectionChangedEventArgs e) { if (_viewReady) { UpdateSummary(); UpdateReadySummary(); } }
    private void SoundChanged(object sender, RoutedEventArgs e) { if (_viewReady) { UpdateSound(); UpdateReadySummary(); } }
    private void UpdateSound() { SpeakerChoice.IsEnabled = SystemSound.IsChecked == true; MicrophoneChoice.IsEnabled = MicrophoneSound.IsChecked == true; }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ProbeAsync();
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择微课保存位置" };
        if (picker.ShowDialog(WorkspacePanel.DialogOwner(this)) == true) OutputDirectory.Text = picker.FolderName;
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        var options = OptionsFromForm(); if (options is null) return;
        SavePreferences(options);
        await _client.SendAsync("start", options);
    }
    private RecordingOptions? OptionsFromForm()
    {
        if (!_client.StateAvailable || _client.State.Active || _probing || _environment?.Ready != true) return null;
        if (DeviceSelectionError() is { } unavailable) { _deviceSelectionError = RecordingError.Text = unavailable; return null; }
        var options = new RecordingOptions(DisplayChoice.SelectedValue as string ?? "", OutputDirectory.Text.Trim(), Fps, MaximumHeight,
            SystemSound.IsChecked == true, MicrophoneSound.IsChecked == true, SpeakerChoice.SelectedValue as string ?? "default", MicrophoneChoice.SelectedValue as string ?? "default");
        if (RecordingContract.Validate(options) is { } error) { RecordingError.Text = error; return null; }
        return options;
    }
    private string? DeviceSelectionError()
    {
        if (_environment?.Ready != true) return null;
        if (DisplayChoice.SelectedValue is not string display || !_environment.Displays.Any(d => d.Id == display))
            return "所选屏幕当前不可用，请重新检测或明确选择其他屏幕。";
        if (SystemSound.IsChecked == true && (SpeakerChoice.SelectedValue is not string speaker ||
            !_environment.Speakers.Any(d => speaker == "default" || d.Id == speaker)))
            return "所选系统声音设备当前不可用，请重新检测、明确重选设备或取消该音源。";
        if (MicrophoneSound.IsChecked == true && (MicrophoneChoice.SelectedValue is not string microphone ||
            !_environment.Microphones.Any(d => microphone == "default" || d.Id == microphone)))
            return "所选麦克风当前不可用，请重新检测、明确重选设备或取消该音源。";
        return null;
    }
    private bool SavePreferences(RecordingOptions options)
    {
        if (_readable)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
                File.WriteAllText(_preferencesPath + ".tmp", JsonSerializer.Serialize(new Preferences(1, options)));
                File.Move(_preferencesPath + ".tmp", _preferencesPath, true); _saved = options;
                RecordingError.Text = "录制设置已保存，可在自动安排中启用。"; return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecordingError.Text = "录制设置未能保存；本次使用当前选项。"; }
        }
        return false;
    }
    private void SaveSettingsClicked(object sender, RoutedEventArgs e)
    { if (OptionsFromForm() is { } options) { if (SavePreferences(options)) _editing = false; Apply(_client.State); } }
    internal void ShowSettingsPanel() { if (!_client.State.Active) { _editing = true; Apply(_client.State); } }
    private void EditSettingsClicked(object sender, RoutedEventArgs e) { _editing = !_editing; Apply(_client.State); }
    private void UpdateReadySummary()
    {
        var deviceError = DeviceSelectionError();
        if (_deviceSelectionError is not null)
        {
            if (RecordingError.Text == _deviceSelectionError) RecordingError.Text = deviceError ?? "";
            _deviceSelectionError = deviceError;
        }
        ReadySummary.Text = deviceError ?? CaptureSummary.Text + " · " + (MicrophoneSound.IsChecked == true && SystemSound.IsChecked == true ? "麦克风＋系统声音" : MicrophoneSound.IsChecked == true ? "麦克风" : SystemSound.IsChecked == true ? "系统声音" : "无音频");
        ReadyDirectory.Text = "保存到 " + OutputDirectory.Text;
    }
    private async void PauseClicked(object sender, RoutedEventArgs e) => await _client.SendAsync(_client.State.Phase == "Paused" ? "resume" : "pause");
    private async void StopClicked(object sender, RoutedEventArgs e) => await _client.SendAsync("stop");
    private void Apply(RecordingState state)
    {
        bool available = _client.StateAvailable;
        if (_viewReady && _probeWhenIdle && !state.Active && available && !_probing)
        { _probeWhenIdle = false; _ = ProbeAsync(); }
        RecordingHeading.Text = !available ? "录制状态未确认" : state.Active ? state.Control?.Owner == "Automatic" ? "自动录课进行中" : "当前录课" : "立即录制";
        LiveRecordingClock.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        RecordingClock.Visibility = state.Active ? Visibility.Collapsed : Visibility.Visible;
        RecordingSettings.Visibility = !state.Active && _editing ? Visibility.Visible : Visibility.Collapsed;
        IdleRecordingSummary.Visibility = !state.Active && !_editing ? Visibility.Visible : Visibility.Collapsed;
        EditSettings.Content = _editing ? "返回录制" : "修改录制设置";
        SaveSettings.Visibility = ReturnRecordingSettings.Visibility = !state.Active && _editing ? Visibility.Visible : Visibility.Collapsed;
        UpdateReadySummary();
        RecordingClock.Text = available ? TimeSpan.FromSeconds(Math.Max(0, state.Seconds)).ToString(@"hh\:mm\:ss") : "—";
        RecordingPhase.Text = available ? state.Phase switch { "Starting" => "准备中", "Recording" => "录制中", "Paused" => "已暂停", "Pausing" => "暂停中", "Saving" => "保存中", "Saved" => "已保存", "Failed" => "未完成", _ => "准备录制" } : "状态未知";
        RecordingMessage.Text = available ? state.Message : "录制后台未确认当前状态，等待重新连接；不能据此判断录制已经停止。";
        if (state.Error is not null) RecordingError.Text = state.Error;
        else if (state.Active || state.Phase == "Saved" || _stateError is not null && RecordingError.Text == _stateError) RecordingError.Text = "";
        // A fresh state clears its old error without hiding unrelated form/device feedback.
        _stateError = state.Error;
        RecordingMetrics.Text = !available ? "旧时长和统计不代表当前状态；恢复连接后重新读取。" : state.Phase == "Idle"
            ? "低帧率适合课件与讲解，快速视频和动画会不够流畅。"
            : $"{state.Frames:N0} 帧 · {state.Bytes / 1048576.0:F1} MB · 丢帧 {state.DroppedFrames} · 音频缓冲异常 {state.AudioOverruns}";
        RecordingSettings.IsEnabled = available && !state.Active && !_probing;
        RecordStart.Visibility = state.Active ? Visibility.Collapsed : Visibility.Visible;
        RecordStart.IsEnabled = available && !state.Active && !_probing && _environment?.Ready == true;
        SaveSettings.IsEnabled = RecordStart.IsEnabled;
        RecordPause.Visibility = RecordStop.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        RecordPause.IsEnabled = RecordStop.IsEnabled = available && !state.Busy;
        RecordPause.Content = state.Phase == "Paused" ? "继续录制" : "暂停";
        OpenVideo.Visibility = state.OutputFile is null ? Visibility.Collapsed : Visibility.Visible;
        OpenFolder.Visibility = Visibility.Visible;
        OpenFolder.Content = state.Phase == "Failed" ? "打开保留片段" : "打开文件夹";
    }
    private void HideClicked(object sender, RoutedEventArgs e) { if (WorkspaceReturn is { } back) back(); else Hide(); }
    private void OpenVideoClicked(object sender, RoutedEventArgs e) => OpenPath(_client.State.OutputFile);
    private void OpenFolderClicked(object sender, RoutedEventArgs e) => OpenPath(_client.State.OutputFile is { } file ? Path.GetDirectoryName(file) : _client.State.RecoveryDirectory ?? OutputDirectory.Text.Trim());
    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        { RecordingError.Text = "请先填写有效的保存位置，或选择要打开的视频。"; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { RecordingError.Text = "无法打开该位置，请检查保存位置是否有效、文件是否已移动。"; }
    }
}
