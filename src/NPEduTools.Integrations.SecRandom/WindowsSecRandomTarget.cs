using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NPEduTools.Integrations.SecRandom;

public static class WindowsSecRandomTarget
{
    public static string Validate(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new InvalidDataException("请选择本机 SecRandom V3 的完整程序路径。");
        path = Path.GetFullPath(path);
        string filename = Path.GetFileName(path);
        bool desktop = filename.Equals("SecRandom.Desktop.exe", StringComparison.OrdinalIgnoreCase);
        if ((!desktop && !filename.Equals("SecRandomLauncher.exe", StringComparison.OrdinalIgnoreCase)) || !File.Exists(path))
            throw new InvalidDataException("请选择 SecRandom.Desktop.exe 或 SecRandomLauncher.exe。");
        using var file = File.OpenRead(path);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("不是有效的 Windows 可执行文件。");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (desktop && info.FileMajorPart != 3)
            throw new InvalidDataException("此接入只支持 SecRandom V3。");
        return path;
    }

    internal static bool Matches(string configured, string peer)
    {
        if (string.Equals(configured, peer, StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(peer).Equals("SecRandom.Desktop.exe", StringComparison.OrdinalIgnoreCase);
        if (!Path.GetFileName(configured).Equals("SecRandomLauncher.exe", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(peer).Equals("SecRandom.Desktop.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var payload = Directory.GetParent(peer);
        return payload is not null && payload.Name.StartsWith("app-v3.", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(payload.Parent?.FullName, Path.GetDirectoryName(configured), StringComparison.OrdinalIgnoreCase);
    }

    public static void VerifyPeer(NamedPipeClientStream pipe, string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        executable = Validate(executable);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var peer = Process.GetProcessById(checked((int)id));
        using var current = Process.GetCurrentProcess();
        if (peer.SessionId != current.SessionId) throw new InvalidOperationException("Different session.");
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid || !OpenProcessToken(handle, 8, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var self = WindowsIdentity.GetCurrent())
            if (identity.User != self.User) throw new InvalidOperationException("Different user.");
        var path = new StringBuilder(32768); int length = path.Capacity;
        if (!QueryFullProcessImageName(handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!Matches(executable, path.ToString())) throw new InvalidOperationException("Different image.");
        Validate(path.ToString());
        if (peer.HasExited) throw new InvalidOperationException("Peer exited.");
    }

    public static bool IsRunning(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var current = Process.GetCurrentProcess();
        foreach (var peer in Process.GetProcessesByName("SecRandom.Desktop"))
            using (peer)
                if (!peer.HasExited && peer.SessionId == current.SessionId) return true;
        return false;
    }

    public static void Start(string executable)
    {
        executable = Validate(executable);
        if (IsRunning(executable)) return; // Verify the actual pipe peer before any mutation.
        using var process = Process.Start(new ProcessStartInfo(executable)
        { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true });
        if (process is null) throw new InvalidOperationException("SecRandom 未能启动。");
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint id);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
