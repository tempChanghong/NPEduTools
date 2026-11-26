using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static partial class HomeRecordingPresentation
{
    public static (string Text, string Mark, string Brush) Phase(string phase, bool confirmed) => (confirmed ? phase : "") switch
    {
        "Idle" => ("准备录制", "○", "TeacherMutedBrush"),
        "Starting" => ("准备中", "…", "TeacherMutedBrush"),
        "Recording" => ("录制中", "●", "TeacherErrorBrush"),
        "Pausing" => ("暂停中", "…", "TeacherWarningBrush"),
        "Paused" => ("已暂停", "Ⅱ", "TeacherWarningBrush"),
        "Saving" => ("保存中", "…", "TeacherMutedBrush"),
        "Saved" => ("已保存", "✓", "TeacherSuccessBrush"),
        "Failed" => ("未完成", "!", "TeacherErrorBrush"),
        _ => ("状态未知", "?", "TeacherMutedBrush")
    };

    private static string Owner(RecordingState state) => state.Control?.Owner switch
    {
        "Automatic" => "自动录课", "Manual" => "手动录课", _ => "来源待确认"
    };

    public static string Detail(RecordingState state, bool confirmed, bool includeOwner = true)
    {
        if (!confirmed) return "录制后台未确认当前状态，等待重新连接。";
        if (!state.Active) return state.Message;
        var clock = TimeSpan.FromSeconds(state.Seconds).ToString(@"hh\:mm\:ss");
        return (includeOwner ? $"{Owner(state)} · " : "") + clock +
            (string.IsNullOrWhiteSpace(state.Message) ? "" : $" · {state.Message}");
    }

    public static string Title(RecordingState state, bool confirmed) => !confirmed ? "录课状态待确认" : state.Phase switch
    {
        "Starting" => "录课准备中", "Recording" => state.Control?.Owner switch
        { "Automatic" => "自动录课进行中", "Manual" => "手动录课进行中", _ => "录课进行中 · 来源待确认" },
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
        if (ConfirmedNext(state) is { } next)
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

    public static UpcomingRecording? ConfirmedNext(AutomaticRecordingState state) =>
        state is { Enabled: true, SuspendedByMode: false, NextState: "Ready", Next: { ClockSource: "ClassIsland" } next } &&
        !string.IsNullOrWhiteSpace(next.Subject) && next.Start.Offset == TimeSpan.Zero && next.End.Offset == TimeSpan.Zero && next.End > next.Start
            ? next : null;
}
