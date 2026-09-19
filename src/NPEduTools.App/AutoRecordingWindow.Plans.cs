using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.App;

public partial class AutoRecordingWindow
{
    private RecordingPlanBook _book = RecordingPlanBook.Create();
    private RecordingPlanBookStore _bookStore = null!;
    private RecordingPlanBook? _savedBook;
    private DateOnly? _today, _viewDate;
    private DayForecast? _forecast;
    private Guid _bridgeInstance;
    private Guid? _editingDated;
    private bool _ready, _setting, _followToday = true, _calendarReading;
    private int _calendarGeneration;
    private DaySchedule? ViewSource => _viewDate == _today ? _source : _forecast?.Schedule;
    private PlannedRecording[] ViewPlans => _viewDate is { } date ? CalendarRecordingPlanner.Build(_book, date, ViewSource) : [];
    private RecurringRecordingRule? SelectedRule => RuleList.SelectedItem as RecurringRecordingRule;

    private void InitializePlanBook(bool hadLegacy)
    {
        _bookStore = new(StartupPreferencesStore.PathFor(_pipe).Replace(".startup.json", ".recording-plans.json", StringComparison.Ordinal));
        try
        {
            _book = _bookStore.Read(hadLegacy && _writable ? _preview.State : null);
            if (_bookStore.Migrated)
            {
                string legacy = StartupPreferencesStore.PathFor(_pipe).Replace(".startup.json", ".recording-preview.json", StringComparison.Ordinal);
                if (!File.Exists(legacy + ".pre-plan-book.bak")) File.Copy(legacy, legacy + ".pre-plan-book.bak", false);
                else if (new FileInfo(legacy + ".pre-plan-book.bak").Length > 512 * 1024 || File.ReadAllText(legacy + ".pre-plan-book.bak") != File.ReadAllText(legacy))
                    throw new InvalidDataException("已有不同的旧规则备份，请先核对。");
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { _writable = false; ErrorText.Text = "录课计划无法读取，原文件已保留；修改及预演已停用。"; }
        if (_writable && _bookStore.Migrated) ErrorText.Text = "旧规则已备份并作为停用草稿迁入。新规则默认排除 17 项科目，旧草稿保留原筛选，请核对后启用。";
        ExcludedNames.Text = string.Join(Environment.NewLine, _book.ExcludedNames);
        PauseThrough.SelectedDate = _book.PauseThrough?.ToDateTime(TimeOnly.MinValue);
        _ready = true; RefreshRuleList();
    }
    private void RefreshRuleList(Guid? selected = null)
    {
        RuleList.ItemsSource = _book.Recurring;
        RuleList.SelectedItem = _book.Recurring.FirstOrDefault(r => r.Id == selected) ?? _book.Recurring.FirstOrDefault();
    }
    private void RuleSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SelectedRule is not { } rule) return;
        _setting = true;
        RuleName.Text = rule.Name; RuleEnabled.IsChecked = rule.Enabled; RuleKind.SelectedIndex = rule.IsFixed ? 1 : 0;
        RuleFrom.SelectedDate = rule.From?.ToDateTime(TimeOnly.MinValue); RuleUntil.SelectedDate = rule.Until?.ToDateTime(TimeOnly.MinValue);
        RuleStart.Text = rule.FixedStart?.ToString("HH:mm") ?? "10:00"; RuleEnd.Text = rule.FixedEnd?.ToString("HH:mm") ?? "10:40";
        UseExclusions.IsChecked = rule.UseDefaultExclusions;
        LeadMinutes.SelectedItem = rule.Filter.BeforeMinutes; TailMinutes.SelectedItem = rule.Filter.AfterMinutes;
        foreach (CheckBox box in Weekdays.Children) box.IsChecked = (rule.Filter.Weekdays & (1 << int.Parse((string)box.Tag))) != 0;
        Numbers.Text = string.Join(",", rule.Filter.LessonNumbers ?? []); AllSubjects.IsChecked = rule.Filter.AllSubjects;
        Subjects.ItemsSource = null; PopulateSubjects(); _setting = false;
        RuleKindChanged(this, e);
    }
    private void RuleKindChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        bool fixedTime = RuleKind.SelectedIndex == 1;
        FixedRuleFields.Visibility = fixedTime ? Visibility.Visible : Visibility.Collapsed;
        LeadMinutes.IsEnabled = TailMinutes.IsEnabled = AllSubjects.IsEnabled = Numbers.IsEnabled = UseExclusions.IsEnabled = !fixedTime;
        Subjects.IsEnabled = !fixedTime && AllSubjects.IsChecked != true;
    }
    private static TimeOnly ParseTime(string text) => TimeOnly.TryParseExact(text.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
        ? time : throw new InvalidDataException("时间请填写 HH:mm，例如 09:58。");
    private bool ChangeBook(Func<RecordingPlanBook, RecordingPlanBook> change, string message)
    {
        if (!_writable) return false;
        try
        {
            var next = change(_book);
            if (CalendarRecordingPlanner.Validate(next) is { } error) throw new InvalidDataException(error);
            // Commit configuration before exposing it to the rehearsal engine.
            _bookStore.Save(next); _book = next; _savedBook = next;
            RebuildPlans(); _preview.Tick(_source, _plans, _clock.Read(Elapsed), Elapsed); Save(); Render(); ErrorText.Text = message;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or OverflowException)
        { ErrorText.Text = "未应用修改：" + error.Message; return false; }
    }
    private void NewRuleClicked(object sender, RoutedEventArgs e)
    {
        var rule = new RecurringRecordingRule(Guid.NewGuid(), "新周期规则", false, new(), ViewSource?.ProfileId);
        if (ChangeBook(b => b with { Recurring = b.Recurring.Append(rule).ToArray() }, "新规则默认停用；配置并勾选启用后保存。")) RefreshRuleList(rule.Id);
    }
    private void DeleteRuleClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { } rule) return;
        if (ChangeBook(b => b with { Recurring = b.Recurring.Where(r => r.Id != rule.Id).ToArray() }, "周期规则已删除。")) RefreshRuleList();
    }
    private void SaveRecurringRule()
    {
        if (SelectedRule is not { } old) { ErrorText.Text = "请先新建一条规则。"; return; }
        bool applied = ChangeBook(b =>
        {
            bool fixedTime = RuleKind.SelectedIndex == 1;
            Guid? profile = old.ProfileId ?? ViewSource?.ProfileId;
            if (!fixedTime && old.ProfileId is not null && ViewSource is not null && old.ProfileId != ViewSource.ProfileId)
                throw new InvalidDataException("当前显示的是另一个档案，请切回原档案再修改这条课程规则，或新建规则。");
            var filter = new RecordingRules((int)LeadMinutes.SelectedItem, (int)TailMinutes.SelectedItem,
                Weekdays.Children.Cast<CheckBox>().Where(c => c.IsChecked == true).Sum(c => 1 << int.Parse((string)c.Tag)),
                AllSubjects.IsChecked == true, Subjects.SelectedItems.Cast<SubjectOption>().Select(s => s.Id).ToArray(),
                Numbers.Text.Split([',', '，', ' ', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().ToArray());
            if (!fixedTime && !filter.AllSubjects && profile is null && RuleEnabled.IsChecked == true)
                throw new InvalidDataException("科目筛选需要先连接学校课表；可以先保存停用草稿。");
            var rule = old with { Name = RuleName.Text.Trim(), Enabled = RuleEnabled.IsChecked == true, Filter = filter,
                ProfileId = fixedTime ? null : profile, From = RuleFrom.SelectedDate is { } from ? DateOnly.FromDateTime(from) : null,
                Until = RuleUntil.SelectedDate is { } until ? DateOnly.FromDateTime(until) : null,
                FixedStart = fixedTime ? ParseTime(RuleStart.Text) : null, FixedEnd = fixedTime ? ParseTime(RuleEnd.Text) : null,
                UseDefaultExclusions = UseExclusions.IsChecked == true };
            return b with { Recurring = b.Recurring.Select(r => r.Id == rule.Id ? rule : r).ToArray() };
        }, "规则已应用并保存。固定时段不使用科目排除；课程规则按档案匹配。");
        if (applied) RefreshRuleList(old.Id);
    }
    private void RebuildPlans()
    {
        _plans = _today is { } date ? CalendarRecordingPlanner.Build(_book, date, _source?.Date == date ? _source : null) : [];
    }
    private void ObserveCalendar(SchoolClockFrame frame)
    {
        var reading = _clock.Read(Elapsed);
        if (!reading.Fresh || reading.Now is not { } now) return;
        var date = DateOnly.FromDateTime(now.Date);
        if (_today != date || _bridgeInstance != frame.BridgeInstanceId)
        {
            _today = date; _bridgeInstance = frame.BridgeInstanceId; _forecast = null; _calendarGeneration++;
            if (_followToday || _viewDate is null || _viewDate < date || _viewDate > date.AddDays(30)) SetViewDate(date, true);
        }
        if (_viewDate is null) SetViewDate(date, true);
        PlanDate.DisplayDateStart = date.ToDateTime(TimeOnly.MinValue);
        PlanDate.DisplayDateEnd = date.AddDays(30).ToDateTime(TimeOnly.MinValue);
        if (_source is not null && _writable) _book = CalendarRecordingPlanner.BindDefaultSubjects(_book, _source);
        RebuildPlans();
        if (_viewDate != _today && _forecast is null && !_calendarReading && reading.CanStart) _ = ReadCalendarAsync();
    }
    private void SetViewDate(DateOnly date, bool follow)
    {
        _followToday = follow; _viewDate = date; _forecast = null; _calendarGeneration++; _editingDated = null;
        _setting = true; PlanDate.SelectedDate = date.ToDateTime(TimeOnly.MinValue); PlanDate.DisplayDate = date.ToDateTime(TimeOnly.MinValue); _setting = false;
        PopulateSubjects(); Render();
    }
    private async void PlanDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _setting || PlanDate.SelectedDate is not { } date) return;
        SetViewDate(DateOnly.FromDateTime(date), DateOnly.FromDateTime(date) == _today);
        await ReadCalendarAsync(true);
    }
    private async void RelativeDayClicked(object sender, RoutedEventArgs e)
    {
        if (_clock.Read(Elapsed) is not { CanStart: true, Now: { } now }) return;
        int offset = int.Parse((string)((Button)sender).Tag);
        SetViewDate(DateOnly.FromDateTime(now.Date).AddDays(offset), offset == 0); await ReadCalendarAsync(true);
    }
    private TimeSpan _calendarRetry;
    private async Task ReadCalendarAsync(bool force = false)
    {
        if (_calendarReading || _shutdown || _viewDate is not { } date || date == _today || !_clock.Read(Elapsed).CanStart || !force && Elapsed < _calendarRetry) return;
        _calendarReading = true; int generation = _calendarGeneration;
        try
        {
            DateStatus.Text = $"{date:yyyy-MM-dd} · 正在读取预计课表…";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var response = await HostClient.RequestAsync(_pipe, new HostRequest(Protocol.Version, Guid.NewGuid(), "classisland.day-plan", 8000, SchoolDate: date), deadline.Token);
            if (_shutdown || generation != _calendarGeneration) return;
            var forecast = response.Forecast;
            if (response.Outcome != "Succeeded" || forecast is null) throw new InvalidDataException(response.Message);
            if (!forecast.Forecast || forecast.BridgeInstanceId != _bridgeInstance || forecast.SchoolDate != _today || forecast.RequestedDate != date)
                throw new InvalidDataException("学校日期或连接已变化，请重新读取。");
            _forecast = forecast;
            if (_writable) _book = CalendarRecordingPlanner.BindDefaultSubjects(_book, forecast.Schedule);
            PopulateSubjects(); Save(); Render();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            if (_shutdown || generation != _calendarGeneration) return;
            _forecast = null; Render(); DateStatus.Text = $"{date:yyyy-MM-dd} · 预计课表暂不可用：{error.Message}";
        }
        finally { _calendarReading = false; _calendarRetry = Elapsed + TimeSpan.FromSeconds(10); }
    }
    private void OverrideClicked(object sender, RoutedEventArgs e)
    {
        if (_viewDate is not { } date || ViewSource is not { } source || Plans.SelectedItem is not PlanRow row) return;
        var plan = ViewPlans.First(p => p.Key == row.Key); if (plan.Fixed) { ErrorText.Text = "固定时段请在下方编辑或删除。"; return; }
        string mode = (string)((Button)sender).Tag;
        ChangeBook(b =>
        {
            var remaining = b.Overrides.Where(o => !(o.Date == date && o.ProfileId == source.ProfileId && o.Number == plan.Lesson.Number));
            if (mode == "Inherit") return b with { Overrides = remaining.ToArray() };
            if (!plan.Lesson.Enabled) throw new InvalidDataException("课程已停用或不存在，只能恢复继承以清除旧安排。");
            return b with { Overrides = remaining.Append(new(Guid.NewGuid(), source.ProfileId, date, plan.Lesson.Number,
                plan.Lesson.Start, plan.Lesson.End, plan.Lesson.Subject, mode, (int)LeadMinutes.SelectedItem, (int)TailMinutes.SelectedItem)).ToArray() };
        }, "单日安排已保存；到当天会重新核对生效课表。");
    }
    private void OnlyExplicitClicked(object sender, RoutedEventArgs e)
    {
        if (_viewDate is not { } date || ViewSource is not { ProfileId: var profile } || profile == Guid.Empty) return;
        ChangeBook(b => b with { Days = b.Days.Where(d => d.Date != date || d.ProfileId != profile)
            .Append(new(date, profile, OnlyExplicit.IsChecked == true)).ToArray() }, "当天周期规则继承方式已保存。");
    }
    private void PlanSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || Plans.SelectedItem is not PlanRow row) return;
        var item = _book.Dated.FirstOrDefault(d => row.Key == $"fixed/{d.Id:N}/{d.Date:yyyy-MM-dd}");
        _editingDated = item?.Id;
        if (item is not null) { DatedName.Text = item.Name; DatedStart.Text = item.Start.ToString("HH:mm"); DatedEnd.Text = item.End.ToString("HH:mm"); }
    }
    private void NewDatedClicked(object sender, RoutedEventArgs e) { _editingDated = null; Plans.SelectedItem = null; DatedName.Text = "单次录课"; }
    private void SaveDatedClicked(object sender, RoutedEventArgs e)
    {
        if (_viewDate is not { } date) { ErrorText.Text = "请先选择一个明确日期。"; return; }
        Guid id = _editingDated ?? Guid.NewGuid();
        if (ChangeBook(b => b with { Dated = b.Dated.Where(d => d.Id != id).Append(new(id, DatedName.Text.Trim(), date, ParseTime(DatedStart.Text), ParseTime(DatedEnd.Text))).ToArray() }, "指定日期的固定时段已保存。")) _editingDated = id;
    }
    private void DeleteDatedClicked(object sender, RoutedEventArgs e)
    {
        if (_editingDated is not { } id) return;
        ChangeBook(b => b with { Dated = b.Dated.Where(d => d.Id != id).ToArray() }, "单日固定时段已删除。"); _editingDated = null;
    }
    private void ExclusionsClicked(object sender, RoutedEventArgs e) => ChangeBook(b => b with {
        ExcludedNames = ExcludedNames.Text.Split(['\r', '\n', ',', '，', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CalendarRecordingPlanner.NormalizeSubject).Distinct().ToArray(), Subjects = b.Subjects.Where(s => s.Explicit).ToArray() }, "默认排除已保存；明确设置的科目例外继续保留。");
    private void SetSubject(bool? allow)
    {
        if (ViewSource is not { } source || Plans.SelectedItem is not PlanRow row) return;
        var plan = ViewPlans.First(p => p.Key == row.Key); if (plan.Fixed || plan.Lesson.SubjectId == Guid.Empty) return;
        ChangeBook(b => b with { Subjects = (allow is null ? b.Subjects.Where(s => s.ProfileId != source.ProfileId || s.SubjectId != plan.Lesson.SubjectId) :
            b.Subjects.Where(s => s.ProfileId != source.ProfileId || s.SubjectId != plan.Lesson.SubjectId).Append(new(source.ProfileId, plan.Lesson.SubjectId, allow.Value))).ToArray() }, "科目设置已保存，仅作用于此档案；单日明确安排仍优先。");
    }
    private void AllowSubjectClicked(object sender, RoutedEventArgs e) => SetSubject(true);
    private void ExcludeSubjectClicked(object sender, RoutedEventArgs e) => SetSubject(false);
    private void ResetSubjectClicked(object sender, RoutedEventArgs e) => SetSubject(null);
    private void PauseThroughClicked(object sender, RoutedEventArgs e) => ChangeBook(b => b with {
        PauseThrough = PauseThrough.SelectedDate is { } date ? DateOnly.FromDateTime(date) : null }, "暂停日期已保存（包含该日），留空表示恢复。");
}
