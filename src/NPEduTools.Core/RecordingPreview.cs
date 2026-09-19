using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record PreviewEvent(DateTimeOffset? At, string Action, string Subject, string Message);
public sealed record PreviewMark(Guid ProfileId, DateTimeOffset Start, DateTimeOffset End, string Reason, string? Key = null);
public sealed record PreviewSession(Guid ProfileId, PlannedRecording Plan, DateTimeOffset StartedAt);
public sealed record PreviewState(int Version, RecordingRules Rules, PreviewEvent[] Events, PreviewMark[] Marks,
    DateOnly? SkipDate = null, PreviewSession? Active = null);

/// <summary>Pure rehearsal state machine. Deliberately has no recorder or process-launch dependency.</summary>
public sealed class RecordingPreview
{
    public PreviewState State { get; private set; }
    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "预演未启动；实际录制由下方自动录制开关控制";
    private TimeSpan? _hardEnd;

    public RecordingPreview(PreviewState? state = null)
    {
        State = state ?? new(2, new(), [], []);
        if (State.Active is not null) Finish(null, "上次程序退出，预演中断；学校时间未知，本节不会重复模拟开始");
    }
    public void Configure(RecordingRules rules)
    {
        if (RecordingPlanner.Validate(rules) is { } error) throw new InvalidDataException(error);
        State = State with { Rules = rules };
    }
    public void SetEnabled(bool enabled, DateTimeOffset? now)
    {
        Enabled = enabled;
        if (!enabled) Finish(now, "用户停止预演");
        Status = enabled ? "预演已启动，等待新鲜日程" : "预演已停止；实际录制由下方自动录制开关控制";
    }
    public bool IsMarked(Guid profile, LessonSlot lesson) => State.Marks.Any(m => m.ProfileId == profile &&
        m.Key is null && m.Start < lesson.End && lesson.Start < m.End);
    public bool IsMarked(Guid profile, PlannedRecording plan) => plan.Fixed ? State.Marks.Any(m => m.Key == plan.Key) : IsMarked(profile, plan.Lesson);
    public void SkipPlan(Guid profile, PlannedRecording plan, DateTimeOffset? now)
    {
        if (!plan.Fixed) { Skip(profile, plan.Lesson, now); return; }
        if (State.Active?.Plan.Key == plan.Key) Finish(now, "用户结束本时段预演");
        else if (!IsMarked(Guid.Empty, plan))
        { Mark(new(Guid.Empty, plan.Lesson.Start, plan.Lesson.End, "本时段已跳过", plan.Key)); Add(new(now, "跳过", plan.Lesson.Subject, "本时段不再模拟开始")); }
    }
    public void Skip(Guid profile, LessonSlot lesson, DateTimeOffset? now)
    {
        if (State.Active is { } active && active.ProfileId == profile && active.Plan.Lesson.Start < lesson.End && lesson.Start < active.Plan.Lesson.End)
            Finish(now, "用户结束本节预演");
        else if (!IsMarked(profile, lesson))
        {
            Mark(new(profile, lesson.Start, lesson.End, "本节已跳过"));
            Add(new(now, "跳过", lesson.Subject, "本节不再模拟开始"));
        }
    }
    public void SkipToday(DateTimeOffset now)
    {
        State = State with { SkipDate = DateOnly.FromDateTime(now.Date) };
        Finish(now, "今天不再预演");
        Status = "今天已暂停预演";
    }
    public void ResumeToday() => State = State with { SkipDate = null };

