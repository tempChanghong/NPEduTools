using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class StudySchedulePresentation
{
    public static string Next(StudyHostState? state)
    {
        const string title = "下一次定时自习：";
        if (state is null || state.PolicyState != "APPLIED") return title + "学校安排待确认";
        string mode = state.Mode == "Exam" ? "考试模式" : "日常模式";
        if (state.Phase == "Disabled") return title + mode + "未启用";
        if (!state.ClockReady || state.SchoolNow is null) return title + mode + "时间待确认";
        if (state.Reason == "INVALID_STUDY_CALENDAR") return title + "学校安排无效，请联系管理员";
        if (state.Next is not { } next) return title + mode + "没有后续时段";
        string source = state.ClockSource == "Windows" ? "Windows 时间" : "ClassIsland 时间";
        return title + $"{Span(next)} · {mode} · {source}" +
            (state.Reason == "EXAM_PAUSED" ? "（当前暂停，尚不会执行）" : "");
    }

    internal static string Span(SchoolNoiseWindow window) => window.Start.Date == window.End.Date
        ? $"{window.Start:yyyy-MM-dd HH:mm}–{window.End:HH:mm}"
        : $"{window.Start:yyyy-MM-dd HH:mm}–{window.End:yyyy-MM-dd HH:mm}";
}
