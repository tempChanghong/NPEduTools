using System.Diagnostics;

namespace NPEduTools.Core;

/// <summary>
/// Cross-process readers/writer reservation for this user's desktop. Open handles are released
/// by Windows on process exit; no stale PID files, expiring leases or thread-affine mutexes.
/// Shared holders may perform ordinary mutations; an exclusive holder owns a runtime switch.
/// </summary>
public static class RuntimeOperationFile
{
    private static string PriorityPath
    {
        get
        {
            using var process = Process.GetCurrentProcess();
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NPEduTools", "coordination", $"runtime-session-{process.SessionId}.priority");
        }
    }
    public static IDisposable? RequestPriority()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PriorityPath)!);
        try { return new FileStream(PriorityPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return null; }
    }
    public static bool PriorityRequested
    {
        get
        {
            if (!File.Exists(PriorityPath)) return false;
            try { using var probe = new FileStream(PriorityPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return false; }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
        }
    }
    public static IDisposable? TryAcquire(bool exclusive)
    {
        if (!exclusive && PriorityRequested) return null;
        using var process = Process.GetCurrentProcess();
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NPEduTools", "coordination");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"runtime-session-{process.SessionId}.lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                exclusive ? FileShare.None : FileShare.ReadWrite);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { return null; }
    }
}
