using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Contracts;

namespace NPEduTools.ClassIsland.Admin;

[SupportedOSPlatform("windows")]
public static class AdminClient
{
    public static async Task<AdminResult> RunAsync(string helper, string action, string executable, string? fingerprint = null)
    {
        string name = "NPEduTools.Admin." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        bool elevate = action != "status";
        var start = new ProcessStartInfo(helper)
        {
            UseShellExecute = elevate, Verb = elevate ? "runas" : "", CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper)!
        };
        start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process? worker = null;
        bool dispatched = false;
        try
        {
            worker = await Task.Run(() => Process.Start(start));
            if (worker is null) return new("Failed", "无法启动管理员操作组件。");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(elevate ? 70 : 12));
            var connect = server.WaitForConnectionAsync(deadline.Token);
            var exited = worker.WaitForExitAsync(deadline.Token);
            if (await Task.WhenAny(connect, exited) == exited && !server.IsConnected)
                return new("Failed", "管理员组件未能建立连接。请使用当前登录用户进行授权。");
            await connect;
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out uint client) || client != worker.Id)
                return new("Failed", "无法核实管理员组件身份，未发送操作。");
            using var identity = WindowsIdentity.GetCurrent();
            var request = new AdminRequest(action, executable, identity.User!.Value, Process.GetCurrentProcess().SessionId, fingerprint);
            dispatched = true;
            await Protocol.WriteAsync(server, request, deadline.Token);
            var result = await Protocol.ReadAsync<AdminResult>(server, deadline.Token);
            await worker.WaitForExitAsync(deadline.Token);
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { return new("Cancelled", "已取消 Windows 管理员授权，未执行更改或重启。"); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
        { return new(dispatched ? "Unknown" : "Failed", dispatched
            ? "未能确认操作结果，请刷新状态核实；不会自动重复执行。"
            : "管理员组件未能连接，尚未发送操作。请重试或检查系统权限。", ErrorCode: $"{ex.GetType().Name}:{ex.HResult:X8}"); }
        finally
        {
            // This is only our dedicated worker; never terminate ClassIsland or its children here.
            if (worker is not null)
            {
                try { if (!worker.HasExited) worker.Kill(); } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                worker.Dispose();
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
