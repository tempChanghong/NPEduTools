using System.Runtime.InteropServices;

namespace NPEduTools.Core;

/// <summary>Reads only the current Windows session's input timestamp, without hooks or input content.</summary>
public sealed class DesktopInactivity(Func<(bool Success, uint Tick)>? input = null, Func<long>? uptime = null)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size, Tick; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput value);
    private readonly object _sync = new();
    private uint? _lastInput;
    private long? _observedChange;
    private static (bool, uint) NativeInput()
    {
        if (!OperatingSystem.IsWindows()) return (false, 0);
        var value = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return (GetLastInputInfo(ref value), value.Tick);
    }
    public TimeSpan? Read()
    {
        lock (_sync)
        {
            var observation = (input ?? NativeInput)();
            if (!observation.Success) return null;
            long now = (uptime ?? (() => Environment.TickCount64))();
            // The native DWORD wraps every 49.7 days. Future/inconsistent values are unknown,
            // not a claim that the user has been idle for 49 days.
            uint age = unchecked((uint)now - observation.Tick);
            if (age > int.MaxValue) return null;
            // Windows does not guarantee increasing input ticks (notably SendInput).
            // Any observed change therefore also restarts the locally observed idle interval.
            if (_lastInput is { } previous && previous != observation.Tick) _observedChange = now;
            _lastInput = observation.Tick;
            double milliseconds = age;
            if (_observedChange is { } changed) milliseconds = Math.Min(milliseconds, Math.Max(0, now - changed));
            return TimeSpan.FromMilliseconds(milliseconds);
        }
    }
}

/// <summary>A Host-owned monotonic interval, bounded by observed desktop inactivity.</summary>
public sealed class StudyIdleReturn(TimeProvider? time = null, Func<TimeSpan?>? inactivity = null)
{
    public const int DefaultSeconds = 600;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<TimeSpan?> _inactivity = inactivity ?? new DesktopInactivity().Read;
    private long? _started;
    private int _seconds;
    public bool Ready { get; private set; }
    public void Start(int seconds)
    {
        _seconds = Math.Clamp(seconds, 1, 10800);
        _started = _time.GetTimestamp(); Ready = false;
    }
    public void Clear() { _started = null; Ready = false; }
    public double Remaining
    {
        get
        {
            if (_started is not { } started) return 0;
            var idle = _inactivity();
            Ready = idle is { } value && value >= TimeSpan.Zero;
            if (!Ready) return _seconds; // Unknown input state must not interrupt an active user.
            double elapsed = Math.Max(0, _time.GetElapsedTime(started).TotalSeconds);
            double remaining = Math.Max(0, _seconds - Math.Min(elapsed, idle!.Value.TotalSeconds));
            if (remaining <= 0) Clear(); // Later input must never hide the already returned display again.
            return remaining;
        }
    }
}
