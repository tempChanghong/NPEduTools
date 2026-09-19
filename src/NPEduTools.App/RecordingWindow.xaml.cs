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
    private readonly string _preferencesPath;
    private bool _shutdown, _readable = true, _probing;
    private RecordingEnvironment? _environment;
    private RecordingOptions? _saved;
    internal RecordingWindow(RecordingClient client, string endpoint)
    {
        _client = client;
        _preferencesPath = StartupPreferencesStore.PathFor(endpoint).Replace(".startup.json", ".recording.json", StringComparison.Ordinal);
        InitializeComponent();
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
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { _readable = false; RecordingError.Text = "保存的录制偏好无法读取，原文件已保留。本次仍可选择设置并录制。"; }
        _client.Changed += Apply;
        Loaded += async (_, _) => { await ProbeAsync(); Apply(_client.State); };
        Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => _client.Changed -= Apply;
    }
    public void Shutdown() { _shutdown = true; Close(); }
    private async Task ProbeAsync()
    {
        if (_probing || _client.State.Active) return;
        _probing = true; RecordStart.IsEnabled = false;
        try
        {
            string? display = DisplayChoice.SelectedValue as string ?? _saved?.Display;
            string speaker = SpeakerChoice.SelectedValue as string ?? _saved?.SpeakerId ?? "default";
            string microphone = MicrophoneChoice.SelectedValue as string ?? _saved?.MicrophoneId ?? "default";
            _environment = await RecordingClient.ProbeAsync();
            DisplayChoice.ItemsSource = _environment.Displays;
            DisplayChoice.SelectedValue = _environment.Displays.FirstOrDefault(d => d.Id == display)?.Id ?? _environment.Displays.FirstOrDefault(d => d.Primary)?.Id;
            SpeakerChoice.ItemsSource = new[] { new RecordingDevice("default", "系统默认输出设备") }.Concat(_environment.Speakers).ToArray();
            MicrophoneChoice.ItemsSource = new[] { new RecordingDevice("default", "系统默认麦克风") }.Concat(_environment.Microphones).ToArray();
            SpeakerChoice.SelectedValue = speaker; MicrophoneChoice.SelectedValue = microphone;
            if (SpeakerChoice.SelectedIndex < 0) SpeakerChoice.SelectedIndex = 0;
            if (MicrophoneChoice.SelectedIndex < 0) MicrophoneChoice.SelectedIndex = 0;
            if (_environment.Error is not null) RecordingError.Text = _environment.Error;
            else if (_readable) RecordingError.Text = "";
            UpdateSummary(); UpdateSound();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { RecordingError.Text = error.Message; _environment = null; }
        finally { _probing = false; Apply(_client.State); }
    }
    private int Fps => int.Parse((string)((ComboBoxItem)FpsChoice.SelectedItem).Tag);
    private int MaximumHeight => int.Parse((string)((ComboBoxItem)QualityChoice.SelectedItem).Tag);
    private void UpdateSummary()
    {
        if (DisplayChoice.SelectedItem is not RecordingDisplay display) return;
        var (width, height) = RecordingContract.OutputSize(display.Width, display.Height, MaximumHeight);
        CaptureSummary.Text = $"采集 {display.Width} × {display.Height} → 保存 {width} × {height} · {Fps} fps · MP4";
    }
    private void ChoicesChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateSummary(); }
    private void SoundChanged(object sender, RoutedEventArgs e) { if (IsLoaded) UpdateSound(); }
    private void UpdateSound() { SpeakerChoice.IsEnabled = SystemSound.IsChecked == true; MicrophoneChoice.IsEnabled = MicrophoneSound.IsChecked == true; }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ProbeAsync();
    private void BrowseClicked(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择微课保存位置" };
        if (picker.ShowDialog(this) == true) OutputDirectory.Text = picker.FolderName;
    }
    private async void StartClicked(object sender, RoutedEventArgs e)
    {
        var options = OptionsFromForm(); if (options is null) return;
        SavePreferences(options);
        await _client.SendAsync("start", options);
    }
    private RecordingOptions? OptionsFromForm()
    {
        if (_client.State.Active || _probing || _environment?.Ready != true) return null;
        var options = new RecordingOptions(DisplayChoice.SelectedValue as string ?? "", OutputDirectory.Text.Trim(), Fps, MaximumHeight,
            SystemSound.IsChecked == true, MicrophoneSound.IsChecked == true, SpeakerChoice.SelectedValue as string ?? "default", MicrophoneChoice.SelectedValue as string ?? "default");
        if (RecordingContract.Validate(options) is { } error) { RecordingError.Text = error; return null; }
        if ((options.Microphone && _environment.Microphones.Length == 0) || (options.SystemAudio && _environment.Speakers.Length == 0))
        { RecordingError.Text = "所选音源没有可用设备。请连接设备并重新检测，或取消该音源。"; return null; }
        return options;
    }
    private void SavePreferences(RecordingOptions options)
    {
        if (_readable)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_preferencesPath)!);
                File.WriteAllText(_preferencesPath + ".tmp", JsonSerializer.Serialize(new Preferences(1, options)));
                File.Move(_preferencesPath + ".tmp", _preferencesPath, true); _saved = options;
                RecordingError.Text = "录制设置已保存，可在自动录课计划中启用。";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecordingError.Text = "录制设置未能保存；本次使用当前选项。"; }
        }
    }
    private void SaveSettingsClicked(object sender, RoutedEventArgs e)
    { if (OptionsFromForm() is { } options) SavePreferences(options); }
    private async void PauseClicked(object sender, RoutedEventArgs e) => await _client.SendAsync(_client.State.Phase == "Paused" ? "resume" : "pause");
    private async void StopClicked(object sender, RoutedEventArgs e) => await _client.SendAsync("stop");
    private void Apply(RecordingState state)
    {
        RecordingClock.Text = TimeSpan.FromSeconds(Math.Max(0, state.Seconds)).ToString(@"hh\:mm\:ss");
        RecordingPhase.Text = state.Phase switch { "Starting" => "准备中", "Recording" => "录制中", "Paused" => "已暂停", "Pausing" => "暂停中", "Saving" => "保存中", "Saved" => "已保存", "Failed" => "未完成", _ => "准备录制" };
        RecordingMessage.Text = state.Message;
        if (state.Error is not null) RecordingError.Text = state.Error;
        else if (state.Active || state.Phase == "Saved") RecordingError.Text = "";
        if (state.Phase != "Idle") RecordingMetrics.Text = $"{state.Frames:N0} 帧 · {state.Bytes / 1048576.0:F1} MB · 丢帧 {state.DroppedFrames} · 音频缓冲异常 {state.AudioOverruns}";
        RecordingSettings.IsEnabled = !state.Active && !_probing;
        RecordStart.Visibility = state.Active ? Visibility.Collapsed : Visibility.Visible;
        RecordStart.IsEnabled = !state.Active && !_probing && _environment?.Ready == true;
        SaveSettings.IsEnabled = RecordStart.IsEnabled;
        RecordPause.Visibility = RecordStop.Visibility = state.Active ? Visibility.Visible : Visibility.Collapsed;
        RecordPause.IsEnabled = RecordStop.IsEnabled = !state.Busy;
        RecordPause.Content = state.Phase == "Paused" ? "继续录制" : "暂停";
        OpenVideo.Visibility = state.OutputFile is null ? Visibility.Collapsed : Visibility.Visible;
        OpenFolder.Visibility = Visibility.Visible;
        OpenFolder.Content = state.Phase == "Failed" ? "打开保留片段" : "打开文件夹";
    }
    private void HideClicked(object sender, RoutedEventArgs e) => Hide();
    private void OpenVideoClicked(object sender, RoutedEventArgs e) => OpenPath(_client.State.OutputFile);
    private void OpenFolderClicked(object sender, RoutedEventArgs e) => OpenPath(_client.State.OutputFile is { } file ? Path.GetDirectoryName(file) : _client.State.RecoveryDirectory ?? OutputDirectory.Text.Trim());
    private void OpenPath(string? path)
    {
        if (path is null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { RecordingError.Text = "无法打开该位置，请检查文件是否已移动。"; }
    }
}
