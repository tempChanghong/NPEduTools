using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class StudySchedulePresentation
{
    public static string Next(StudyHostState? state)
    {
        const string title = "下一次定时自习：";
        if (state is null || state.PolicyState != "APPLIED") return title + Unavailable(state);
        if (state.Reason == "OFFLINE_CONTINUING") return title + "学校连接正在恢复，本次自习继续；后续安排待同步";
        string mode = state.Mode == "Exam" ? "考试模式" : "日常模式";
        if (state.Reason == "NOT_CONFIGURED") return title + "学校尚未配置本班安排";
        if (state.Phase == "Disabled") return title + mode + "未启用";
        if (!state.ClockReady || state.SchoolNow is null) return title + mode + "时间待确认（学校安排已收到）";
        if (state.Reason == "INVALID_STUDY_CALENDAR") return title + "学校安排无效，请联系管理员";
        if (state.Next is not { } next) return title + mode + "没有后续时段";
        string source = state.ClockSource == "Windows" ? "Windows 时间" : "ClassIsland 时间";
        return title + $"{Span(next)} · {mode} · {source}" +
            (state.Reason == "EXAM_PAUSED" ? "（当前暂停，尚不会执行）" : "");
    }

    public static string Unavailable(StudyHostState? state) => state?.Reason switch
    {
        "WAITING_CONFIGURATION" => "正在等待学校下发安排",
        "NOT_ELIGIBLE" => "学校互联尚未就绪或已暂停",
        "STUDY_OFFLINE" => "学校安排暂时无法同步，将自动重试",
        "CONFIGURATION_STALE" => "学校安排确认已过期，正在重新同步",
        "TEMPORARILY_UNAVAILABLE" => "学校服务暂时不可用，将自动重试",
        "RATE_LIMITED" => "同步请求暂时受限，将自动重试",
        "STUDY_UNSUPPORTED" => "学校服务尚不支持自习安排",
        "INVALID_STUDY_POLICY" or "INVALID_RESPONSE" => "学校安排读取失败，请核对服务版本与配置",
        "POLICY_CHANGED" => "学校安排已变更，正在重新同步",
        "SESSION_SUPERSEDED" => "互联会话已变化，请检查学校连接",
        "AUTH_INVALID" or "DEVICE_SUSPENDED" or "BINDING_CHANGED" => "学校授权或班级绑定已变化，请检查学校连接",
        _ => "学校安排状态待确认"
    };

    public static string Configuration(StudyHostState? state)
    {
        if (state is null || state.PolicyState != "APPLIED") return Unavailable(state);
        if (state.Reason == "OFFLINE_CONTINUING") return "学校连接正在恢复，沿用本次已确认安排；展示与采集状态由本机核实。";
        if (state.Settings is null) return state.Reason == "NOT_CONFIGURED"
            ? "请在学校后台核对本机绑定班级的年级继承或班级安排。" : "学校已关闭本班自习安排。";
        var schedule = state.Settings.ForMode(state.Mode).Schedule;
        string mode = state.Mode == "Exam" ? "考试模式" : "日常模式";
        if (schedule.Rules.Count == 0) return $"已收到学校配置 · {mode}未设置时段";
        string[] days = ["", "一", "二", "三", "四", "五", "六", "日"];
        string rules = string.Join("；", schedule.Rules.Select(rule =>
            $"周{string.Join("、", rule.Days.Select(day => days[day]))} {rule.Start}–{rule.End}"));
        return $"已收到学校配置 · {mode}\n{rules}\n生效日期 {schedule.EffectiveFrom}" +
            (schedule.EffectiveTo is { } end ? $" 至 {end}" : "起") + "；实际执行还受例外日与时间状态影响。";
    }

    internal static string Span(SchoolNoiseWindow window) => window.Start.Date == window.End.Date
        ? $"{window.Start:yyyy-MM-dd HH:mm}–{window.End:HH:mm}"
        : $"{window.Start:yyyy-MM-dd HH:mm}–{window.End:yyyy-MM-dd HH:mm}";
}
