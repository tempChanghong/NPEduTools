using System.Diagnostics;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed class ClassIslandLaunchTarget : IClassIslandLaunchTarget
{
    public string ValidateExecutable(string path)
    {
        try { return ValidateCore(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            throw new LaunchTargetException("ExecutableUnreadable", "无法读取所选程序，请检查文件及访问权限。");
        }
    }

    private static string ValidateCore(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length > 2048 || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new LaunchTargetException("InvalidExecutablePath", "请选择本机 ClassIsland 可执行文件的完整路径。");
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        if (!name.Equals("ClassIsland.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("ClassIsland.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            throw new LaunchTargetException("NotClassIslandExecutable", "请选择 ClassIsland.exe 或 ClassIsland.Desktop.exe。");
        if (!File.Exists(full)) throw new LaunchTargetException("ExecutableNotFound", "文件不存在，请重新选择 ClassIsland 程序。");
        using var file = File.OpenRead(full);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z' ||
            FileVersionInfo.GetVersionInfo(full).ProductName != "ClassIsland")
            throw new LaunchTargetException("NotClassIslandExecutable", "所选文件不是可识别的 ClassIsland 程序。");
        return full;
    }

    public bool IsRunning(string executablePath)
    {
        bool matched = false;
        using var current = Process.GetCurrentProcess();
        foreach (string name in new[] { "ClassIsland", "ClassIsland.Desktop" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        if (process.SessionId != current.SessionId ||
                            !string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase))
                            throw new LaunchTargetException("DifferentClassIslandInstance",
                                "已有其他位置或会话的 ClassIsland 正在运行，请核对路径后重试。");
                        matched = true;
                    }
                    catch (InvalidOperationException) { /* Exited while inspecting. */ }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        throw new LaunchTargetException("ProcessIdentityUnavailable", "无法核实已有 ClassIsland 实例，已停止启动以避免重复运行。");
                    }
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        if (!matched && Mutex.TryOpenExisting(@"Global\ClassIsland.Lock", out var instance))
        {
            instance.Dispose();
            throw new LaunchTargetException("ClassIslandInstanceBusy", "ClassIsland 已占用运行锁，暂时无法确认所属实例。");
        }
        return matched;
    }

    public int Start(string executablePath)
    {
        try
        {
            // Direct executable launch: no shell, custom command line, or automatic close/kill.
            using var process = Process.Start(new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            }) ?? throw new LaunchTargetException("ProcessStartFailed", "无法启动 ClassIsland。");
            return process.Id;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new LaunchTargetException("ProcessStartFailed", "系统未能启动 ClassIsland，请检查运行环境和权限。");
        }
    }
}
