using System.Diagnostics;
using NPEduTools.Core;

namespace NPEduTools.Host;

public interface IExamAwareTarget
{
    string Validate(string path);
    void Open(string path, string? link);
    IExamAwareProcess Capture(int processId, string path);
}

public interface IExamAwareProcess : IDisposable { bool HasExited { get; } }

public sealed class ExamAwareTarget : IExamAwareTarget
{
    public static bool IsRunning(string path)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("ExamAware");
        bool found = false;
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    if (process.SessionId != current.SessionId ||
                        !string.Equals(ClassIslandLaunchTarget.ProcessPath(process), path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("检测到其他位置或会话的 ExamAware2，无法核实目标退出，请先检查。");
                    found = true;
                }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            return found;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private sealed class ObservedProcess(Process process) : IExamAwareProcess
    {
        public bool HasExited => process.HasExited;
        public void Dispose() => process.Dispose();
    }

    public IExamAwareProcess Capture(int processId, string path)
    {
        if (processId <= 0) throw new LaunchTargetException("PeerIdentityMissing", "请更新桥接插件后重新连接。");
        var process = Process.GetProcessById(processId);
        try
        {
            // Retain an OS handle to the exact process; PID reuse cannot confirm a different process.
            _ = process.Handle;
            using var current = Process.GetCurrentProcess();
            if (process.HasExited || process.SessionId != current.SessionId ||
                !string.Equals(ClassIslandLaunchTarget.ProcessPath(process), path, StringComparison.OrdinalIgnoreCase))
                throw new LaunchTargetException("PeerIdentityMismatch", "桥接进程与所选程序位置或当前会话不符，未发送退出请求。");
            return new ObservedProcess(process);
        }
        catch { process.Dispose(); throw; }
    }

    public string Validate(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.IndexOfAny(['"', '\r', '\n']) >= 0 || !Path.GetFileName(path).Equals("ExamAware.exe", StringComparison.OrdinalIgnoreCase))
            throw new LaunchTargetException("InvalidExecutable", "请选择本机 ExamAware.exe 的完整路径。");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new LaunchTargetException("ExecutableMissing", "ExamAware 程序不存在，请重新选择。");
        using var file = File.OpenRead(path);
        if (file.ReadByte() != 'M' || file.ReadByte() != 'Z' || FileVersionInfo.GetVersionInfo(path).ProductName != "ExamAware")
            throw new LaunchTargetException("InvalidExecutable", "所选文件不是可识别的 ExamAware 程序。");
        var version = FileVersionInfo.GetVersionInfo(path);
        if (version.FileMajorPart != 1 || version.FileMinorPart != 5 || version.FileBuildPart != 2)
            throw new LaunchTargetException("UnsupportedVersion", "当前适配 ExamAware2 1.5.2，请选择这一版本的正式程序。");
        // The product name alone cannot distinguish ExamAware 1 from 2.
        string asar = Path.Combine(Path.GetDirectoryName(path)!, "resources", "app.asar");
        if (!File.Exists(asar)) throw new LaunchTargetException("UnsupportedDistribution", "请选择 ExamAware2 正式发行版目录中的程序。");
        return path;
    }

    public void Open(string path, string? link)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName("ExamAware");
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    if (process.SessionId != current.SessionId ||
                        !string.Equals(ClassIslandLaunchTarget.ProcessPath(process), path, StringComparison.OrdinalIgnoreCase))
                        throw new LaunchTargetException("DifferentExamAwareInstance", "已有其他路径或会话的 ExamAware 运行，请先核对安装位置。");
                }
                catch (InvalidOperationException) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! };
        if (link is not null) start.ArgumentList.Add(link);
        // ExamAware's own single-instance handler restores the existing main window.
        using var started = Process.Start(start) ?? throw new IOException("启动进程失败。");
    }
}
