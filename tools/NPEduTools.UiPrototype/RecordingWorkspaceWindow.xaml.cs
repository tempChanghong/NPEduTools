using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NPEduTools.Core;

namespace NPEduTools.UiPrototype;

public partial class RecordingWorkspaceWindow : Window
{
    internal readonly RecordingPrototypeState State = new();
    private bool _ready, _folded;
    private string _page = "Home", _tab = "Manual";
    private string _settings = "教室大屏 · 1080p · 8 fps · 麦克风＋系统声音", _folder = @"D:\课堂录像";
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    internal RecordingWorkspaceWindow()
    {
        InitializeComponent();
        PlanDate.SelectedDate = RecordingPrototypeState.DemoDate.ToDateTime(TimeOnly.MinValue);
        DisplayChoice.SelectedIndex = Quality.SelectedIndex = Fps.SelectedIndex = MicChoice.SelectedIndex = SpeakerChoice.SelectedIndex = RecordFilter.SelectedIndex = 0;
        for (int day = 0; day < 7; day++)
            Weekdays.Children.Add(new CheckBox { Content = new[] {"日","一","二","三","四","五","六"}[day], Tag = day, IsChecked = true, Margin = new Thickness(0,0,16,10) });
        ExclusionsSummary.Text = string.Join("、", RecordingPlanBook.DefaultExcluded);
        _timer.Tick += (_, _) => { if (State.Phase == "Recording") State.Seconds++; RenderStatus(); };
        Closed += (_, _) => _timer.Stop();
        Loaded += (_, _) => { NPEduTools.App.DesktopWindowBounds.KeepVisible(this); ResizeNavigation(); };
        SizeChanged += (_, _) => { if (_ready) ResizeNavigation(); };
        _ready = true; Render(); _timer.Start();
    }
    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    private void Tell(string text, bool error = false)
    {
        FeedbackText.Text = text; Feedback.Visibility = Visibility.Visible;
        Feedback.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#FFF0E7" : "#EAF3EC"));
    }
    private void Navigate(string page, string? tab = null)
    {
        _page = page; if (tab is not null) _tab = tab;
        Feedback.Visibility = Visibility.Collapsed; Render();
    }
    private void Render()
    {
        if (!_ready) return;
        HomePage.Visibility = Show(_page == "Home"); RecordingPage.Visibility = Show(_page == "Recording");
        SettingsPage.Visibility = Show(_page == "Settings"); WeeklyPage.Visibility = Show(_page == "Weekly");
        HomeNav.Tag = _page == "Home" ? "active" : null; RecordingNav.Tag = _page != "Home" ? "active" : null;
        PageTitle.Text = _page switch { "Home" => "主页", "Settings" => "录制设置", "Weekly" => "每周安排", _ => "录课" };
        PageHint.Text = _page switch { "Home" => "当前任务、下一节安排和需要处理的记录。", "Settings" => "画面、声音与保存位置，只配置一次。", "Weekly" => "把每周重复的规则集中放在这里。", _ => "今天的课，提前安排好。" };
        ManualPane.Visibility = Show(_tab == "Manual"); PlanPane.Visibility = Show(_tab == "Plan"); RecordsPane.Visibility = Show(_tab == "Records");
        foreach (var pair in new[] { (ManualTab,"Manual"),(PlanTab,"Plan"),(RecordsTab,"Records") })
            pair.Item1.Style = (Style)FindResource(_tab == pair.Item2 ? "Primary" : "Quiet");
        Lessons.ItemsSource = State.Plans().Select(p => new DemoLessonRow(p.Lesson.Number, $"{p.Lesson.Start:HH:mm}–{p.Lesson.End:HH:mm}",
            p.Lesson.Number == 1 ? "周测 · 晨间时段" : p.Lesson.Number == 2 ? "语文 · 第一节" : p.Lesson.Subject,
            p.Selected ? $"录制 {p.Start:HH:mm}–{p.End:HH:mm}" : "不安排录制",
            State.Handled(p) ? "已处理" : p.Conflict ? "需调整" : p.Selected ? "会录" : "不录", State.Handled(p) ? "任务已执行或跳过，本次不会自动重录。" : DescribeCourse(p), p.Selected ? "当天不录" : "当天录制")).ToArray();
        DraftHint.Text = State.Dirty ? "有未保存的改动；当前自动录课仍使用上次保存的安排。" : "按已保存安排显示；修改课程只影响当天。";
        SavePlan.IsEnabled = DiscardPlan.IsEnabled = State.Dirty;
        var filter = RecordFilter.SelectedIndex;
        RecordItems.ItemsSource = State.Records.Where(r => filter == 0 || filter == 1 && r.Status == "未完成" || filter == 2 && r.Status == "已保存").ToArray();
        if (State.SelectedRecord is { } record)
        {
            RecordDetail.Visibility = Visibility.Visible; RecordDetailHeading.Text = record.Subject + " · " + record.Status;
            RecordPath.Text = string.IsNullOrEmpty(record.Path) ? "这项任务已跳过，没有生成视频。" : record.Path;
        }
        else RecordDetail.Visibility = Visibility.Collapsed;
        RenderStatus(); ResizeNavigation();
    }
    private void RenderStatus()
    {
        if (!_ready) return;
        SchoolTime.Text = $"{State.Now:HH:mm} · 学校时间";
        ClockLabel.Text = State.ClockReady ? "ClassIsland 已校时（演示）" : "时间未确认（演示）";
        HomeState.Text = State.Active ? State.Phase == "Paused" ? "录制已暂停" : "当前正在录课" : "当前没有录制";
        HomeSummary.Text = State.Active ? $"{State.ActiveSubject} · {State.Started:HH:mm} 开始 · {State.Audio}" : _settings;
        HomePrimary.Content = State.Active ? "管理当前录制" : "立即录课";
        var next = State.NextPlan();
        HomeNext.Text = State.ClockReady ? next is null ? "这一天没有剩余安排" : $"{next.Lesson.Subject} · {next.Start:HH:mm} 开始" : "学校时间尚未确认";
        HomeAuto.Text = State.AutomaticEnabled ? "自动录课已开启 · 使用已保存安排" : "自动录课未开启；有安排不会自动开始";
        IdleSummary.Visibility = Show(!State.Active); LiveSummary.Visibility = Show(State.Active);
        ManualHeading.Text = State.Active ? State.Owner == "Automatic" ? "自动录课进行中" : "手动录课进行中" : State.Phase == "Failed" ? "上次录课未完成" : State.Phase == "Saved" ? "上次录课已保存" : "立即录一节课";
        ManualHint.Text = State.Active ? State.Owner == "Automatic" ? "暂停不会延长自动课时的结束时间。" : "暂停后可继续；结束时点击停止并保存。" : "使用已保存的录制设置；结束后在本地查看文件。";
        InputSummary.Text = LiveSettings.Text = _settings; FolderSummary.Text = "保存到 " + _folder;
        Duration.Text = TimeSpan.FromSeconds(State.Seconds).ToString(@"hh\:mm\:ss");
        ActiveSource.Text = $"{State.ActiveSubject} · {State.Started:HH:mm} 开始" + (State.End is { } end ? $" · {end:HH:mm} 截止" : " · 主动停止并保存");
        AudioStatus.Text = State.Audio;
        AudioPanel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(State.Audio.Contains("等待") || State.Audio.Contains("静音") ? "#FFF3DC" : "#EAF3EC"));
        StartManual.Visibility = Show(!State.Active); Pause.Visibility = StopManual.Visibility = Show(State.Active);
        Pause.Content = State.Phase == "Paused" ? "继续录制" : "暂停";
        ViewLastRecord.Visibility = Show(!State.Active && State.Phase is "Saved" or "Failed");
        OpenSettings.IsEnabled = !State.Active;
        AudioGap.IsEnabled = AudioRecovered.IsEnabled = FailCapture.IsEnabled = State.Active;
        AutoStatus.Text = State.AutomaticEnabled ? State.Active && State.Owner == "Automatic" ? "正在自动录课" : "自动录课已开启" : "自动录课未开启";
        AutoNext.Text = State.Dirty ? "未保存的课程改动还没有生效。" : HomeNext.Text;
        ToggleAutomatic.Content = State.AutomaticEnabled ? "关闭自动录课" : "开启自动录课";
        ToggleAutomatic.IsEnabled = State.AutomaticEnabled || State.ClockReady;
        SkipNext.IsEnabled = State.AutomaticEnabled && (!State.Active || State.Owner == "Manual");
        Advance.Content = State.Active && State.Owner == "Automatic" ? "演示到期结束" : "演示到下一课";
    }
    private static string DescribeCourse(PlannedRecording plan)
    {
        if (plan.Conflict) return "课时有重叠，请检查课表。";
        if (!plan.Selected) return plan.Reason.Contains("单日") ? "仅今天不录；每周规则保持原状。" : plan.Reason.Contains("排除") ? "在默认不录科目中。" : "不在每周安排中。";
        if (plan.Reason.Contains("课前余量已缩短")) return "与上一时段相邻，自动缩短课前余量。";
        if (plan.Reason.Contains("课后余量已缩短")) return "为下一节让出时间，自动缩短课后余量。";
        return plan.Reason.Contains("单日") ? "今天单独安排录制。" : "按每周安排录制。";
    }
    private void ResizeNavigation()
    {
        bool compact = _folded || ActualWidth < 1000;
        bool shortWindow = ActualHeight < 580;
        PageHint.Visibility = ClockLabel.Visibility = PlanFooterHint.Visibility = Show(!shortWindow);
        ContentArea.Margin = shortWindow ? new Thickness(16,10,16,10) : new Thickness(24,20,24,14);
        NavWidth.Width = new GridLength(compact ? 64 : 148);
        foreach (var label in new[] {Brand,HomeLabel,RecordingLabel,NavNote}) label.Visibility = Show(!compact);
    }
    private void HomeClicked(object sender, RoutedEventArgs e) => Navigate("Home");
    private void HomePrimaryClicked(object sender, RoutedEventArgs e) => Navigate("Recording","Manual");
    private void RecordingClicked(object sender, RoutedEventArgs e) => Navigate("Recording",State.Active ? "Manual" : "Plan");
    private void ManualClicked(object sender, RoutedEventArgs e) => Navigate("Recording","Manual");
    private void PlanClicked(object sender, RoutedEventArgs e) => Navigate("Recording","Plan");
    private void RecordsClicked(object sender, RoutedEventArgs e) => Navigate("Recording","Records");
    private void NeedsAttentionClicked(object sender, RoutedEventArgs e) { RecordFilter.SelectedIndex = 1; Navigate("Recording","Records"); }
    private void FoldClicked(object sender, RoutedEventArgs e) { _folded = !_folded; ResizeNavigation(); }
    private void StartClicked(object sender, RoutedEventArgs e) { State.StartManual(); Render(); Tell("演示录制已开始；没有启动真实采集。"); }
    private void PauseClicked(object sender, RoutedEventArgs e) { State.Phase = State.Phase == "Paused" ? "Recording" : "Paused"; RenderStatus(); }
    private void StopClicked(object sender, RoutedEventArgs e) { State.Stop(); Render(); Tell("演示录制已结束，记录已生成。"); }
    private void AudioGapClicked(object sender, RoutedEventArgs e) { State.Audio = "音频写入变慢，正在等待恢复；宽限至多 10 秒（演示）。"; RenderStatus(); }
    private void AudioRecoveredClicked(object sender, RoutedEventArgs e) { State.Audio = "音频卡顿已恢复，缺口补为静音；累计卡顿只提示质量下降（演示）。"; RenderStatus(); }
    private void FailClicked(object sender, RoutedEventArgs e) { State.Stop("未完成","音频连续无法写入，当前片段已保留（演示）。"); Render(); Tell("演示中断已记录，可以到录制记录查看保留位置。",true); }
    private void CourseClicked(object sender, RoutedEventArgs e) { State.ToggleCourse((int)((Button)sender).Tag); Render(); Tell("当天改动已进入草稿；保存后才用于自动安排。"); }
    private void SavePlanClicked(object sender, RoutedEventArgs e) { State.Save(); Render(); Tell("演示安排已保存。自动录课总开关保持原状态。"); }
    private void DiscardPlanClicked(object sender, RoutedEventArgs e) { State.Discard(); Render(); Tell("已恢复上次保存的演示安排。"); }
    private void ToggleAutomaticClicked(object sender, RoutedEventArgs e)
    {
        if (!State.AutomaticEnabled && !State.ClockReady) { Tell("先确认学校时间。",true); return; }
        State.AutomaticEnabled = !State.AutomaticEnabled;
        if (!State.AutomaticEnabled && State.Active && State.Owner == "Automatic") State.Stop();
        Render(); Tell(State.AutomaticEnabled ? "自动录课已开启（演示）；实际执行使用已保存安排。" : "自动录课已关闭（演示），手动录制不受影响。");
    }
    private void SkipNextClicked(object sender, RoutedEventArgs e) { State.SkipNext(); Render(); Tell("下一项演示任务已跳过，没有结束当前手动录制。"); }
    private void AdvanceClicked(object sender, RoutedEventArgs e) { string? result = State.Advance(); Render(); Tell(result!); }
    private void DateChanged(object sender, SelectionChangedEventArgs e) { if (!_ready || PlanDate.SelectedDate is not { } date) return; State.ViewDate = DateOnly.FromDateTime(date); Render(); }
    private void PreviousClicked(object sender, RoutedEventArgs e) => PlanDate.SelectedDate = State.ViewDate.AddDays(-1).ToDateTime(TimeOnly.MinValue);
    private void NextClicked(object sender, RoutedEventArgs e) => PlanDate.SelectedDate = State.ViewDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
    private void RecordFilterChanged(object sender, SelectionChangedEventArgs e) { if (_ready) { State.SelectedRecord = null; Render(); } }
    private void RecordDetailsClicked(object sender, RoutedEventArgs e) { State.SelectedRecord = (DemoRecording)((Button)sender).Tag; Render(); }
    private void SettingsClicked(object sender, RoutedEventArgs e) { if (!State.Active) Navigate("Settings"); }
    private void BackSettingsClicked(object sender, RoutedEventArgs e) => Navigate("Recording","Manual");
    private void SaveSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Folder.Text)) { Tell("填写演示保存位置。",true); return; }
        string sound = Mic.IsChecked == true && Speaker.IsChecked == true ? "麦克风＋系统声音" : Mic.IsChecked == true ? "麦克风" : Speaker.IsChecked == true ? "系统声音" : "无音频";
        _settings = $"{(DisplayChoice.SelectedIndex == 0 ? "教室大屏" : "备用屏幕")} · {(Quality.SelectedIndex == 0 ? "1080p" : "720p")} · {(Fps.SelectedIndex == 0 ? "8 fps" : "15 fps")} · {sound}";
        _folder = Folder.Text.Trim(); Navigate("Recording","Manual"); Tell("录制设置已保存到原型内存，关闭后丢弃。");
    }
    private void WeeklyClicked(object sender, RoutedEventArgs e)
    {
        var rule = State.Draft.Recurring[0];
        foreach (CheckBox day in Weekdays.Children) day.IsChecked = (rule.Filter.Weekdays & (1 << (int)day.Tag)) != 0;
        Before.Text = rule.Filter.BeforeMinutes.ToString(CultureInfo.InvariantCulture); After.Text = rule.Filter.AfterMinutes.ToString(CultureInfo.InvariantCulture);
        DefaultExclusions.IsChecked = rule.UseDefaultExclusions; Navigate("Weekly");
    }
    private void ApplyWeeklyClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Before.Text,out int before) || !int.TryParse(After.Text,out int after) || before is < 0 or > 5 || after is < 0 or > 5)
        { Tell("课前、课后余量分别填写 0–5 分钟。",true); return; }
        int weekdays = Weekdays.Children.OfType<CheckBox>().Where(d => d.IsChecked == true).Sum(d => 1 << (int)d.Tag);
        var rule = State.Draft.Recurring[0];
        State.Draft = State.Draft with { Recurring = [rule with { Filter = rule.Filter with { BeforeMinutes = before, AfterMinutes = after, Weekdays = weekdays }, UseDefaultExclusions = DefaultExclusions.IsChecked == true }] };
        Navigate("Recording","Plan"); Tell("每周改动已应用到草稿；请保存安排。");
    }
}
