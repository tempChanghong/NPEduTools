using NPEduTools.Contracts;

namespace NPEduTools.App;

internal static class ManualStudyPresentation
{
    internal static string Heading(ManualStudyState? state) => state?.State switch
    {
        "Idle" => "开始一节手动自习",
        "Waiting" => "手动自习已预约",
        "Preparing" => "手动自习即将开始",
        "Starting" => "手动自习正在启动",
        "Active" => "手动自习进行中",
        "Stopping" => "正在结束手动自习",
        "Ended" or "Stopped" => "手动自习已结束",
        "Cancelled" when state.Reason is "CancelledBeforeStart" or "ScheduledTakeover" or "CLASSROOM_POLICY_CHANGED" => "自习预约已取消",
        "Cancelled" => "手动自习未开始",
        "Interrupted" => "手动自习已中断",
        _ => "手动会话状态待确认"
    };

    internal static string Details(ManualStudyState? state)
    {
        if (state is null) return "";
        var lines = new List<string>();
        bool scheduled = state.State is "Waiting" or "Preparing" or "Cancelled";
        if (state.StartUtc is { } start)
            lines.Add($"{(scheduled ? "预约开始" : "开始时间")}　{start.ToLocalTime():yyyy-MM-dd HH:mm}");
        if (state.EndUtc is { } end && state.State != "Interrupted")
        {
            bool ended = state.State is "Ended" or "Stopped";
            string label = ended ? "实际结束" : state.HasPlannedEnd ? "计划结束" : "最晚结束";
            string limit = !ended && !state.HasPlannedEnd ? "（三小时上限）" : "";
            lines.Add($"{label}　{end.ToLocalTime():yyyy-MM-dd HH:mm}{limit}");
        }
        if (state.State is "Waiting" or "Preparing") lines.Add("尚未开始采集。");
        if (state.State == "Starting") lines.Add("正在等待麦克风采样。");
        string? reason = state.Reason switch
        {
            "UserEnded" when state.State == "Stopping" => "老师已请求结束。",
            "UserEnded" => "老师主动结束了这节自习。",
            "PlannedEnd" => "已到指定的结束时间。",
            "ThreeHourLimit" => "已到手动自习的三小时上限。",
            "CancelledBeforeStart" => "预约已在开始前取消。",
            "CLASSROOM_POLICY_CHANGED" => "发起时的学校授权已变化。请重新核对本班权限并预约。",
            "ScheduledTakeover" when state.State == "Stopping" => "正在交给学校定时自习。",
            "ScheduledTakeover" => "学校定时自习已接管。",
            "SCHEDULE_PRIORITY" => "学校定时自习优先，此次手动自习未启动。",
            "MissedStart" => "预约时段已过。",
            "CaptureFault" => "麦克风采集中断。",
            "BackgroundExit" or "NoiseStopping" => "本机后台已退出或正在退出。",
            "SESSION_CHANGED" or "NoiseStateChanged" => "后台的采集会话已变更。",
            "MANUAL_REPORT_STORE_UNAVAILABLE" => "本机报告文件保存失败。",
            "EQUIPMENT_LOCKED" => "学校已锁定采集设备。",
            "MicrophoneNotConfigured" => "请先选择并保存麦克风。",
            "NoiseBusy" => "麦克风正在使用中。",
            "NoiseCleanupRequired" => "麦克风尚未释放，请检查后台状态。",
            "NoiseConfigurationUnavailable" => "麦克风设置保存失败。",
            null or "" => null,
            _ => "请核对本机后台状态。"
        };
        if (reason is not null) lines.Add(reason);
        return string.Join("\n", lines);
    }
}