    public void Tick(DaySchedule? source, PlannedRecording[] plans, SchoolClockReading clock, TimeSpan elapsed)
    {
        if (!Enabled) return;
        DateTimeOffset? now = clock.Now;
        DateTimeOffset? eventTime = clock.Fresh ? now : null;
        bool fresh = clock.CanStart && now is not null && source is not null &&
            source.Date == DateOnly.FromDateTime(now.Value.Date);
        // An active rehearsal has its own fixed end, even while disconnected or the clock is unverified.
        if (State.Active is { } active)
        {
            if (clock.Fresh && now is not null)
                Tighten(elapsed + Remaining(active.Plan.End, now.Value, clock.AgeMs));
            if (_hardEnd is null || elapsed >= _hardEnd || (clock.Fresh && now >= active.Plan.End))
                Finish(eventTime, "到达计划结束期限（含课后余量）；异常时仍按原剩余时长结束");
            else if (!active.Plan.Fixed && fresh && (!source!.Enabled || source.ProfileId != active.ProfileId)) Finish(eventTime, "生效课表已禁用或档案已切换");
            else if (clock.CanStart && (active.Plan.Fixed || fresh))
            {
                var replacement = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Fixed == active.Plan.Fixed &&
                    (p.Fixed ? p.Key == active.Plan.Key : p.Lesson.Start < active.Plan.Lesson.End && active.Plan.Lesson.Start < p.Lesson.End));
                if (replacement is null) Finish(eventTime, "课表或规则变化，本节已不在计划中");
                else if (replacement.End < active.Plan.End)
                {
                    State = State with { Active = active with { Plan = active.Plan with { End = replacement.End } } };
                    Tighten(elapsed + Remaining(replacement.End, now!.Value, clock.AgeMs));
                    if (elapsed >= _hardEnd) Finish(eventTime, "更新后的计划结束时间已到");
                }
            }
        }
        if (State.Active is { } ongoing)
        {
            Status = clock.Fresh && now >= ongoing.Plan.Lesson.End ? $"模拟课后余量：{ongoing.Plan.Lesson.Subject} · 剩余 {(_hardEnd - elapsed):mm\\:ss}" :
                $"模拟录制中：{ongoing.Plan.Lesson.Subject} · 最多剩余 {(_hardEnd - elapsed):hh\\:mm\\:ss}";
            if (!clock.CanStart) Status += "（时间源异常，仍按原期限结束）";
            return;
        }
        if (clock.DateNeedsReview) { Enabled = false; Status = clock.Message; return; }
        if (clock.Fresh && now is not null && State.SkipDate == DateOnly.FromDateTime(now.Value.Date)) { Status = "今天已暂停预演"; return; }
        if (!clock.CanStart) { Status = clock.Message + "；暂不模拟新的开始"; return; }
        bool hasFixed = plans.Any(p => p.Fixed && p.Selected);
        if (!fresh && !hasFixed) { Status = "等待新鲜日程，暂不模拟新的开始"; return; }
        if (fresh && !source!.Enabled && !hasFixed) { Status = "课表未启用，暂不预演"; return; }
        if (fresh && !source!.ClockVerified && !hasFixed) { Status = source.ClockMessage; return; }
        var due = plans.FirstOrDefault(p => p.Selected && !p.Conflict && (p.Fixed || fresh && source!.Enabled && source.ClockVerified) && now >= p.Start &&
            Remaining(p.Lesson.End, now!.Value, clock.AgeMs) > TimeSpan.Zero && !IsMarked(source?.ProfileId ?? Guid.Empty, p));
        if (due is not null)
        {
            // Mark on admission, before emitting the start: restarting must not duplicate this occurrence.
            Guid profile = due.Fixed ? Guid.Empty : source!.ProfileId;
            Mark(new(profile, due.Lesson.Start, due.Lesson.End, "已模拟开始", due.Fixed ? due.Key : null));
            State = State with { Active = new(profile, due, now!.Value) };
            _hardEnd = elapsed + Remaining(due.End, now.Value, clock.AgeMs);
            Add(new(now, "应开始", due.Lesson.Subject, now > due.Lesson.Start ? "课中补录预演；缺失的开头不补造" : "到达课前录制窗口"));
            Status = $"模拟录制中：{due.Lesson.Subject} · {due.End:HH:mm:ss} 结束";
        }
        else
        {
            var next = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Start > now && !IsMarked(source?.ProfileId ?? Guid.Empty, p));
            Status = next is null ? "本日没有待开始的预演任务" : $"下次预演：{next.Lesson.Subject} · {next.Start:HH:mm:ss}";
        }
    }
    private static TimeSpan Remaining(DateTimeOffset end, DateTimeOffset now, double ageMs) =>
        TimeSpan.FromMilliseconds(Math.Max(0, (end - now).TotalMilliseconds - ageMs));
    private void Tighten(TimeSpan deadline) => _hardEnd = _hardEnd is null || deadline < _hardEnd ? deadline : _hardEnd;
    private void Finish(DateTimeOffset? now, string reason)
    {
        if (State.Active is not { } active) return;
        Add(new(now, "应停止", active.Plan.Lesson.Subject, reason));
        State = State with { Active = null };
        _hardEnd = null;
    }
    private void Mark(PreviewMark mark) => State = State with { Marks = State.Marks.Append(mark).TakeLast(512).ToArray() };
    private void Add(PreviewEvent value) => State = State with { Events = State.Events.Append(value).TakeLast(200).ToArray() };
}

public sealed class RecordingPreviewStore(string path)
{
    public bool Migrated { get; private set; }
    public PreviewState Read()
    {
        if (!File.Exists(path)) return new(2, new(), [], []);
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("预演记录过大，原文件已保留。");
        var state = JsonSerializer.Deserialize<PreviewState>(File.ReadAllText(path));
        if (state is not { Version: 1 or 2 } || state.Rules is null || RecordingPlanner.Validate(state.Rules) is not null ||
            state.Events is null or { Length: > 200 } || state.Marks is null or { Length: > 512 } ||
            state.Marks.Any(m => m is null || m.End <= m.Start) || state.Events.Any(e => e is null) ||
            (state.Active is { } active && (active.Plan?.Lesson is null || active.Plan.End <= active.Plan.Start)))
            throw new InvalidDataException("预演配置或记录无效，原文件已保留。");
        if (state.Version == 1)
        {
            // Old marks describe system-calendar instants. Never silently reinterpret them as school dates.
            string backup = path + ".v1.bak";
            if (!File.Exists(backup)) File.Copy(path, backup, overwrite: false);
            else if (new FileInfo(backup).Length > 512 * 1024 || File.ReadAllText(backup) != File.ReadAllText(path))
                throw new InvalidDataException("已有不同的旧版备份，原文件已保留，请核对后迁移。");
            Migrated = true;
            return new(2, state.Rules, [], []);
        }
        return state;
    }
    public void Save(PreviewState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, path, true);
    }
}
