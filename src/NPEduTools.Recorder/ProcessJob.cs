using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NPEduTools.Recorder;

internal sealed class ProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;
    public ProcessJob()
    {
        _handle = CreateJobObject(0, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (_handle.IsInvalid || !SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { _handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }
    public void Add(Process process)
    {
        if (AssignProcessToJobObject(_handle, process.Handle)) return;
        int error = Marshal.GetLastWin32Error();
        if (!process.HasExited) process.Kill(true);
        throw new Win32Exception(error, "无法建立录制进程清理保护。");
    }
    public void Dispose() => _handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long PerProcess, PerJob; public uint Flags; public nuint MinWorking, MaxWorking; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
