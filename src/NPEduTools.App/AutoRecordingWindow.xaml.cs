using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class AutoRecordingWindow : Window
{
    private sealed record SubjectOption(Guid Id, string Name);
    private sealed record PlanRow(string Key, int Number, string Subject, string ClassTime, string CaptureTime, string Status);
    private readonly string _pipe;
    private readonly RecordingPreviewStore _store;
    private readonly RecordingPreview _preview;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource _lifetime = new();
    private DaySchedule? _source;
    private PlannedRecording[] _plans = [];
    private readonly SchoolClockTracker _clock = new();
    private static TimeSpan Elapsed => Stopwatch.GetElapsedTime(0);
    private DateTimeOffset? EventTime => _clock.Read(Elapsed) is { Fresh: true } reading ? reading.Now : null;
    private bool _querying, _shutdown, _writable = true;
    private PreviewState? _saved;
    internal AutoRecordingWindow(string pipe)
    {
        _pipe = pipe;
        _store = new(StartupPreferencesStore.PathFor(pipe).Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal));
        InitializeComponent();
        try { _preview = new(_store.Read()); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _preview = new(); _writable = false; ErrorText.Text = "配置或预演记录无法读取，原文件已保留；仍可查看今日课表。"; }
        if (_store.Migrated) ErrorText.Text = "已保留原系统时间预演记录的备份，并迁移规则；请按学校日期重新确认预演。";
        var rules = _preview.State.Rules;
        LeadMinutes.ItemsSource = TailMinutes.ItemsSource = Enumerable.Range(0, 6).ToArray();
        LeadMinutes.SelectedItem = rules.BeforeMinutes; TailMinutes.SelectedItem = rules.AfterMinutes;
        foreach (CheckBox day in Weekdays.Children) day.IsChecked = (rules.Weekdays & (1 << int.Parse((string)day.Tag))) != 0;
        Numbers.Text = string.Join(",", rules.LessonNumbers ?? []);
        AllSubjects.IsChecked = rules.AllSubjects;
        Subjects.IsEnabled = !rules.AllSubjects;
        ApplyRules.IsEnabled = TogglePreview.IsEnabled = SkipDay.IsEnabled = _writable;
        PopulateSubjects(); Save(); Render();
        _timer.Tick += async (_, _) =>
        {
            if (_shutdown) return;
            _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed);
            bool changed = !ReferenceEquals(_saved, _preview.State);
            Save(); Render(changed);
            if (IsVisible || _preview.Enabled) await ReadAsync();
        };
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); };
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerModeChanged;
        Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
    }
    public void Shutdown()
    {
        _shutdown = true; _timer.Stop(); _lifetime.Cancel();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerModeChanged;
        _preview.SetEnabled(false, EventTime); Save(); Close();
    }
    private void PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode is not (Microsoft.Win32.PowerModes.Suspend or Microsoft.Win32.PowerModes.Resume)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shutdown) return;
            _clock.Unavailable("电脑经历休眠或唤醒，请重新确认学校时间", Elapsed);
            _preview.SetEnabled(false, null); Save(); Render();
            ErrorText.Text = "休眠或唤醒后已停止预演；请核对学校时间后重新启动。";
        });
    }
    private async Task ReadAsync()
    {
        if (_querying || _shutdown) return;
        _querying = true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var start = Elapsed;
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.school-clock", 2000), deadline.Token);
            if (_shutdown) return;
            if (response.Outcome != "Succeeded" || response.SchoolClock is null) throw new InvalidDataException(response.Message);
            _clock.Accept(response.SchoolClock, Elapsed, (Elapsed - start).TotalMilliseconds);
            var source = _clock.Schedule;
            bool changed = source?.Revision != _source?.Revision || source?.Enabled != _source?.Enabled ||
                source?.Date != _source?.Date || source?.ProfileId != _source?.ProfileId;
            _source = source;
            if (changed) _plans = _source is null ? [] : RecordingPlanner.Build(_source, _preview.State.Rules);
            SourceStatus.Text = _source is null ? "日程暂不可用，等待桥接插件提供学校课表" :
                $"学校日期 {_source.Date:yyyy-MM-dd} · {_source.Name} · {_source.Lessons.Length} 节 · {_source.ClockMessage}";
            if (changed) PopulateSubjects();
            _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed);
            changed |= !ReferenceEquals(_saved, _preview.State);
            Save(); Render(changed);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or TimeoutException)
        {
            if (_shutdown) return;
            _source = null; _plans = [];
            _clock.Unavailable("学校时间连接中断", Elapsed);
            SourceStatus.Text = error is OperationCanceledException ? "读取日程超时，稍后自动重试" : "日程暂不可用：" + error.Message;
            _preview.Tick(null, [], _clock.Read(Elapsed), Elapsed); Save(); Render();
        }
        finally { _querying = false; }
    }
    private void PopulateSubjects()
    {
        var selected = Subjects.Items.Count == 0 ? _preview.State.Rules.SubjectIds ?? [] : Subjects.SelectedItems.Cast<SubjectOption>().Select(s => s.Id).ToArray();
        var options = (_source?.Lessons.Select(l => new SubjectOption(l.SubjectId, l.Subject)) ?? [])
            .Where(s => s.Id != Guid.Empty).DistinctBy(s => s.Id).ToList();
        foreach (var id in selected.Where(id => options.All(s => s.Id != id))) options.Add(new(id, "当前课表未包含：" + id.ToString("N")[..8]));
        Subjects.ItemsSource = options;
        foreach (var option in options.Where(o => selected.Contains(o.Id))) Subjects.SelectedItems.Add(option);
    }
    private void Save()
    {
        if (!_writable || ReferenceEquals(_saved, _preview.State)) return;
        try { _store.Save(_preview.State); _saved = _preview.State; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _writable = false; _preview.SetEnabled(false, EventTime);
            ApplyRules.IsEnabled = TogglePreview.IsEnabled = SkipDay.IsEnabled = false;
            ErrorText.Text = "记录无法保存，预演已停止。请检查文件夹权限及剩余空间。";
        }
    }
    private void Render(bool rows = true)
    {
        PreviewStatus.Text = _preview.Status;
        var clock = _clock.Read(Elapsed);
        ClockStatus.Text = clock.Now is { } now ?
            $"ClassIsland 时间：{now:yyyy-MM-dd HH:mm:ss}（{(clock.Fresh ? "学校时间样本" : "最后收到，已失效")}） · {clock.Message}" : clock.Message;
        TogglePreview.Content = _preview.Enabled ? "停止预演" : "启动预演";
        SkipDay.IsEnabled = _writable && clock.CanStart;
        SkipDay.Content = clock.Now is { } date && _preview.State.SkipDate == DateOnly.FromDateTime(date.Date) ? "恢复今日预演" : "今天不再预演";
        if (!rows) return;
        string? selected = (Plans.SelectedItem as PlanRow)?.Key;
        var items = _plans.Select(p => new PlanRow(p.Key, p.Lesson.Number, p.Lesson.Subject,
            $"{p.Lesson.Start:HH:mm}–{p.Lesson.End:HH:mm}", $"{p.Start:HH:mm}–{p.End:HH:mm}",
            _preview.State.Active?.Plan.Key == p.Key ? "模拟录制中" : _source is not null && _preview.IsMarked(_source.ProfileId, p.Lesson) ? "已预演 / 已跳过" : p.Reason)).ToArray();
        Plans.ItemsSource = items;
        if (selected is not null) Plans.SelectedItem = items.FirstOrDefault(i => i.Key == selected);
        Events.ItemsSource = _preview.State.Events.Reverse().Select(e => $"{(e.At is { } at ? at.ToString("MM-dd HH:mm:ss") + " 学校时间" : "学校时间未知")}  {e.Action} · {e.Subject}｜{e.Message}").ToArray();
    }
    private void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable) return;
        try
        {
            int[] numbers = Numbers.Text.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().ToArray();
            int weekdays = Weekdays.Children.Cast<CheckBox>().Where(c => c.IsChecked == true).Sum(c => 1 << int.Parse((string)c.Tag));
            _preview.Configure(new((int)LeadMinutes.SelectedItem, (int)TailMinutes.SelectedItem, weekdays, AllSubjects.IsChecked == true,
                Subjects.SelectedItems.Cast<SubjectOption>().Select(s => s.Id).ToArray(), numbers));
            _plans = _source is null ? [] : RecordingPlanner.Build(_source, _preview.State.Rules);
            _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed); ErrorText.Text = "规则已应用并保存。"; Save(); Render();
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException)
        { ErrorText.Text = "无法应用规则：" + error.Message; }
    }
    private void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable) return;
        if (!_preview.Enabled) _clock.ConfirmDate();
        _preview.SetEnabled(!_preview.Enabled, EventTime);
        _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed); Save(); Render();
    }
    private void SkipClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable || _source is null || Plans.SelectedItem is not PlanRow row) return;
        var plan = _plans.First(p => p.Key == row.Key);
        _preview.Skip(_source.ProfileId, plan.Lesson, EventTime);
        _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed); Save(); Render();
    }
    private void SkipDayClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable || _clock.Read(Elapsed) is not { CanStart: true, Now: { } now }) return;
        if (_preview.State.SkipDate == DateOnly.FromDateTime(now.Date)) _preview.ResumeToday();
        else _preview.SkipToday(now);
        _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed); Save(); Render();
    }
    private void SubjectsChanged(object sender, RoutedEventArgs e) { if (Subjects is not null) Subjects.IsEnabled = AllSubjects.IsChecked != true; }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private void HideClicked(object sender, RoutedEventArgs e) => Hide();
}
