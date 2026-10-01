using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NPEduTools.ClassIsland.Admin;

namespace NPEduTools.App;

internal static class StartupElevation
{
    // Keep an asInvoker bootstrap so the existing Windows login entry can start it. All UI,
    // credentials and workers are initialized only after this check passes in the elevated copy.
    public static bool Ensure(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序位置。");
        if (!Path.GetFileName(executable).Equals("NPEduTools.App.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请通过 NPEduTools.App.exe 启动应用，或使用管理员权限的 Visual Studio 调试。");
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        try { using var process = Process.Start(start) ?? throw new InvalidOperationException("未能启动管理员实例。"); }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223) { } // Cancellation exits; no ordinary-mode fallback.
        return false;
    }

    public static async Task CheckExistingHostAsync(string pipeName)
    {
        if (!Mutex.TryOpenExisting($@"Local\{pipeName}.Host", out var instance)) return;
        instance.Dispose();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        // Explicitly verify SID/session/PID instead of CurrentUserOnly's token-owner comparison,
        // which differs across the standard/admin boundary. No operation is sent to this pipe.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) throw new IOException("无法核实已有后台身份。");
        using var identity = WindowsIdentity.GetCurrent();
        using var server = Process.GetProcessById(checked((int)pid));
        using var current = Process.GetCurrentProcess();
        if (server.SessionId != current.SessionId || ClassIslandProcess.UserSid(server.Id) != identity.User?.Value)
            throw new InvalidOperationException("已有后台不属于当前用户或会话，请先核实。");
        if (!ClassIslandProcess.IsElevated(server.Id))
            throw new InvalidOperationException("已有普通权限的 NPEduTools 后台，不能直接复用。请先完成录制及正在进行的操作，在旧程序托盘中选择“停止后台并退出”，然后重新启动。程序不会强制结束后台或录制。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
