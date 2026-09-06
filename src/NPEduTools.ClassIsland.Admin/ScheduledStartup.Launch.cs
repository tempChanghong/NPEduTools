using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NPEduTools.ClassIsland.Admin;

public sealed partial class ScheduledStartup
{
    private AdminResult Launch(AdminRequest request)
    {
        var status = Status(request);
        if (request.Action == "launch-elevated" && request.ExpectedFingerprint != status.Fingerprint)
            return new("Rejected", "计划任务已变化，请重新点击启动检查；尚未执行启动。", status);
        switch (ClassIslandLaunchPolicy.Choose(status.ProcessState, status.TaskState))
        {
            case LaunchChoice.Existing:
                return new("Succeeded", status.ProcessMessage + "，正在核实课程连接。", status);
            case LaunchChoice.OfferAdministratorRestart:
                return new("NeedsRestart", "ClassIsland 已以普通权限运行。可点击“管理员重启”切换权限；当前实例继续运行。", status);
            case LaunchChoice.Reject:
                return new("Rejected", status.ProcessState == "Unknown" ? status.ProcessMessage : status.TaskMessage + "，已停止启动。", status);
            case LaunchChoice.ScheduledTask:
                return RunTask(request, status);
            default:
                // An elevated retry must not silently switch from a task to a different launch route.
                if (request.Action == "launch-elevated") return new("Rejected", "管理员任务已不可用，请重新检查。", status);
                try
                {
                    EnsureNoInstance(request);
                    using var process = Process.Start(new ProcessStartInfo(request.Executable)
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(request.Executable)! });
                    if (process is null) return new("Failed", "系统未能启动 ClassIsland。", status);
                    return VerifyLaunched(request, false);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
                { return new("NeedsElevation", "该程序要求管理员权限，正在请求 Windows 授权。", status, "ExecutableRequiresElevation"); }
        }
    }

    private static void EnsureNoInstance(AdminRequest request)
    {
        if (ClassIslandProcess.Find(request.Executable, request.UserSid, request.SessionId) is not null)
            throw new InvalidOperationException("检查期间 ClassIsland 已启动，请重新核实当前实例。");
        if (Mutex.TryOpenExisting(@"Global\ClassIsland.Lock", out var instance))
        { instance.Dispose(); throw new InvalidOperationException("ClassIsland 的运行锁已被占用，请稍后重试。"); }
    }

    private AdminResult RunTask(AdminRequest request, AdminStatus status)
    {
        object? task = null, settings = null, definition = null, running = null, instances = null;
        bool requested = false;
        try
        {
            task = _folder.GetTask(TaskDefinitionPolicy.TaskName);
            string xml = ((dynamic)task).Xml;
            if (TaskDefinitionPolicy.Fingerprint(xml) != status.Fingerprint || !((dynamic)task).Enabled ||
                !TaskDefinitionPolicy.IsCompatible(xml, request.Executable, request.UserSid, ResolveSid))
                return new("Rejected", "计划任务在启动前发生变化，请刷新检查。", Status(request));
            definition = ((dynamic)task).Definition;
            settings = ((dynamic)definition).Settings;
            if (!((dynamic)settings).AllowDemandStart)
                return new("Rejected", "此任务不允许手动启动，请在自启动设置中更新任务。", status);
            instances = ((dynamic)task).GetInstances(0);
            if (((dynamic)instances).Count == 0 && (int)((dynamic)task).State != 2) // don't resubmit a queued/running task
            {
                EnsureNoInstance(request);
                requested = true;
                running = ((dynamic)task).RunEx(null, 4, request.SessionId, null); // TASK_RUN_USE_SESSION_ID
            }
            return VerifyLaunched(request, true);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
        { return new("NeedsElevation", "启动现有管理员任务需要 Windows 授权。", status, "TaskAccessDenied"); }
        catch (Exception ex) when (ex is COMException or Win32Exception or IOException or InvalidOperationException)
        { return new(requested ? "Unknown" : "Failed", requested
            ? "计划任务启动结果待核实，请刷新；不会改用其他方式重复启动。"
            : "无法运行管理员任务，请检查自启动设置：" + ex.Message, ErrorCode: $"TaskRun:{ex.HResult:X8}"); }
        finally
        {
            foreach (object? obj in new[] { instances, running, settings, definition, task })
                if (obj is not null) Marshal.FinalReleaseComObject(obj);
        }
    }

    private AdminResult VerifyLaunched(AdminRequest request, bool administratorRequired)
    {
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            var instance = ClassIslandProcess.Find(request.Executable, request.UserSid, request.SessionId);
            if (instance is null) continue;
            if (administratorRequired && !instance.Elevated)
                return new("Failed", "检测到普通权限实例，尚未确认管理员任务启动成功。", Status(request));
            return new("Succeeded", administratorRequired ? "已通过现有计划任务启动，并确认管理员权限。" : "ClassIsland 已启动，正在核实课程连接。", Status(request));
        }
        return new("Unknown", "尚未确认 ClassIsland 进程启动，可能仍在等待任务条件；不会自动重复启动。", Status(request));
    }
}
