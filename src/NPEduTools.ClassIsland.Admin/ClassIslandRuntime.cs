using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using NPEduTools.Core;

namespace NPEduTools.ClassIsland.Admin;

/// <summary>Current-process operations only. Does not instantiate Task Scheduler or read startup settings.</summary>
[SupportedOSPlatform("windows")]
public static class ClassIslandRuntime
{
    public static AdminResult Execute(AdminRequest request)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var current = Process.GetCurrentProcess();
        if (identity.User?.Value != request.UserSid || current.SessionId != request.SessionId)
            return new("Rejected", "操作必须属于当前用户和会话。", ErrorCode: "SESSION_MISMATCH");
        if (request.Action is not ("runtime-status" or "runtime-close"))
            return new("Rejected", "不支持的运行状态操作。", ErrorCode: "INVALID_ACTION");
        string path = ClassIslandExecutable.Validate(request.Executable);
        AdminStatus Status()
        {
            var process = ClassIslandProcess.Find(path, request.UserSid, request.SessionId);
            return new("NotRead", "此操作不读取自启动设置", null,
                process is null ? "Stopped" : process.Elevated ? "Administrator" : "Standard",
                process is null ? "ClassIsland 已停止" : "已核实当前用户的 ClassIsland 实例", false,
                process?.Id, process?.Started);
        }
        if (request.Action == "runtime-status") return new("Succeeded", "已读取进程状态。", Status());
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return new("Rejected", "退出操作需要管理员执行上下文。", ErrorCode: "HOST_NOT_ELEVATED");
        if (request.ExpectedProcessId is not > 0 || request.ExpectedProcessStarted is not > 0)
            return new("Rejected", "必须指定已经核实的进程实例。", ErrorCode: "PROCESS_IDENTITY_REQUIRED");
        using var gate = new Mutex(false, $@"Local\NPEduTools.ClassIsland.Operation.{request.UserSid}");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return new("Rejected", "另一项 ClassIsland 操作正在执行。", ErrorCode: "OPERATION_BUSY");
        try
        {
            ClassIslandProcess.EnsureStoppedAsync(path, request.UserSid, request.SessionId,
                new(request.ExpectedProcessId.Value, request.ExpectedProcessStarted.Value, false)).GetAwaiter().GetResult();
            return new("Succeeded", "已确认 ClassIsland 正常退出。", Status());
        }
        finally { gate.ReleaseMutex(); }
    }
}
