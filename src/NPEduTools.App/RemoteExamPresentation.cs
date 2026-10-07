using NPEduTools.Contracts;

namespace NPEduTools.App;

public enum RemoteExamLocalAction { CheckEnvironment, InspectCurrent, EndPause }
public sealed record RemoteExamFeedback(string Title, string Message, string NextAction, string Details);
public sealed record RemoteExamHistoryItem(string Title, string Step, string Message, string NextAction,
    string LocallyEnded, string Details);

// Presentation only: never infers completed steps, changes permissions or retries a command.
public static class RemoteExamPresentation
{
    private sealed record Reason(string Message, string NextAction);

    public static RemoteExamHistoryItem History(RemoteExamHistory entry)
    {
        string target = entry.Target switch { "Exam" => "进入考试", "Daily" => "返回日常", _ => "目标模式待核实" };
        string outcome = entry.Outcome switch
        {
            "SUCCEEDED" => "切换成功（历史回执）", "REJECTED" => "请求未执行", "PARTIAL" => "切换未完成",
            "UNKNOWN" => "结果未知", "CHECKING" => "正在检查", "RUNNING" => "正在执行",
            "WAITING_LOCAL" => "等待现场处理", "RECEIVED" => "请求已收到", _ => "结果待核实"
        };
        var reason = Explain(entry.Reason);
        bool terminalFailure = entry.Outcome is "PARTIAL" or "REJECTED" or "UNKNOWN";
        string message = entry.Outcome switch
        {
            "SUCCEEDED" => "执行时已核实目标环境；这条回执不代表软件此刻仍处于该状态。",
            "PARTIAL" => "本次切换没有全部完成，部分软件或自启动设置可能已改变。" + reason.Message,
            "REJECTED" => reason.Message,
            "UNKNOWN" => "未能确认本次操作的最终结果。" + reason.Message,
            "CHECKING" or "RUNNING" or "RECEIVED" => "请求尚未完成，请等待执行回执；不要把受理当作切换成功。",
            "WAITING_LOCAL" => reason.Message,
            _ => "收到未识别的结果，不能确认切换成功。"
        };
        string next = terminalFailure || entry.Outcome == "WAITING_LOCAL" ? reason.NextAction :
            entry.Outcome == "SUCCEEDED" ? "需要确认当前环境时，点击“核实当前考试状态”或“检查考试环境”。" :
            entry.Outcome is "CHECKING" or "RUNNING" or "RECEIVED" ? "等待刷新；此处不会自动重发操作。" :
            "先刷新并核实当前环境，再由学校明确发起新的请求；此处不会自动重试。";
        string ended = entry.LocallyEndedAt is { } time
            ? $"本机已解除该次远程录课暂停：{time.ToLocalTime():yyyy-MM-dd HH:mm:ss}。原切换结果仍保留。" : "";
        return new($"{entry.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {target} · {outcome}",
            "最后记录的步骤：" + Step(entry.Step), message, next, ended,
            $"请求编号：{entry.OperationId}\n目标：{entry.Target}\n原始结果：{entry.Outcome}\n原始步骤：{entry.Step}\n错误码：{entry.Reason ?? "无"}");
    }

    public static RemoteExamFeedback Feedback(HostResponse reply, RemoteExamLocalAction action)
    {
        string details = $"请求编号：{reply.RequestId}\n原始结果：{reply.Outcome}\n错误码：{reply.ErrorCode ?? "无"}\n后台说明：{reply.Message}";
        bool succeeded = reply.Outcome == "Succeeded" && reply.ErrorCode is null;
        // A successful response without a readable state must not imply the pause was cleared.
        if (succeeded && reply.RemoteExam is { StorageError: null } state &&
            (action != RemoteExamLocalAction.EndPause || !state.AutomaticPaused))
        {
            return action switch
            {
                RemoteExamLocalAction.EndPause => new("已解除远程录课暂停",
                    "仅解除这次远程录课暂停，没有切换软件或修改自启动，也没有清除历史。",
                    "自动录课仍按原开关、计划和课堂模式判断；这不等同于返回日常模式。", details),
                RemoteExamLocalAction.InspectCurrent => new("当前考试状态已核实",
                    "本次只核查当前环境，没有执行软件切换。",
                    "考试结束后，先完整返回日常，再按需要解除远程录课暂停。", details),
                _ => new("本机检查通过", "本次只检查权限、程序位置和软件连接，没有执行切换或修改自启动。",
                    "检查通过不等于已进入考试模式；远程切换仍需学校明确请求。", details)
            };
        }
        if (succeeded || reply.Outcome is not ("Rejected" or "Failed"))
            return new("操作结果待核实", "尚未取得可确认的最终状态，不能认定操作成功。",
                "先刷新并核实当前状态；不要根据这一回执重复操作。此处不会自动重发。", details);
        var reason = Explain(reply.ErrorCode);
        return new(action == RemoteExamLocalAction.EndPause ? "未能确认暂停已解除" : "本机检查未通过",
            reason.Message, reason.NextAction, details);
    }

