using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class OnboardingWindow : Window
{
    private readonly string _endpoint;
    private readonly Action<OnboardingState> _save;
    private readonly Action _preferences, _classIsland, _recording, _shortcuts;
    private readonly Action<bool> _finish;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private SchoolClockTracker _clock = new();
    private OnboardingState _state;
    private bool _closing, _clockBusy, _probeBusy, _connected, _scheduleReady, _preferencesReadable, _clockValidationFailed;
    private DateOnly? _confirmedDate;
    private Guid _bridgeInstance;
    private long _epoch;

    internal OnboardingWindow(string endpoint, OnboardingState state, Action<OnboardingState> save,
        Action preferences, Action classIsland, Action recording, Action shortcuts, Action<bool> finish, string? initialError)
    {
        _endpoint = endpoint; _save = save; _preferences = preferences; _classIsland = classIsland;
        _recording = recording; _shortcuts = shortcuts; _finish = finish;
        // Rechecking starts a new walkthrough only on the user's explicit open action.
        _state = state.Completed ? new(Features: state.Features) : state;
        InitializeComponent();
        UseShortcuts.IsChecked = state.Features.HasFlag(OnboardingFeatures.Shortcuts);
        UseTouch.IsChecked = state.Features.HasFlag(OnboardingFeatures.Touch);
        UseRecording.IsChecked = state.Features.HasFlag(OnboardingFeatures.Recording);
        UseAutomatic.IsChecked = state.Features.HasFlag(OnboardingFeatures.Automatic);
        ConfirmClock.Checked += (_, _) =>
        {
            _confirmedDate = _clock.Read(_elapsed.Elapsed).Now is { } now ? DateOnly.FromDateTime(now.Date) : null;
            if (_clockValidationFailed) { ErrorText.Text = ""; _clockValidationFailed = false; }
        };
        _timer.Tick += async (_, _) => { if (_state.Step == "classisland") await CheckClockAsync(); };
        Loaded += (_, _) => _timer.Start();
        Activated += (_, _) => { if (_state.Step == "preferences") RefreshPreferences(); };
        Closing += (_, e) =>
        {
            if (!_closing && !Persist(CurrentChoices() with { Deferred = true })) e.Cancel = true;
        };
        Closed += (_, _) => { _timer.Stop(); _lifetime.Cancel(); };
        Render();
        ErrorText.Text = initialError ?? "";
        LeaveWithoutSave.Visibility = initialError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Shutdown()
    {
        if (!_closing) Persist(CurrentChoices() with { Deferred = true });
        _closing = true; Close();
    }

    private OnboardingState CurrentChoices()
    {
        if (_state.Step != "welcome") return _state;
        var features = (UseShortcuts.IsChecked == true ? OnboardingFeatures.Shortcuts : 0) |
            (UseTouch.IsChecked == true ? OnboardingFeatures.Touch : 0) |
            (UseRecording.IsChecked == true ? OnboardingFeatures.Recording : 0) |
            (UseAutomatic.IsChecked == true ? OnboardingFeatures.Automatic : 0);
        return features == _state.Features ? _state : _state.Select(features);
    }

    private bool Persist(OnboardingState next)
    {
        try { _save(next); _state = next; ErrorText.Text = ""; LeaveWithoutSave.Visibility = Visibility.Collapsed; return true; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ErrorText.Text = "进度未保存，本步未完成。请检查配置目录权限后重试，也可以不保存并关闭引导。"; LeaveWithoutSave.Visibility = Visibility.Visible; return false; }
    }

    private static string Label(string step) => step switch
    { "welcome" => "选择用途", "preferences" => "使用偏好", "classisland" => "连接学校时间", "recording" => "录制准备", _ => "准备好开始了" };

    private void Render()
    {
        foreach (var (panel, step) in new[] { (WelcomePage, "welcome"), (PreferencesPage, "preferences"),
            (ClassIslandPage, "classisland"), (RecordingPage, "recording"), (ReviewPage, "review") })
            panel.Visibility = _state.Step == step ? Visibility.Visible : Visibility.Collapsed;
        StepTitle.Text = Label(_state.Step);
        ProgressText.Text = $"初始设置 · {Array.IndexOf(_state.Steps, _state.Step) + 1} / {_state.Steps.Length}";
        BackButton.Visibility = _state.Step == "welcome" ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = _state.Step is "welcome" or "review" ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _state.Step == "welcome" ? "开始设置" : _state.Step == "review" ? "完成并开始使用" : "下一步";
        ShortcutHelp.Visibility = _state.Features.HasFlag(OnboardingFeatures.Shortcuts) ? Visibility.Visible : Visibility.Collapsed;
        TouchHelp.Visibility = _state.Features.HasFlag(OnboardingFeatures.Touch) ? Visibility.Visible : Visibility.Collapsed;
        if (_state.Step == "preferences") RefreshPreferences();
        if (_state.Step == "classisland")
        {
            _clock = new(); _connected = false; _scheduleReady = false;
            ConfirmClock.IsChecked = false; ConfirmClock.IsEnabled = false;
            _ = CheckClockAsync();
        }
        if (_state.Step == "recording")
        { RecordingText.Text = "请检查已保存的设置；检查不会开始采集。"; }
        if (_state.Step == "review")
        {
            ReviewText.Text = string.Join("\n", _state.Steps.Where(s => s is not ("welcome" or "review")).Select(s =>
                $"{Label(s)}：{((_state.Skipped ?? []).Contains(s) ? "已跳过，待配置" : (_state.Reviewed ?? []).Contains(s) ? s == "preferences" ? "已查看，使用现有设置" : "本次已检查（非持续状态）" : "尚待处理")}"));
            FinishHint.Text = _state.Features.HasFlag(OnboardingFeatures.Automatic)
                ? "下一步打开自动录课计划。请核对周期规则、单日计划和预演结果，再主动启用自动录制。完成引导本身不会开启录制。"
                : "下一步进入侧边栏。需要管理时，随时打开主窗口。";
        }
    }

    private void RefreshPreferences()
    {
        try
        {
            var settings = new StartupPreferencesStore(StartupPreferencesStore.PathFor(_endpoint)).Read();
            _preferencesReadable = true;
            PreferencesText.Text = $"当前保存的偏好：登录后{(settings.EdgeOnlyAtLogin ? "仅显示侧栏" : "显示主窗口")}；启动时{(settings.EnableTouchOnLaunch ? "开启" : "不开启")}触摸辅助。\n侧栏位置与 Windows 登录启动请在偏好页查看。";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _preferencesReadable = false; PreferencesText.Text = "现有偏好无法读取，请在偏好页查看提示。可暂时跳过此项。"; }
    }

    private async void NextClicked(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        if (_state.Step == "preferences")
        {
            RefreshPreferences();
            if (!_preferencesReadable) { ErrorText.Text = "现有偏好无法读取，请先处理或跳过此项。"; return; }
        }
        if (_state.Step == "classisland")
        {
            if (!_connected || !_scheduleReady || !_clock.Read(_elapsed.Elapsed).CanStart || ConfirmClock.IsChecked != true)
            { _clockValidationFailed = true; ErrorText.Text = "请等待本体、桥接和课表检查通过，并核对学校日期与铃声；也可以跳过此项。"; return; }
        }
        if (_state.Step == "recording")
        {
            // Always re-read/probe on navigation: settings or devices may have changed since the last check.
            if (!await CheckRecordingAsync()) return;
        }
        var next = CurrentChoices().Advance(false);
        if (!Persist(next)) return;
        if (next.Completed)
        { _closing = true; Close(); _finish(next.Features.HasFlag(OnboardingFeatures.Automatic)); }
        else Render();
    }
    private void BackClicked(object sender, RoutedEventArgs e) { if (Persist(CurrentChoices().Back())) Render(); }
    private void SkipClicked(object sender, RoutedEventArgs e) { if (Persist(_state.Advance(true))) Render(); }
    private void LaterClicked(object sender, RoutedEventArgs e) => Close();
    private void LeaveWithoutSaveClicked(object sender, RoutedEventArgs e) { _closing = true; Close(); }
    private void PreferencesClicked(object sender, RoutedEventArgs e) => _preferences();
    private void ClassIslandClicked(object sender, RoutedEventArgs e) => _classIsland();
    private void RecordingClicked(object sender, RoutedEventArgs e) => _recording();
    private void ShortcutsClicked(object sender, RoutedEventArgs e) => _shortcuts();

    private async Task<HostResponse> RequestAsync(string capability)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        return await HostClient.RequestAsync(_endpoint, new HostRequest(Protocol.Version, Guid.NewGuid(), capability, TimeoutMs: 1500), deadline.Token);
    }

    private async Task CheckClockAsync()
    {
        if (_clockBusy || _lifetime.IsCancellationRequested) return;
        _clockBusy = true;
        try
        {
            var config = await RequestAsync("classisland.config.get");
            string? path = config.Launch?.Settings.ExecutablePath;
            PathText.Text = config.Outcome != "Succeeded" || config.Launch?.StorageWarning is not null ? "程序路径：暂时无法读取配置"
                : string.IsNullOrWhiteSpace(path) ? "程序路径：尚未保存。若 ClassIsland 已连接，可继续；路径用于以后启动本体。"
                : File.Exists(path) ? "程序路径：文件存在 · " + path : "程序路径：文件不存在，请重新选择";
            var connection = await RequestAsync("classisland.status");
            _connected = connection.Outcome == "Succeeded" && connection.Status is not null;
            ConnectionText.Text = _connected ? "本体：已连接" : "本体：未连接，请从管理窗口检查并启动 ClassIsland";
            var start = _elapsed.Elapsed;
            var response = await RequestAsync("classisland.school-clock");
            var frame = response.SchoolClock ?? SchoolClockFrame.Unavailable("尚未取得桥接时间");
            _clock.Accept(frame, _elapsed.Elapsed, (_elapsed.Elapsed - start).TotalMilliseconds);
            var reading = _clock.Read(_elapsed.Elapsed);
            _scheduleReady = frame.Schedule is { ClockVerified: true, Enabled: true } schedule &&
                frame.SchoolNow is { } schoolNow && schedule.Date == DateOnly.FromDateTime(schoolNow.Date);
            bool ready = _connected && reading.Fresh && (reading.CanStart || reading.DateNeedsReview) && _scheduleReady;
            if (!ready || _bridgeInstance != frame.BridgeInstanceId || _epoch != frame.Epoch ||
                _confirmedDate is { } date && reading.Now is { } now && date != DateOnly.FromDateTime(now.Date)) ConfirmClock.IsChecked = false;
            _bridgeInstance = frame.BridgeInstanceId; _epoch = frame.Epoch;
            ConfirmClock.IsEnabled = ready;
            if (ConfirmClock.IsChecked == true && reading.DateNeedsReview) _clock.ConfirmDate();
            BridgeText.Text = reading.Fresh ? $"桥接：已取得时间；课表{(_scheduleReady ? "已就绪" : "未就绪，请检查是否加载并启用")}" : "桥接：未取得可用时间，请检查插件与连接";
            ClockText.Text = reading.Fresh ? $"学校时间：{reading.Now:yyyy-MM-dd HH:mm:ss}\n{reading.Message}" : "学校时间：暂不可用；" + reading.Message;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _connected = false; _scheduleReady = false; _clock.Unavailable("连接失败，等待重新检查", _elapsed.Elapsed);
            ConfirmClock.IsChecked = false; ConfirmClock.IsEnabled = false;
            ConnectionText.Text = "本体：暂时无法检查，请确认后台与 ClassIsland 已启动";
            BridgeText.Text = "桥接：尚未确认"; ClockText.Text = "学校时间：暂不可用";
        }
        finally { _clockBusy = false; }
    }

    private async void CheckRecordingClicked(object sender, RoutedEventArgs e) => await CheckRecordingAsync();
    private async Task<bool> CheckRecordingAsync()
    {
        if (_probeBusy || _lifetime.IsCancellationRequested) return false;
        _probeBusy = true; NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = CheckRecordingButton.IsEnabled = false;
        RecordingText.Text = "正在检查设备与保存目录，不会开始录制…";
        try
        {
            var options = RecordingWindow.ReadSavedOptions(_endpoint) ?? throw new InvalidDataException("请先在录制窗口保存录制设置。");
            var environment = await RecordingClient.ProbeAsync();
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!environment.Ready) throw new InvalidDataException(environment.Error ?? "录制环境未就绪。");
            if (!environment.Displays.Any(d => d.Id == options.Display)) throw new InvalidDataException("保存的屏幕已不可用，请重新选择。");
            if (options.SystemAudio && !environment.Speakers.Any(d => options.SpeakerId == "default" || d.Id == options.SpeakerId))
                throw new InvalidDataException("保存的系统声音设备不可用，请连接设备或取消该音源。");
            if (options.Microphone && !environment.Microphones.Any(d => options.MicrophoneId == "default" || d.Id == options.MicrophoneId))
                throw new InvalidDataException("保存的麦克风不可用，请连接设备或取消该音源。");
            if (RecordingWindow.ReadSavedOptions(_endpoint) != options) throw new InvalidDataException("录制设置刚刚发生变化，请重新检查。");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(options.OutputDirectory);
                string path = Path.Combine(options.OutputDirectory, ".npedutools-check-" + Guid.NewGuid().ToString("N"));
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose))
                { file.WriteByte(0); file.Flush(true); }
                var drive = new DriveInfo(Path.GetPathRoot(options.OutputDirectory)!);
                if (drive.AvailableFreeSpace < 512L * 1024 * 1024) throw new IOException("保存位置剩余空间不足 512 MB。");
            }, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            RecordingText.Text = $"检查通过：屏幕可用，保存目录可写。\n{options.MaximumHeight}p · {options.FramesPerSecond} fps · 系统声音{(options.SystemAudio ? "已选" : "关闭")} · 麦克风{(options.Microphone ? "已选" : "关闭")}\n保存到：{options.OutputDirectory}\n设备枚举通过；实际画面与声音请通过试录回放确认。";
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { RecordingText.Text = "尚未准备好：" + (error is OperationCanceledException ? "检查已取消。" : error.Message); return false; }
        finally
        { _probeBusy = false; NextButton.IsEnabled = BackButton.IsEnabled = SkipButton.IsEnabled = CheckRecordingButton.IsEnabled = true; }
    }
}
