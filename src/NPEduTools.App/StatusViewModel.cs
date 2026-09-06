using System.ComponentModel;
using NPEduTools.Contracts;

namespace NPEduTools.App;

public sealed class StatusViewModel : INotifyPropertyChanged
{
    public string Connection { get; private set; } = "正在连接后台";
    public string State { get; private set; } = "等待课程状态";
    public string Subject { get; private set; } = "—";
    public string Detail { get; private set; } = "首次打开时将自动启动后台。";
    public string Updated { get; private set; } = "尚未同步";
    public string Events { get; private set; } = "等待连接";
    public string LessonPlan { get; private set; } = "—";
    public string Accent { get; private set; } = "#8C713B";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(WatchSnapshot snapshot)
    {
        bool healthy = snapshot.Outcome == "Succeeded" && snapshot.Status is not null;
        Connection = healthy ? "已连接 ClassIsland" : snapshot.Outcome == "Connecting" ? "正在连接 ClassIsland" : "ClassIsland 暂不可用";
        Accent = healthy ? "#286B51" : "#8C713B";
        var s = healthy ? snapshot.Status : null;
        State = s?.State switch
        {
            "OnClass" => "正在上课", "Breaking" => "课间休息", "AfterSchool" => "已放学",
            "None" => "当前无课程", null => "等待课程状态", _ => "课程状态：" + s.State
        };
        Subject = !healthy ? "—" : s is { IsClassPlanLoaded: false } ? "暂无科目" : s?.Subject ?? "暂无科目";
        Detail = healthy ? (s!.IsTimerRunning ? "课程状态自动同步中。" : "ClassIsland 计时已暂停。") : snapshot.Message;
        Updated = s is null ? "当前状态不可用" : "最近同步  " + s.SampleCompletedAt.ToLocalTime().ToString("HH:mm:ss");
        LessonPlan = s is null ? "—" : !s.IsClassPlanLoaded ? "未加载课表" : s.IsClassPlanEnabled ? "课表已启用" : "课表已停用";
        Events = s is null ? "连接恢复后重新同步" :
            $"本次连接  ·  上课 {Count(s, "onClass")}  /  课间 {Count(s, "onBreakingTime")}";
        Notify();
    }

    public void Disconnected(string message)
    {
        Connection = "后台未连接";
        Accent = "#8C713B";
        State = "等待课程状态";
        Subject = "—";
        Detail = message;
        Updated = "当前状态不可用";
        Events = "连接恢复后重新同步";
        LessonPlan = "—";
        Notify();
    }

    private static long Count(LessonStatusDto status, string suffix) =>
        status.ObservedEvents.GetValueOrDefault("classisland.lessonsService." + suffix);
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
