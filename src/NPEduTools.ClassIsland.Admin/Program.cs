using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NPEduTools.Contracts;

namespace NPEduTools.ClassIsland.Admin;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || !args[0].StartsWith("NPEduTools.Admin.", StringComparison.Ordinal) ||
            !Guid.TryParseExact(args[0]["NPEduTools.Admin.".Length..], "N", out _) || !int.TryParse(args[1], out int parent)) return 2;
        // CurrentUserOnly on the client compares token OWNER (Administrators after elevation),
        // not user SID. Keep the server's user-only ACL and explicitly verify its PID + token user.
        // Identification prevents the unelevated pipe server from impersonating this elevated token.
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        // A wedged COM call is isolated in this worker and never blocks the desktop indefinitely.
        using var watchdog = new Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(65), Timeout.InfiniteTimeSpan);
        try
        {
            pipe.Connect(8000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parent) return 3;
            using var identity = WindowsIdentity.GetCurrent();
            if (ClassIslandProcess.UserSid(parent) != identity.User?.Value) return 3;
            var request = Protocol.ReadAsync<AdminRequest>(pipe, deadline.Token).GetAwaiter().GetResult();
            AdminResult result;
            try
            {
                using var service = new ScheduledStartup();
                result = service.Execute(request);
            }
            catch (Exception ex) when (ex is COMException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or NPEduTools.Core.LaunchTargetException)
            {
                result = new("Failed", ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005)
                    ? "权限不足，无法确认或修改任务；请请求管理员授权后重试。"
                    : "操作未完成：" + ex.Message);
            }
            Protocol.WriteAsync(pipe, result, deadline.Token).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.Text.Json.JsonException) { return 4; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
}
