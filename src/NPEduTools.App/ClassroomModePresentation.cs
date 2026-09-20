using NPEduTools.Contracts;

namespace NPEduTools.App;

public sealed record ClassroomModePresentation(string Title, string Detail, string RailLabel, bool Attention)
{
    public static ClassroomModePresentation From(ClassroomModeState? state)
    {
        if (state is null) return new("课堂模式 · 状态未知", "后台未连接，暂时无法核实模式与录课暂停状态。", "未知", true);
        string name = state.Mode switch { "Daily" => "日常模式", "Exam" => "考试模式", _ => "尚未设置模式" };
        bool attention = state.Phase is "Incomplete" or "Unavailable" || state.MatchesMode == false;
        string suffix = state.Busy ? " · 正在切换／核实" : state.Phase == "Incomplete" ? " · 切换未完成" :
            state.Phase == "Unavailable" ? " · 记录不可用" : state.MatchesMode == false ? " · 设置不一致" : "";
        string detail = state.AutomaticPaused ? "自动录课因课堂模式暂停，原计划保留。" : "自动录课按原有启用状态与计划判断。";
        if (attention || state.Busy) detail = state.Message + " " + detail;
        return new(name + suffix, detail, state.Busy ? "切换" : attention ? "待处理" :
            state.Mode == "Exam" ? "考试" : state.Mode == "Daily" ? "日常" : "未设", attention);
    }
}