    public static string Runtime(RemoteExamStatus? state) => state is null
        ? "当前远程录课暂停状态未知，请等待后台连接恢复。"
        : state.StorageError is not null
            ? "考试记录暂时不可用，录课保护仍保留。请先处理存储故障。"
            : state.AutomaticPaused ? "当前远程录课已暂停，原计划保留。"
            : "当前没有远程录课暂停；是否开始录课仍取决于原开关、计划及课堂模式。";

    public static string RuntimeDetails(RemoteExamStatus? state) => state is null ? "" :
        $"当前暂停请求编号：{state.PauseOperationId?.ToString() ?? "无"}\n状态版本：{state.RuntimeRevision}\n存储错误：{state.StorageError ?? "无"}";

    public static string Step(string step) => step switch
    {
        "Check" => "检查本机环境", "PauseRecording" => "保存录制并建立录课暂停",
        "PrepareExam" => "准备 ExamAware2 考试看板", "SetStartup" => "设置登录自启动",
        "CloseClassIsland" => "正常退出 ClassIsland", "CloseExam" => "正常退出 ExamAware2",
        "StartClassIsland" => "启动 ClassIsland 并等待课程接口", "Verify" => "核实切换结果",
        _ => "步骤待核实（见技术详情）"
    };

    private static Reason Explain(string? code) => code switch
    {
        "STARTUP_NOT_READY" => new("登录自启动设置尚未得到确认。",
            "检查 ExamAware2 桥接权限及 ClassIsland 管理员任务；处理后由学校重新请求核查并补完切换。"),
        "CLASSISLAND_TASK_REQUIRED" => new("ClassIsland 尚未具备可核实的管理员登录任务。",
            "在 ClassIsland 软件连接设置中创建并核实对应程序的管理员任务，再重新检查。"),
        "CLASSISLAND_CONFIGURATION_REQUIRED" => new("ClassIsland 的程序位置或配置未准备好。", "保存正确程序位置并处理配置警告，再重新检查。"),
        "EXAMAWARE_CONFIGURATION_REQUIRED" => new("ExamAware2 的程序位置或桥接未准备好。", "在考试看板保存程序位置，核对桥接配对与配置，再重新检查。"),
        "CLASSISLAND_EXECUTABLE_INVALID" => new("ClassIsland 程序文件无效或缺失。", "重新选择实际使用的 ClassIsland 可执行文件，保存后重新检查。"),
        "EXAMAWARE_EXECUTABLE_INVALID" => new("ExamAware2 程序文件无效、缺失或版本不受支持。", "重新选择受支持的 ExamAware2 程序，保存后重新检查。"),
        "INVALID_LOCAL_EXECUTABLE" => new("本机教学软件的程序文件无法通过验证。", "分别核对 ClassIsland 与 ExamAware2 的程序位置、文件及版本；详细后台说明见技术详情。"),
        "EXAMAWARE_EXECUTABLE_UNREADABLE" => new("无法读取 ExamAware2 程序文件。", "核对路径及文件访问权限，再重新检查。"),
        "CLASSISLAND_IDENTITY_UNAVAILABLE" => new("无法核实 ClassIsland 的程序实例或所属会话。", "检查其他路径、用户或会话中的 ClassIsland 实例，再重新核实。"),
        "EXAMAWARE_IDENTITY_UNAVAILABLE" => new("无法核实 ExamAware2 的程序实例或所属会话。", "检查其他路径、用户或会话中的 ExamAware2 实例，再重新核实。"),
        "EXAMAWARE_NOT_READY" => new("ExamAware2 考试看板或桥接接口尚未就绪。", "检查 ExamAware2 与桥接连接，等待就绪后再由学校请求核查。"),
        "CLASSISLAND_NOT_READY" => new("ClassIsland 的课程接口尚未就绪。", "核对程序运行、插件和课程接口，等待就绪后重新请求返回日常。"),
        "EXAMAWARE_PRESENTING" => new("ExamAware2 正在放映考试方案。", "先在 ExamAware2 正常结束放映，再重新请求切换。"),
        "EXAMAWARE_EXIT_FAILED" => new("ExamAware2 未能正常退出。", "先保存并关闭编辑器、正常结束放映，处理软件提示后再请求切换；不会强制结束进程。"),
        "CLASSISLAND_EXIT_UNAVAILABLE" => new("无法确认 ClassIsland 已正常退出。", "核对 ClassIsland 的实例与退出提示，再由学校重新请求核查。"),
        "HOST_NOT_ELEVATED" => new("实际执行操作的 NPEduTools 后台没有管理员权限。", "正常保存并结束录制后完整退出 NPEduTools，再以管理员身份启动；仅提升前台不足以替换原后台。"),
        "RECORDING_BUSY" => new("录制器仍在工作或状态尚未确认。", "本机检查不会停止录制；等待正常保存或先在本机停止录制，再重新检查。"),
        "RECORDING_SAVE_FAILED" => new("当前录制未能正常保存。", "检查录制错误、保存目录和剩余空间；保留现有片段，处理后再请求切换。"),
        "RECORDING_SAVE_TIMEOUT" => new("等待录制保存超时，尚不能确认保存完成。", "先核实录制和保存状态，再请求切换；不会强制结束录制器。"),
        "OPERATION_BUSY" => new("当前有其他操作占用，尚不能执行本次检查或切换。", "核对正在处理的通知或软件管理操作，等待结束后重新检查；此处不会自动重试。"),
        "DESKTOP_UNAVAILABLE" => new("当前 Windows 交互桌面不可用。", "解锁电脑并回到运行 NPEduTools 的用户会话，再重新检查。"),
        "AUTH_REVOKED" or "CONTROL_DISABLED" or "CONTROL_OFFLINE" or "POLICY_CHANGED" or "POLICY_STORE_UNAVAILABLE" =>
            new("学校连接、授权或许可状态已变化，当前请求不能继续。", "核实学校连接、设备绑定与后台权限；处理后刷新，由有权限的管理员明确发起新请求。"),
        "EXPIRED" => new("本次远程请求已过期。", "先核实实际环境，再由学校管理员发起新的请求。"),
        "STATE_CHANGED" or "CONFIGURATION_DRIFT" => new("检查期间本机状态或配置发生变化。", "核实程序位置和当前环境，重新检查后再明确请求操作。"),
        "DAILY_MODE_REQUIRED" => new("本机仍处于考试模式，不能确认已返回日常。", "先在课堂模式或学校后台完整返回日常，再核实并解除远程录课暂停。"),
        "STORAGE_UNAVAILABLE" => new("考试状态或操作记录无法可靠读写。", "核对本机存储权限和剩余空间，保留原记录，处理后重新核实；不要删除记录来绕过保护。"),
        "RECOVERY_REQUIRED" => new("存在尚未核实的切换记录。", "先核实当前软件和设置，再由学校明确请求检查并补完切换。"),
        "HOST_INTERRUPTED" => new("后台曾中断，本次操作的最终结果未得到确认。", "先核实当前环境，再由学校发起新请求检查并补完；不会重放原操作。"),
        "TARGET_NOT_READY" => new("实际环境尚未满足请求的目标模式。", "核对 ExamAware2、ClassIsland 和自启动的实际状态，处理阻碍后重新请求核查。"),
        "UAC_CANCELLED" => new("本次 Windows 管理员授权被取消。", "核对操作与程序来源后，按需重新发起并完成管理员授权。"),
        "HISTORY_FULL" => new("本机考试操作记录已达到保存上限。", "保留操作记录，联系维护人员处理容量；不要直接删除未核实记录。"),
        "ACTION_FAILED" => new("软件切换步骤执行失败，尚不能确认目标环境。", "核对最后记录的步骤和现场软件提示，处理后再由学校明确请求核查。"),
        "OPERATION_NOT_FOUND" or "RESOLUTION_NOT_REQUIRED" => new("本次请求对应的远程录课暂停不存在或已解除。", "刷新当前状态，核对请求编号；无需据此重复解除其他操作。"),
        "RemoteExamUnavailable" or "MODE_CONTROL_UNAVAILABLE" => new("当前后台不支持这项考试操作。", "核对 App 与 Host 版本，正常保存并完整退出后从同一程序包重新启动。"),
        "PREFLIGHT_UNAVAILABLE" => new("未能完成本机环境检查。", "核对程序位置、桥接与后台连接，处理后重新检查；本次检查未请求软件切换。"),
        "LOCAL_CONTROL_UNAVAILABLE" => new("未能确认本机操作的最终结果。", "先刷新并核实当前暂停状态，不要直接重复解除操作。"),
        "INVALID_REQUEST" or "REQUEST_CONFLICT" => new("请求内容无效或与原请求记录不一致。", "保留请求编号与技术详情，刷新后由学校管理员明确发起新请求。"),
        _ => new("尚未取得可识别的具体原因。", "先核实当前状态，保留技术详情供排查；此处不会自动重试或认定操作成功。")
    };
}
