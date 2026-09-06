using System.Diagnostics;
using NPEduTools.Core;

namespace NPEduTools.Host;

public sealed class ClassIslandLaunchTarget : IClassIslandLaunchTarget
{
    public string ValidateExecutable(string path) => ClassIslandExecutable.Validate(path);

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
