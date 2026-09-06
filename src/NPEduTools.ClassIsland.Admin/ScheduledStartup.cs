using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NPEduTools.Core;

namespace NPEduTools.ClassIsland.Admin;

[SupportedOSPlatform("windows")]
public sealed partial class ScheduledStartup : IDisposable
{
    private readonly dynamic _service;
    private readonly dynamic _folder;
    public ScheduledStartup()
    {
        _service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        _service.Connect();
        _folder = _service.GetFolder("\\");
    }

    public static string? ResolveSid(string account)
    {
        try { return account.StartsWith("S-1-", StringComparison.Ordinal) ? new SecurityIdentifier(account).Value :
            ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value; }
        catch (Exception ex) when (ex is ArgumentException or IdentityNotMappedException) { return null; }
    }

    public (string? Xml, bool Enabled) Read()
    {
        object? task = null;
        try
        {
            task = _folder.GetTask(TaskDefinitionPolicy.TaskName);
            return (((dynamic)task).Xml, ((dynamic)task).Enabled);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return (null, false); }
        finally { if (task is not null) Marshal.FinalReleaseComObject(task); }
    }

    public AdminStatus Status(AdminRequest request)
    {
        var (xml, enabled) = Read();
        bool compatible = xml is null || TaskDefinitionPolicy.IsCompatible(xml, request.Executable, request.UserSid, ResolveSid);
        string taskState = xml is null ? "Missing" : !compatible ? "Conflict" : enabled ? "Enabled" : "Disabled";
        string taskMessage = taskState switch
        {
            "Missing" => "尚未创建管理员自启动任务", "Enabled" => "管理员自启动已启用", "Disabled" => "任务已存在，但已禁用",
            _ => "同名任务指向其他程序、用户或使用不同定义，不能直接修改"
        };
        string processState, processMessage;
        try
        {
            var process = ClassIslandProcess.Find(request.Executable, request.UserSid, request.SessionId);
            processState = process is null ? "Stopped" : process.Elevated ? "Administrator" : "Standard";
            processMessage = processState switch { "Stopped" => "ClassIsland 尚未运行", "Administrator" => "ClassIsland 正以管理员身份运行", _ => "ClassIsland 正以普通权限运行" };
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { processState = "Unknown"; processMessage = "暂时无法核实 ClassIsland 权限或实例，请使用“管理员启动／重启”进一步检查。"; }
        return new(taskState, taskMessage, TaskDefinitionPolicy.Fingerprint(xml), processState, processMessage,
            File.Exists(Path.Combine(Path.GetDirectoryName(request.Executable)!, "Plugins", "classisland.startUpAsAdmin", "manifest.yml")));
    }

    public AdminResult Execute(AdminRequest request)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != request.UserSid || System.Diagnostics.Process.GetCurrentProcess().SessionId != request.SessionId)
            return new("Rejected", "请使用当前登录用户授权，不能替其他账户配置或重启 ClassIsland。");
        if (request.Action is not ("status" or "inspect" or "create" or "delete" or "elevate" or "launch" or "launch-elevated")) return new("Rejected", "不支持的管理员操作。");
        string executable = ClassIslandExecutable.Validate(request.Executable);
        request = request with { Executable = executable };
        if (request.Action == "status") return new("Succeeded", "已读取状态。", Status(request));
        if (request.Action != "launch" && !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return new("Rejected", "此操作需要 Windows 管理员授权。");
        if (request.Action == "inspect") return new("Succeeded", "已使用管理员权限核实状态。", Status(request));
        using var gate = new Mutex(false, $@"Local\NPEduTools.ClassIsland.Operation.{request.UserSid}");
        bool acquired;
        try { acquired = gate.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return new("Rejected", "另一项 ClassIsland 管理员操作正在执行，请稍后刷新。");
        try
        {
            if (request.Action is "launch" or "launch-elevated") return Launch(request);
            if (request.Action == "elevate")
            {
                // Keep the mutex on this thread: synchronous wrapper around the bounded process workflow.
                string result = ClassIslandProcess.EnsureAdministratorAsync(executable, request.UserSid, request.SessionId).GetAwaiter().GetResult();
                return new("Succeeded", result, Status(request));
            }
            var (before, _) = Read();
            if (request.ExpectedFingerprint != TaskDefinitionPolicy.Fingerprint(before)) return new("Rejected", "计划任务已被其他程序更改，请刷新后再操作。", Status(request));
            if (before is not null && !TaskDefinitionPolicy.IsCompatible(before, executable, request.UserSid, ResolveSid))
                return new("Rejected", "同名任务不属于当前用户和所选程序，已保留原任务。", Status(request));
            if (request.Action == "create")
            {
                object registered = _folder.RegisterTask(TaskDefinitionPolicy.TaskName, TaskDefinitionPolicy.Create(executable, request.UserSid),
                    before is null ? 2 : 4, request.UserSid, null, 3, null); // CREATE or UPDATE, INTERACTIVE_TOKEN
                Marshal.FinalReleaseComObject(registered);
                var after = Status(request);
                if (after.TaskState != "Enabled") return new("Unknown", "任务写入后状态不符合预期，请刷新检查。", after);
                string shortcut = DisableMatchingShortcut(executable);
                return new("Succeeded", "已创建／更新管理员自启动任务，当前用户下次登录时生效。" + shortcut, after);
            }
            if (before is not null) _folder.DeleteTask(TaskDefinitionPolicy.TaskName, 0);
            var deleted = Status(request);
            return new(deleted.TaskState == "Missing" ? "Succeeded" : "Unknown", deleted.TaskState == "Missing"
                ? "管理员自启动任务已删除；当前 ClassIsland 继续运行。" : "未能确认任务已删除，请刷新检查。", deleted);
        }
        finally { gate.ReleaseMutex(); }
    }

    private static string DisableMatchingShortcut(string executable)
    {
        // Match the plugin's built-in startup behavior, but preserve unrelated or repointed shortcuts.
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "ClassIsland.lnk");
        if (!File.Exists(path)) return "";
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            link = ((dynamic)shell!).CreateShortcut(path);
            if (!string.Equals((string)((dynamic)link).TargetPath, executable, StringComparison.OrdinalIgnoreCase))
                return " 检测到其他位置的普通自启动快捷方式，已保留，请核对以免重复启动。";
            File.Delete(path);
            return " 已关闭同一程序的普通自启动，避免重复启动。";
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        { return " 普通自启动快捷方式未能清理，请在 ClassIsland 中关闭普通自启动。"; }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }

    public void Dispose() { Marshal.FinalReleaseComObject((object)_folder); Marshal.FinalReleaseComObject((object)_service); }
}
