using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class HomeRecordingPresentation
{
    public static string Title(RecordingState state, bool confirmed) => !confirmed ? "录课状态待确认" : state.Phase switch
    {
        "Starting" => "录课准备中", "Recording" => state.Control?.Owner == "Automatic" ? "自动录课进行中" : "手动录课进行中",
        "Paused" => "录课已暂停", "Pausing" => "正在暂停录课", "Saving" => "正在保存录课", "Saved" => "上次录课已保存",
        "Failed" => "上次录课未完成", "Idle" => "当前没有录课任务", _ => "录课状态待确认"
    };
    public static string Action(RecordingState state, bool confirmed) => !confirmed ? "检查录课状态" :
        state.Active ? "管理当前录课" : state.Phase is "Saved" or "Failed" ? "查看录制结果" : state.Phase == "Idle" ? "立即录课" : "检查录课状态";
    public static string Next(AutomaticRecordingState state, bool confirmed)
    {
        if (!confirmed) return "后台尚未确认录课安排";
        if (!state.Enabled) return "自动录课未开启；保存安排不会自动开始";
        if (state.SuspendedByMode || state.NextState == "Suspended") return "当前模式暂停自动录课，计划保留";
        if (state.NextState == "Ready" && state.Next is { ClockSource: "ClassIsland" } next &&
            !string.IsNullOrWhiteSpace(next.Subject) && next.Start.Offset == TimeSpan.Zero && next.End.Offset == TimeSpan.Zero && next.End > next.Start)
            return $"{next.Subject} · {next.Start:yyyy-MM-dd HH:mm}–" + (next.Start.Date == next.End.Date ? $"{next.End:HH:mm}" : $"{next.End:yyyy-MM-dd HH:mm}") + " · ClassIsland 时间";
        return state.NextState switch
        {
            "ClockPending" => "学校时间尚未确认，暂不显示下一项",
            "SkippedToday" => "今天已暂停自动录课，计划保留",
            "None" => "今天没有剩余的待开始任务",
            "Pending" => "等待后台计算下一项安排",
            _ => "后台未提供可确认的下一项，请查看安排"
        };
    }
}
