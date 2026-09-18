using System.Text.Json;
using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record PreviewEvent(DateTimeOffset At, string Action, string Subject, string Message);
public sealed record PreviewMark(Guid ProfileId, DateTimeOffset Start, DateTimeOffset End, string Reason);
public sealed record PreviewSession(Guid ProfileId, PlannedRecording Plan, DateTimeOffset StartedAt);
public sealed record PreviewState(int Version, RecordingRules Rules, PreviewEvent[] Events, PreviewMark[] Marks,
    DateOnly? SkipDate = null, PreviewSession? Active = null);

/// <summary>Pure rehearsal state machine. Deliberately has no recorder or process-launch dependency.</summary>
public sealed class RecordingPreview
{
    public PreviewState State { get; private set; }
    public bool Enabled { get; private set; }
    public string Status { get; private set; } = "预演未启动；不会采集屏幕或声音";
    private DateTimeOffset? _lastTick;

    public RecordingPreview(PreviewState? state = null)
    {
        State = state ?? new(1, new(), [], []);
        if (State.Active is not null) Finish(DateTimeOffset.Now, "上次程序退出，预演中断；本节不会重复模拟开始");
    }
    public void Configure(RecordingRules rules)
    {
        if (RecordingPlanner.Validate(rules) is { } error) throw new InvalidDataException(error);
        State = State with { Rules = rules };
    }
    public void SetEnabled(bool enabled, DateTimeOffset now)
    {
        Enabled = enabled;
        if (enabled) _lastTick = null;
        if (!enabled) Finish(now, "用户停止预演");
        Status = enabled ? "预演已启动，等待新鲜日程" : "预演已停止；不会采集屏幕或声音";
    }
    public bool IsMarked(Guid profile, LessonSlot lesson) => State.Marks.Any(m => m.ProfileId == profile &&
        m.Start < lesson.End && lesson.Start < m.End);
    public void Skip(Guid profile, LessonSlot lesson, DateTimeOffset now)
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

    public void Tick(DaySchedule? source, PlannedRecording[] plans, DateTimeOffset now)
    {
        if (!Enabled) return;
        if (_lastTick is { } previous && now < previous.AddSeconds(-2))
        { Finish(now, "检测到时间回拨，预演已停止"); Enabled = false; Status = "时间回拨，请重新确认后启动预演"; return; }
        _lastTick = now;
        bool fresh = source is not null && source.SampledAt <= now.AddSeconds(2) && now - source.SampledAt <= TimeSpan.FromSeconds(15) &&
            source.Date == DateOnly.FromDateTime(now.Date);
        // An active rehearsal has its own fixed end, even while disconnected or the clock is unverified.
        if (State.Active is { } active)
        {
            if (now >= active.Plan.End) Finish(now, "到达计划结束时间（含课后余量）");
            else if (fresh && (!source!.Enabled || source.ProfileId != active.ProfileId)) Finish(now, "生效课表已禁用或档案已切换");
            else if (fresh)
            {
                var replacement = plans.FirstOrDefault(p => p.Selected && !p.Conflict &&
                    p.Lesson.Start < active.Plan.Lesson.End && active.Plan.Lesson.Start < p.Lesson.End);
                if (replacement is null) Finish(now, "课表或规则变化，本节已不在计划中");
                else if (replacement.End < active.Plan.End)
                {
                    State = State with { Active = active with { Plan = active.Plan with { End = replacement.End } } };
                    if (now >= replacement.End) Finish(now, "更新后的计划结束时间已到");
                }
            }
        }
        if (State.Active is { } ongoing)
        {
            Status = now >= ongoing.Plan.Lesson.End ? $"模拟课后余量：{ongoing.Plan.Lesson.Subject} · 剩余 {(ongoing.Plan.End - now):mm\\:ss}" :
                $"模拟录制中：{ongoing.Plan.Lesson.Subject} · {ongoing.Plan.End:HH:mm:ss} 结束";
            if (!fresh) Status += "（连接中断，仍按原期限结束）";
            return;
        }
        if (State.SkipDate == DateOnly.FromDateTime(now.Date)) { Status = "今天已暂停预演"; return; }
        if (!fresh) { Status = "等待新鲜日程，暂不模拟新的开始"; return; }
        if (!source!.Enabled) { Status = "课表未启用，暂不预演"; return; }
        if (!source.ClockVerified) { Status = source.ClockMessage; return; }
        var due = plans.FirstOrDefault(p => p.Selected && !p.Conflict && now >= p.Start && now < p.Lesson.End && !IsMarked(source.ProfileId, p.Lesson));
        if (due is not null)
        {
            // Mark on admission, before emitting the start: restarting must not duplicate this occurrence.
            Mark(new(source.ProfileId, due.Lesson.Start, due.Lesson.End, "已模拟开始"));
            State = State with { Active = new(source.ProfileId, due, now) };
            Add(new(now, "应开始", due.Lesson.Subject, now > due.Lesson.Start ? "课中补录预演；缺失的开头不补造" : "到达课前录制窗口"));
            Status = $"模拟录制中：{due.Lesson.Subject} · {due.End:HH:mm:ss} 结束";
        }
        else
        {
            var next = plans.FirstOrDefault(p => p.Selected && !p.Conflict && p.Start > now && !IsMarked(source.ProfileId, p.Lesson));
            Status = next is null ? "本日没有待开始的预演任务" : $"下次预演：{next.Lesson.Subject} · {next.Start:HH:mm:ss}";
        }
    }
    private void Finish(DateTimeOffset now, string reason)
    {
        if (State.Active is not { } active) return;
        Add(new(now, "应停止", active.Plan.Lesson.Subject, reason));
        State = State with { Active = null };
    }
    private void Mark(PreviewMark mark) => State = State with { Marks = State.Marks.Append(mark).TakeLast(512).ToArray() };
    private void Add(PreviewEvent value) => State = State with { Events = State.Events.Append(value).TakeLast(200).ToArray() };
}

public sealed class RecordingPreviewStore(string path)
{
    public PreviewState Read()
    {
        if (!File.Exists(path)) return new(1, new(), [], []);
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("预演记录过大，原文件已保留。");
        var state = JsonSerializer.Deserialize<PreviewState>(File.ReadAllText(path));
        if (state is not { Version: 1 } || state.Rules is null || RecordingPlanner.Validate(state.Rules) is not null ||
            state.Events is null or { Length: > 200 } || state.Marks is null or { Length: > 512 } ||
            state.Marks.Any(m => m is null || m.End <= m.Start) || state.Events.Any(e => e is null) ||
            (state.Active is { } active && (active.Plan?.Lesson is null || active.Plan.End <= active.Plan.Start)))
            throw new InvalidDataException("预演配置或记录无效，原文件已保留。");
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
