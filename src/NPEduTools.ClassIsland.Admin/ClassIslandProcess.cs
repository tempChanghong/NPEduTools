using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NPEduTools.ClassIsland.Admin;

[SupportedOSPlatform("windows")]
public static class ClassIslandProcess
{
    public sealed record Instance(int Id, long Started, bool Elevated);

    public static string UserSid(int processId)
    {
        using var handle = OpenProcess(0x1000, false, processId);
        if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User!.Value;
    }

    public static Instance? Find(string executable, string sid, int session)
    {
        Instance? found = null;
        foreach (string name in new[] { "ClassIsland", "ClassIsland.Desktop" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        // Other sessions cannot receive this user's restart operation.
                        if (process.SessionId != session) throw new InvalidOperationException("其他会话中已有 ClassIsland，无法安全重启当前实例。");
                        using var handle = OpenProcess(0x1000, false, process.Id); // QUERY_LIMITED_INFORMATION
                        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        var path = new StringBuilder(32768); int length = path.Capacity;
                        if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (!string.Equals(path.ToString(), executable, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("其他位置的 ClassIsland 已在运行，请先核对程序路径。");
                        if (!OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        using (token)
                        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                        {
                            if (identity.User?.Value != sid) throw new InvalidOperationException("ClassIsland 属于其他用户，不能操作该实例。");
                            if (!GetTokenInformation(token, 20, out int elevated, sizeof(int), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (!GetProcessTimes(handle, out long started, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                            if (found is not null) throw new InvalidOperationException("检测到多个 ClassIsland 实例，暂不重启。");
                            found = new(process.Id, started, elevated != 0);
                        }
                    }
                    catch (Win32Exception) when (process.HasExited) { }
                }
            }
        }
        return found;
    }

    public static async Task<string> EnsureAdministratorAsync(string executable, string sid, int session)
    {
        var existing = Find(executable, sid, session);
        if (existing?.Elevated == true) return "ClassIsland 已以管理员身份运行，无需重启。";
        if (existing is not null)
        {
            RequestNormalExit(existing);
            for (int i = 0; i < 50 && Find(executable, sid, session) is not null; i++) await Task.Delay(200);
            if (Find(executable, sid, session) is not null) throw new InvalidOperationException("ClassIsland 尚未退出，已停止后续启动。");
        }
        if (Mutex.TryOpenExisting(@"Global\ClassIsland.Lock", out var mutex))
        { mutex.Dispose(); throw new InvalidOperationException("ClassIsland 运行锁尚未释放，请稍后重试。"); }
        // This narrowly scoped worker is already elevated by UAC. The interactive app inherits its token.
        using var launched = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true });
        if (launched is null) throw new InvalidOperationException("ClassIsland 启动失败。");
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(200);
            if (Find(executable, sid, session) is { Elevated: true }) return existing is null
                ? "已启动 ClassIsland，并确认进程具有管理员权限。" : "已正常退出原实例，并以管理员身份重新启动 ClassIsland。";
        }
        throw new InvalidOperationException("尚未确认管理员实例启动成功，请刷新核实；不会自动重复启动。");
    }

    internal static void RequestNormalExit(Instance instance)
        => RequestNormalExitCore(instance);

    public static async Task EnsureStoppedAsync(string executable, string sid, int session)
    {
        var existing = Find(executable, sid, session);
        if (existing is null) return;
        RequestNormalExitCore(existing);
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(200);
            var current = Find(executable, sid, session);
            if (current is null) return;
            if (current.Id != existing.Id || current.Started != existing.Started)
                throw new InvalidOperationException("ClassIsland 在退出期间重新启动，请检查后重试；未向新实例发送退出请求。");
        }
        throw new InvalidOperationException("ClassIsland 尚未退出，请关闭弹窗或处理未保存内容后重试；不会强制结束进程。");
    }

    private static void RequestNormalExitCore(Instance instance)
    {
        using var process = OpenProcess(0x1000, false, instance.Id);
        if (process.IsInvalid || !GetProcessTimes(process, out long started, out _, out _, out _) || started != instance.Started)
            throw new InvalidOperationException("ClassIsland 实例已发生变化，请刷新后重试。");
        var windows = new List<nint>();
        EnumWindows((hwnd, _) => { GetWindowThreadProcessId(hwnd, out uint pid); if (pid == instance.Id) windows.Add(hwnd); return true; }, 0);
        if (windows.Count is 0 or > 32) throw new InvalidOperationException("无法定位 ClassIsland 的正常退出窗口，未执行重启。");
        var asked = new List<nint>();
        bool consent = false;
        try
        {
            foreach (nint hwnd in windows)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != instance.Id) continue;
                asked.Add(hwnd);
                // ENDSESSION_CLOSEAPP, never ENDSESSION_CRITICAL. A refusal or a hung window aborts.
                if (SendMessageTimeout(hwnd, 0x11, 0, 1, 2, 1000, out nint answer) == 0 || answer == 0)
                    throw new InvalidOperationException("ClassIsland 拒绝退出或尚未响应，已取消重启；不会强制结束进程。");
            }
            consent = asked.Count > 0;
        }
        finally
        {
            foreach (nint hwnd in asked)
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == instance.Id) SendMessageTimeout(hwnd, 0x16, consent ? 1u : 0u, 1, 2, 500, out _);
            }
        }
    }

    private delegate bool WindowCallback(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeout(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, int length, out int required);
}
