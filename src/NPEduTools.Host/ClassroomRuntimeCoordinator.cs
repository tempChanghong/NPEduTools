using NPEduTools.Contracts;

namespace NPEduTools.Host;

public interface IClassroomRuntimeActions
{
    Task ValidateAsync(ClassroomStartupSnapshot expected);
    Task PrepareExamAsync(ClassroomStartupSnapshot expected);
    Task CloseClassIslandAsync(ClassroomStartupSnapshot expected);
    Task CloseExamAsync(ClassroomStartupSnapshot expected);
    Task StartClassIslandAsync(ClassroomStartupSnapshot expected);
    Task VerifyAsync(ClassroomStartupSnapshot expected, string target);
}

/// <summary>Every retry observes current processes. An unanswered quit never permits the next start.</summary>
public sealed class ClassroomRuntimeCoordinator(IClassroomRuntimeActions actions)
{
    public async Task RunAsync(ClassroomRuntimeIntent intent, Action<string, string> progress)
    {
        await actions.ValidateAsync(intent.Startup);
        if (intent.Target == "Exam")
        {
            progress("PrepareExam", "正在确认 ExamAware2 桥接及进程就绪…");
            await actions.PrepareExamAsync(intent.Startup);
            progress("CloseClassIsland", "考试看板已就绪，正在请求 ClassIsland 正常退出；可能需要管理员授权。");
            await actions.CloseClassIslandAsync(intent.Startup);
        }
        else if (intent.Target == "Daily")
        {
            progress("CloseExam", "正在等待 ExamAware2 正常退出；请保存并关闭编辑器、结束放映。");
            await actions.CloseExamAsync(intent.Startup);
            progress("StartClassIsland", "考试看板已退出，正在通过管理员任务启动 ClassIsland…");
            await actions.StartClassIslandAsync(intent.Startup);
        }
        else throw new InvalidOperationException("即时切换目标无效。");
        progress("Verify", "正在核实目标软件就绪及另一款软件已退出…");
        await actions.VerifyAsync(intent.Startup, intent.Target);
    }
}

