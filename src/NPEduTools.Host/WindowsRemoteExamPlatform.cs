using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Core;

namespace NPEduTools.Host;

[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteExamPlatform : IRemoteExamPlatform
{
    public bool HostElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
    public bool DesktopAvailable
    {
        get
        {
            if (!Environment.UserInteractive) return false;
            var desktop = OpenInputDesktop(0, false, 0x0100);
            if (desktop == IntPtr.Zero) return false;
            CloseDesktop(desktop);
            return true;
        }
    }
    public void ValidateConfiguration(RemoteExamConfiguration config)
    {
        try
        {
            ClassIslandExecutable.Validate(config.ClassIslandPath);
        }
        catch (LaunchTargetException error)
        { throw new RemoteExamException("CLASSISLAND_EXECUTABLE_INVALID", "ClassIsland：" + error.Message + " 请在 ClassIsland 设置中检查并保存程序位置。"); }
        try
        {
            new ExamAwareTarget().Validate(config.ExamAwarePath);
        }
        catch (LaunchTargetException error)
        { throw new RemoteExamException("EXAMAWARE_EXECUTABLE_INVALID", "ExamAware2：" + error.Message + " 请在考试看板中检查并保存程序位置。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { throw new RemoteExamException("EXAMAWARE_EXECUTABLE_UNREADABLE", "无法读取 ExamAware2 程序文件，请检查已保存的位置和文件访问权限。"); }
    }
    public bool ExamAwareRunning(string path)
    {
        try { return ExamAwareTarget.IsRunning(path); }
        catch (Exception error) when (error is IOException or InvalidOperationException or
            System.ComponentModel.Win32Exception or LaunchTargetException or UnauthorizedAccessException)
        { throw new RemoteExamException("EXAMAWARE_IDENTITY_UNAVAILABLE"); }
    }
    public RemoteExamProcess InspectClassIsland(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        try
        {
            var instance = ClassIslandProcess.Find(path, identity.User!.Value, process.SessionId);
            return new(instance is null ? "Stopped" : instance.Elevated ? "Administrator" : "Standard",
                instance?.Id, instance?.Started);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new RemoteExamException("CLASSISLAND_IDENTITY_UNAVAILABLE"); }
    }
    public async Task CloseClassIslandAsync(string path, RemoteExamProcess instance, Func<Task> beforeDispatch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HostElevated) throw new RemoteExamException("HOST_NOT_ELEVATED");
        var response = await AdminClient.RunAsync(
            Path.Combine(AppContext.BaseDirectory, "Admin", "NPEduTools.ClassIsland.Admin.exe"), "runtime-close", path,
            beforeDispatch: beforeDispatch, expectedProcessId: instance.ProcessId, expectedProcessStarted: instance.Started);
        token.ThrowIfCancellationRequested();
        if (response.Outcome != "Succeeded" || response.Status?.ProcessState != "Stopped")
            throw new RemoteExamException(response.Outcome == "Cancelled" ? "UAC_CANCELLED" :
                response.Outcome == "Unknown" ? "CLASSISLAND_EXIT_UNKNOWN" : "CLASSISLAND_EXIT_FAILED");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
}
