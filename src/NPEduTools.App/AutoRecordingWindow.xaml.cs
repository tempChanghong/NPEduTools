using System.IO;
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
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private DaySchedule? _source;
    private PlannedRecording[] _plans = [];
    private DateTimeOffset _nextRead;
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
            _preview.Tick(_source, _plans, DateTimeOffset.Now);
            bool changed = !ReferenceEquals(_saved, _preview.State);
            Save(); Render(changed);
            if ((IsVisible || _preview.Enabled) && DateTimeOffset.Now >= _nextRead) await ReadAsync();
        };
        Loaded += async (_, _) => { _timer.Start(); await ReadAsync(); };
        Closing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
    }
    public void Shutdown()
    {
        _shutdown = true; _timer.Stop(); _lifetime.Cancel();
        _preview.SetEnabled(false, DateTimeOffset.Now); Save(); Close();
    }
    private async Task ReadAsync()
    {
        if (_querying || _shutdown) return;
        _querying = true;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(13));
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.schedule", 10000), deadline.Token);
            if (_shutdown) return;
            if (response.Outcome != "Succeeded" || response.Schedule is null) throw new InvalidDataException(response.Message);
            _source = response.Schedule;
            _plans = RecordingPlanner.Build(_source, _preview.State.Rules);
            SourceStatus.Text = $"{_source.Date:yyyy-MM-dd} · {_source.Name} · {_source.Lessons.Length} 节 · {_source.ClockMessage}";
            PopulateSubjects();
            _preview.Tick(_source, _plans, DateTimeOffset.Now); Save(); Render();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or TimeoutException)
        {
            if (_shutdown) return;
            _source = null; _plans = [];
            SourceStatus.Text = error is OperationCanceledException ? "读取日程超时，稍后自动重试" : "日程暂不可用：" + error.Message;
            _preview.Tick(null, [], DateTimeOffset.Now); Save(); Render();
        }
        finally { _querying = false; _nextRead = DateTimeOffset.Now.AddSeconds(5); }
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
            _writable = false; _preview.SetEnabled(false, DateTimeOffset.Now);
            ApplyRules.IsEnabled = TogglePreview.IsEnabled = SkipDay.IsEnabled = false;
            ErrorText.Text = "记录无法保存，预演已停止。请检查文件夹权限及剩余空间。";
        }
    }
    private void Render(bool rows = true)
    {
        PreviewStatus.Text = _preview.Status;
        TogglePreview.Content = _preview.Enabled ? "停止预演" : "启动预演";
        SkipDay.Content = _preview.State.SkipDate == DateOnly.FromDateTime(DateTime.Today) ? "恢复今日预演" : "今天不再预演";
        if (!rows) return;
        string? selected = (Plans.SelectedItem as PlanRow)?.Key;
        var items = _plans.Select(p => new PlanRow(p.Key, p.Lesson.Number, p.Lesson.Subject,
            $"{p.Lesson.Start:HH:mm}–{p.Lesson.End:HH:mm}", $"{p.Start:HH:mm}–{p.End:HH:mm}",
            _preview.State.Active?.Plan.Key == p.Key ? "模拟录制中" : _source is not null && _preview.IsMarked(_source.ProfileId, p.Lesson) ? "已预演 / 已跳过" : p.Reason)).ToArray();
        Plans.ItemsSource = items;
        if (selected is not null) Plans.SelectedItem = items.FirstOrDefault(i => i.Key == selected);
        Events.ItemsSource = _preview.State.Events.Reverse().Select(e => $"{e.At:MM-dd HH:mm:ss}  {e.Action} · {e.Subject}｜{e.Message}").ToArray();
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
            _preview.Tick(_source, _plans, DateTimeOffset.Now); ErrorText.Text = "规则已应用并保存。"; Save(); Render();
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidDataException)
        { ErrorText.Text = "无法应用规则：" + error.Message; }
    }
    private void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable) return;
        _preview.SetEnabled(!_preview.Enabled, DateTimeOffset.Now);
        _preview.Tick(_source, _plans, DateTimeOffset.Now); Save(); Render();
    }
    private void SkipClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable || _source is null || Plans.SelectedItem is not PlanRow row) return;
        var plan = _plans.First(p => p.Key == row.Key);
        _preview.Skip(_source.ProfileId, plan.Lesson, DateTimeOffset.Now);
        _preview.Tick(_source, _plans, DateTimeOffset.Now); Save(); Render();
    }
    private void SkipDayClicked(object sender, RoutedEventArgs e)
    {
        if (!_writable) return;
        if (_preview.State.SkipDate == DateOnly.FromDateTime(DateTime.Today)) _preview.ResumeToday();
        else _preview.SkipToday(DateTimeOffset.Now);
        _preview.Tick(_source, _plans, DateTimeOffset.Now); Save(); Render();
    }
    private void SubjectsChanged(object sender, RoutedEventArgs e) { if (Subjects is not null) Subjects.IsEnabled = AllSubjects.IsChecked != true; }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await ReadAsync();
    private void HideClicked(object sender, RoutedEventArgs e) => Hide();
}
