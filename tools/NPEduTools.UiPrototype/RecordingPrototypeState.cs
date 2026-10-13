using NPEduTools.Contracts;
using NPEduTools.Core;

namespace NPEduTools.UiPrototype;

// In-memory UX fixture. Uses the production pure planner, never a Host or recorder.
internal sealed class RecordingPrototypeState
{
    internal static readonly DateOnly DemoDate = new(2026, 10, 13);
    private static readonly Guid Profile = Guid.Parse("ff4845fa-d2ee-49b5-9c78-9fabfe8b29b6");
    internal DateOnly ViewDate = DemoDate;
    internal DateTimeOffset Now = At(DemoDate, 7, 25);
    internal RecordingPlanBook Saved = RecordingPlanBook.Create(), Draft;
    internal bool AutomaticEnabled, ClockReady = true;
    internal string Phase = "Idle", Owner = "Manual", Audio = "麦克风与系统声音已选择", ActiveSubject = "";
    internal DateTimeOffset Started;
    internal DateTimeOffset? End;
    internal readonly List<DemoRecording> Records = [];
    internal DemoRecording? SelectedRecord;
    internal int Seconds;
    private readonly HashSet<string> _handled = [];
    internal RecordingPrototypeState()
    {
        Draft = Saved;
        Records.Add(new("昨天下午 · 数学", "14:00–14:45", "已保存", "自动录课", @"D:\课堂录像\示例-数学.mp4", "视频已保存（演示）。"));
        Records.Add(new("昨天上午 · 英语", "09:00–09:23", "未完成", "自动录课", @"D:\课堂录像\.npeedutools-sessions\示例-英语", "音频写入中断；原始片段与日志已保留（演示）。"));
    }
    internal bool Active => Phase is "Recording" or "Paused";
    internal bool Dirty => System.Text.Json.JsonSerializer.Serialize(Draft) != System.Text.Json.JsonSerializer.Serialize(Saved);
    internal static DateTimeOffset At(DateOnly date, int hour, int minute) => new(date.ToDateTime(new(hour, minute)), TimeSpan.Zero);
    internal DaySchedule Day(DateOnly date) => new(Profile, Guid.Empty, Guid.Empty, "演示课表", date, "demo", Now, true, ClockReady, "演示学校时间",
        [Slot(date,1,"周测",7,30,8,0), Slot(date,2,"语文",8,0,8,45), Slot(date,3,"数学",8,55,9,40),
         Slot(date,4,"英语",10,0,10,45), Slot(date,5,"体育",11,0,11,45), Slot(date,6,"化学",14,0,14,45)]);
    private static LessonSlot Slot(DateOnly date, int number, string subject, int sh, int sm, int eh, int em) =>
        new(number, new Guid(number, 0, 0, new byte[8]), subject, At(date, sh, sm), At(date, eh, em), true);
    internal PlannedRecording[] Plans(bool saved = false) => CalendarRecordingPlanner.Build(saved ? Saved : Draft, ViewDate, Day(ViewDate));
    internal bool Handled(PlannedRecording plan) => _handled.Contains(plan.Key);
    internal PlannedRecording? NextPlan() => Plans(true).FirstOrDefault(p => p.Selected && !p.Conflict && p.Lesson.End > Now && !_handled.Contains(p.Key));
    internal void ToggleCourse(int number)
    {
        var plan = Plans().Single(p => p.Lesson.Number == number);
        Draft = Draft with { Overrides = [.. Draft.Overrides.Where(o => o.Date != ViewDate || o.Number != number),
            new(Guid.NewGuid(), Profile, ViewDate, number, plan.Lesson.Start, plan.Lesson.End, plan.Lesson.Subject, plan.Selected ? "Exclude" : "Include")] };
    }
    internal void Save() => Saved = Draft;
    internal void Discard() => Draft = Saved;
    internal void StartManual()
    {
        if (Active) return;
        Owner = "Manual"; Phase = "Recording"; Started = Now; Seconds = 0; End = null; ActiveSubject = "手动微课";
        Audio = "麦克风与系统声音正常（演示）";
    }
    internal void Stop(string result = "已保存", string reason = "视频已保存（演示）。")
    {
        if (!Active) return;
        Records.Insert(0, new(ActiveSubject, $"{Started:HH:mm}–{Now:HH:mm}", result, Owner == "Automatic" ? "自动录课" : "本次手动演示",
            result == "已保存" ? @"D:\课堂录像\示例-新录课.mp4" : @"D:\课堂录像\.npeedutools-sessions\示例-中断", reason));
        Phase = result == "已保存" ? "Saved" : "Failed"; End = null;
    }
    internal string? Advance()
    {
        if (!ClockReady) return "学校时间尚未确认，不能触发新的自动任务。";
        // The fixture advances a frozen school clock; this is not a live scheduling loop.
        if (Active && Owner == "Automatic")
        { Now = End!.Value; Stop(); return "已演示到期结束，视频已保存。"; }
        var next = NextPlan();
        if (next is null) return "这一天没有剩余的演示录课任务。";
        Now = next.Start > Now ? next.Start : Now;
        if (!AutomaticEnabled) return "演示时间已推进；自动录课尚未开启，所以没有开始录制。";
        _handled.Add(next.Key);
        if (Active)
        {
            Records.Insert(0,new(next.Lesson.Subject,$"{next.Lesson.Start:HH:mm}–{next.Lesson.End:HH:mm}","已跳过","自动录课","","被手动录制占用；没有中断手动录制（演示）。"));
            return "该节自动任务已跳过，原手动录制继续。";
        }
        Owner = "Automatic"; Phase = "Recording"; Started = Now; Seconds = 0; End = next.End; ActiveSubject = next.Lesson.Subject;
        Audio = "麦克风与系统声音正常（演示）";
        return $"已演示开始：{next.Lesson.Subject} · {Now:HH:mm}。";
    }
    internal void SkipNext()
    {
        var next = NextPlan();
        if (next is null) return;
        _handled.Add(next.Key);
        Records.Insert(0,new(next.Lesson.Subject,$"{next.Lesson.Start:HH:mm}–{next.Lesson.End:HH:mm}","已跳过","自动录课","","用户跳过本节（演示）。"));
    }
}
internal sealed record DemoRecording(string Subject, string Time, string Status, string Owner, string Path, string Detail);
internal sealed record DemoLessonRow(int Number, string Time, string Subject, string Capture, string Status, string Reason, string Action);
