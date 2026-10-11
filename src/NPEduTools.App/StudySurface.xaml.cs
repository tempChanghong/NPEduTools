using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class StudySurface : UserControl
{
    private readonly StudyPresentationTimeline _timeline;
    private readonly StudyReminderAudio _audio;
    private readonly Func<Task> _hide;
    private readonly Action _manage;
    private readonly Func<string, StudyHostState, Task<bool>>? _reminderPermission;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly TimeProvider _time;
    private NoiseState? _noise;
    private NoiseState? _drawnNoise;
    private bool _drawnEstimated;
    private string? _shownNotice;
    private string? _returnError;
    private long? _holdAt;
    private bool _busy, _paused = true;
    private long? _renderedAt;
    public int? FreshRenderAgeMs => !_paused && _timeline.Fresh && _renderedAt is { } at &&
        _time.GetElapsedTime(at).TotalMilliseconds is >= 0 and < 1000 ? (int)_time.GetElapsedTime(at).TotalMilliseconds : null;
    private int _animationGeneration;
    private int _reminderGeneration;
    private readonly HashSet<Guid> _disciplineAttempts = [];
    private readonly Queue<Guid> _disciplineOrder = new();
    private string _edgeBand = "None";
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(187, 192, 196));
    private static readonly Brush Collecting = new SolidColorBrush(Color.FromRgb(148, 194, 163));

    public StudySurface(Func<Task> hide, Action manage, bool muted = false, TimeProvider? time = null,
        Func<string, StudyHostState, Task<bool>>? reminderPermission = null)
    {
        _hide = hide; _manage = manage; _reminderPermission = reminderPermission; _time = time ?? TimeProvider.System; _timeline = new(_time); _audio = new(muted);
        InitializeComponent();
        InitializeHomework();
        Clock.FontFamily = Remaining.FontFamily = NoticeTime.FontFamily = new FontFamily(new Uri("pack://application:,,,/NPEduTools.App;component/"), "./Assets/Study/Fonts/#TCloudNumber");
        Previous.Visibility = Next.Visibility = Visibility.Collapsed; PageLabel.Text = "";
        _timer.Tick += (_, _) => Render();
        _holdTimer.Tick += async (_, _) =>
        {
            if (_holdAt is not { } at) return;
            if (!_timeline.Fresh || _timeline.State?.Phase != "Active") { CancelHold(); return; }
            if (_time.GetElapsedTime(at).TotalSeconds < 2) return;
            CancelHold(); await HideAsync();
        };
    }
    public void Apply(StudyHostState state, NoiseState? noise, TimeSpan transportAge = default)
    {
        if (_timeline.State is not { } previous || previous.Phase != state.Phase || previous.Version != state.Version ||
            previous.Window != state.Window || previous.Display?.InstanceId != state.Display?.InstanceId || previous.ClockReady != state.ClockReady)
            _reminderGeneration++;
        _paused = false; _timeline.Apply(state, transportAge); _noise = noise; _timer.Start(); Render();
    }
    public void Suspend()
        { _paused = true; _reminderGeneration++; _timer.Stop(); _pager.Advance(false); CancelHold(); _timeline.Suspend(); CloseSeatingView(); DismissNotice(); SetEdge("None"); _audio.Stop(); }
    public void Stop() { Suspend(); ClearSeating(); _audio.Dispose(); }
    private void Render()
    {
        if (_paused || _timeline.State is not { } state) return;
        bool fresh = _timeline.Fresh;
        Clock.Text = state.SchoolNow?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "--:--:--";
        Clock.Foreground = fresh ? Brushes.White : Muted;
        DateSource.Text = state.SchoolNow is { } date ? $"{date.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"))} · ClassIsland 学校时间" : "等待 ClassIsland 学校时间";
        PhaseText.Text = !fresh ? "学校时间待同步" : state.Phase switch { "Preparing" => "准备开始", "Active" => "自习进行中", "Ended" => "本次自习结束", _ => "自习暂停" };
        WindowText.Text = state.Window is { } w ? $"{w.Start:HH:mm} – {w.End:HH:mm}" : "—";
        RemainingLabel.Text = "剩余时间";
        Remaining.Text = fresh && state.Phase == "Active" && state.Window is { } window && state.SchoolNow is { } now
            ? TimeSpan.FromSeconds(Math.Max(0, Math.Ceiling((window.End - now).TotalSeconds))).ToString(@"hh\:mm\:ss") : "—";
        if (fresh) { ProgressFill.Width = 1470 * _timeline.Progress; ProgressPercent.Text = $"{Math.Floor(_timeline.Progress * 100):0}%"; }
        ProgressCaption.Text = fresh ? "本时段时间进度" : "时间待同步 · 进度暂停";
        SessionNote.Text = _returnError ?? (state.Display?.StageAudioError is not null ? "阶段提示音暂不可用" :
            state.Phase == "Preparing" ? "即将开始自习" : "自习安排由学校管理后台设置");
        bool capturing = fresh && state.Phase == "Active" && state.Execution is { Owner: "Schedule", State: "Active", SignalFresh: true } execution &&
            _noise is { State: "Active" } noise && execution.SessionId == noise.SessionId &&
            (noise.Quality == "DigitalSilence" || noise.Quality is "Good" or "Clipping" && noise.CurrentDbfs is { } level && double.IsFinite(level));
        NoiseStatus.Text = !fresh ? "监测状态待确认" : capturing ? "正在监测" : state.Execution?.Owner == "Manual" ? "当前为手动监测" :
            state.Settings?.Noise.Enabled == false ? "噪音监测未启用" :
            state.Execution?.Reason == "MICROPHONE_NOT_CONFIGURED" ? "尚未选择麦克风" :
            state.CaptureState switch { "STARTING" or "WAITING_FRESH_SIGNAL" or "ACTIVE" => "正在等待采样", "STOPPING" => "正在停止监测", "STOPPED" => "监测已停止", "FAULTED" => "监测暂不可用", _ => "尚未开始采集" };
        NoiseStatus.Foreground = capturing ? Collecting : Muted;
        bool estimated = state.Settings?.Noise.Estimated is not null;
        NoiseDetail.Text = estimated ? NoiseSignalPresentation.EstimateReason(_noise?.Estimate) : "dBFS · 数字信号电平";
        string mode = state.Settings?.Presentation.NoiseDisplay ?? "StatusTrend";
        TrendCanvas.Visibility = capturing && mode == "StatusTrend" ? Visibility.Visible : Visibility.Collapsed;
        Dbfs.Visibility = mode == "StatusDbfs" ? Visibility.Visible : Visibility.Collapsed;
        Dbfs.Text = estimated ? NoiseSignalPresentation.Estimated(capturing ? _noise?.Estimate?.CurrentDbA : null) :
            capturing ? NoiseSignalPresentation.Level(_noise!.CurrentDbfs) + " dBFS" : "— dBFS";
        DrawTrend(capturing && mode == "StatusTrend" && (!estimated || _noise?.Estimate?.State == "Active"), estimated);
        bool matchingThreshold = capturing && (!estimated || _noise?.Estimate?.State == "Active") && state.Threshold is { } threshold && threshold.SessionId == _noise!.SessionId;
        bool reminderUnavailable = matchingThreshold && state.Threshold!.ReminderError is not null;
        if (reminderUnavailable) NoiseDetail.Text += " · 纪律提醒暂不可用";
        NoiseDetail.ToolTip = estimated ? "估算 dB(A) 使用本机参考与 A 加权 F/S 统计。参考失效时暂停该单位的阈值判定；dBFS 采样继续。" : "dBFS 是数字信号电平，不能直接当作教室声压级。";
        SetEdge(matchingThreshold ? state.Threshold!.Band : "None");
        if (!capturing || estimated && _noise?.Estimate?.State != "Active") _timeline.CancelDiscipline();
        if (_shownNotice == "Discipline" && _timeline.Notice != "Discipline") _audio.Stop();
        HoldButton.IsEnabled = fresh && state.Phase == "Active" && !_busy;
        ManageButton.IsEnabled = HoldButton.IsEnabled;
        if (!HoldButton.IsEnabled) CancelHold();
        var notice = _timeline.Notice;
        if (notice != _shownNotice)
        {
            if (notice is null) DismissNotice(); else PresentNotice(notice);
        }
        if (_timeline.TakeCue() is { } cue && fresh) _ = PlayReminderAsync(cue, state);
        if (matchingThreshold && _timeline.Notice is null && state.Threshold is { Band: "High", DisciplineId: { } id, ReminderError: null } && _disciplineAttempts.Add(id))
        {
            _disciplineOrder.Enqueue(id); if (_disciplineOrder.Count > 128) _disciplineAttempts.Remove(_disciplineOrder.Dequeue());
            _ = PlayDisciplineAsync(state, id);
        }
        if (!fresh) _audio.Stop();
        RenderSeating();
        RenderHomework(state, fresh);
        NoticeTime.Text = Clock.Text;
        _renderedAt = fresh ? _time.GetTimestamp() : null;
    }
    private void DrawTrend(bool capturing, bool estimated)
    {
        if (capturing && ReferenceEquals(_drawnNoise, _noise) && _drawnEstimated == estimated) return;
        _drawnEstimated = estimated;
        _drawnNoise = capturing ? _noise : null;
        TrendCanvas.Children.Clear();
        if (!capturing || _noise?.Summary is not { } summary) return;
        double end = Math.Max(60, summary.ElapsedSeconds), start = end - 60, prior = -10;
        Polyline? line = null;
        foreach (var point in estimated ? (_noise.EstimatedTrend ?? []).Select(p => (p.Seconds, Level: p.DbA, p.Quality)) :
            _noise.Trend.Select(p => (p.Seconds, Level: p.Dbfs, p.Quality)))
        {
            if (point.Seconds < start) continue;
            if (point.Level is not { } level || !double.IsFinite(level) || point.Quality == "Invalid") { line = null; continue; }
            if (line is null || point.Seconds - prior > 1)
            { line = new() { Stroke = new SolidColorBrush(Color.FromRgb(131, 170, 181)), StrokeThickness = 2 }; TrendCanvas.Children.Add(line); }
            double y = estimated ? (130 - Math.Clamp(level, 30, 130)) / 100 * 48 : -Math.Clamp(level, -100, 0) / 100 * 48;
            line.Points.Add(new((point.Seconds - start) / 60 * 360, y)); prior = point.Seconds;
        }
    }
    private async Task PlayReminderAsync(string cue, StudyHostState state)
    {
        int generation = _reminderGeneration;
        if (_reminderPermission is not null && !await _reminderPermission(cue, state)) return;
        if (!_paused && generation == _reminderGeneration && _timeline.Fresh && _timeline.Notice == cue && _timeline.State is { } current &&
            current.Version == state.Version && current.Window == state.Window && current.Display?.InstanceId == state.Display?.InstanceId)
            _audio.Play(cue);
    }
    private async Task PlayDisciplineAsync(StudyHostState state, Guid id)
    {
        int generation = _reminderGeneration;
        // Formal StudyWindow always supplies the Host permission check. An unconnected preview is silent.
        if (_reminderPermission is null || !await _reminderPermission("Discipline", state)) return;
        if (_paused || generation != _reminderGeneration || !_timeline.Fresh || _timeline.State is not { } current ||
            current.Version != state.Version || current.Window != state.Window || current.Display?.InstanceId != state.Display?.InstanceId ||
            current.Threshold?.DisciplineId != id || current.Threshold.SessionId != _noise?.SessionId ||
            current.Settings?.Noise.Estimated is not null && _noise?.Estimate?.State != "Active" ||
            _noise is not { State: "Active", Quality: "Good" or "Clipping" } || !_timeline.BeginDiscipline()) return;
        PresentNotice("Discipline"); _audio.Play("Discipline");
        // The five-second overlay pauses homework just like the other stage notices.
        RenderHomework(current, true);
    }
    private void SetEdge(string band)
    {
        if (band is not ("Medium" or "High")) band = "None";
        if (_edgeBand == band) return;
        _edgeBand = band;
        NoiseEdge.BeginAnimation(OpacityProperty, null);
        NoiseEdge.Visibility = band == "None" ? Visibility.Collapsed : Visibility.Visible;
        NoiseEdge.Opacity = band == "High" ? .85 : .36;
        // Four static gradients, one short entrance; no fullscreen blur, looping animation or frame buffer.
        if (band != "None") NoiseEdge.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, NoiseEdge.Opacity, TimeSpan.FromMilliseconds(260)) { FillBehavior = FillBehavior.Stop });
    }
    private void PresentNotice(string kind)
    {
        DismissNotice(); _shownNotice = kind; int generation = _animationGeneration;
        NoticeTitle.Text = kind switch { "Preparing" => "准备开始", "Active" => "自习开始", "Discipline" => "注意纪律", _ => "自习结束" };
        StageNotice.Background = new SolidColorBrush(kind switch { "Preparing" => Color.FromArgb(224, 241, 196, 15), "Active" => Color.FromArgb(224, 255, 59, 48), "Discipline" => Color.FromArgb(238, 158, 23, 32), _ => Color.FromArgb(224, 46, 204, 113) });
        StageNotice.Visibility = Visibility.Visible;
        var intro = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { FillBehavior = FillBehavior.Stop };
        intro.Completed += (_, _) => { if (generation == _animationGeneration) { NoticeCopy.BeginAnimation(OpacityProperty, null); NoticeCopy.Opacity = 1; } };
        NoticeCopy.Opacity = 1; NoticeCopy.BeginAnimation(OpacityProperty, intro);
        // Blur is restricted to the text glyphs in XAML. No fullscreen blur or permanent pulse timer.
    }
    private void DismissNotice()
    { _animationGeneration++; _shownNotice = null; StageNotice.Visibility = Visibility.Collapsed; NoticeCopy.BeginAnimation(OpacityProperty, null); }
    public async Task HideAsync()
    {
        if (_busy || !_timeline.Fresh || _timeline.State?.Phase != "Active") return;
        _busy = true; HoldButton.IsEnabled = false;
        try { await _hide(); }
        finally { _busy = false; if (!_paused) Render(); }
    }
    public void BeginHold()
    { if (_busy || !_timeline.Fresh || _timeline.State?.Phase != "Active" || _holdAt is not null) return; _holdAt = _time.GetTimestamp(); HoldText.Text = "继续按住…"; _holdTimer.Start(); }
    public void CancelHold() { _holdAt = null; _holdTimer.Stop(); HoldText.Text = "临时使用电脑"; }
    public void ReturnError(string? message) { _returnError = message; if (!_paused) Render(); }
    private void HoldMouseDown(object sender, MouseButtonEventArgs e) { if (e.StylusDevice is not null) return; BeginHold(); HoldButton.CaptureMouse(); e.Handled = true; }
    private void HoldMouseUp(object sender, MouseButtonEventArgs e) { CancelHold(); HoldButton.ReleaseMouseCapture(); e.Handled = true; }
    private void HoldTouchDown(object sender, TouchEventArgs e) { BeginHold(); HoldButton.CaptureTouch(e.TouchDevice); e.Handled = true; }
    private void HoldTouchUp(object sender, TouchEventArgs e) { CancelHold(); HoldButton.ReleaseTouchCapture(e.TouchDevice); e.Handled = true; }
    private void HoldLost(object sender, RoutedEventArgs e) => CancelHold();
    private void HoldKeyDown(object sender, KeyEventArgs e) { if (e.Key is Key.Space or Key.Enter) { if (!e.IsRepeat) BeginHold(); e.Handled = true; } }
    private void HoldKeyUp(object sender, KeyEventArgs e) { if (e.Key is Key.Space or Key.Enter) { CancelHold(); e.Handled = true; } }
    private async void ManageClicked(object sender, RoutedEventArgs e)
    { await HideAsync(); if (_paused) _manage(); }
}
