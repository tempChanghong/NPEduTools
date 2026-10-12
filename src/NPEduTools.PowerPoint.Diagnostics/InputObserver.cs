using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NPEduTools.PowerPoint.Diagnostics;

[SupportedOSPlatform("windows")]
internal sealed class InputObserver : IDisposable
{
    private readonly ProbeSession _session;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly TouchInputBuffer _events = new();
    private Exception? _error;
    private long _callbackErrors;
    private uint _threadId;
    public long Dropped => _events.Dropped;
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);
    public bool TryRead(out InputSample? input) => _events.TryRead(Environment.TickCount64, HitTest, out input);
    public void Discard() => _events.Discard();

    private static bool HitTest(ShowTarget target, int x, int y)
    {
        // WindowFromPoint can send WM_NCHITTEST to another UI thread. Keep it out of OnInput:
        // global mouse/touch delivery must never wait for a PowerPoint/WPF window to respond.
        nint hit = Native.GetAncestor(Native.WindowFromPoint(new() { X = x, Y = y }), 2);
        if (hit.ToInt64() != target.Hwnd) return false;
        Native.GetWindowThreadProcessId(hit, out uint pid);
        return pid == target.ProcessId;
    }

    public InputObserver(ProbeSession session)
    {
        _session = session;
        _thread = new Thread(Run) { IsBackground = true, Name = "PowerPoint input observer" };
        _thread.Start();
        if (!_ready.Wait(3000)) { Dispose(); throw new TimeoutException("Input hook initialization timed out."); }
        if (_error is not null) { Dispose(); throw new InvalidOperationException("Input hook unavailable.", _error); }
    }

    private void Run()
    {
        Native.HookCallback callback = OnInput;
        nint hook = 0;
        try
        {
            // Create the queue before publishing the thread ID so Dispose can wake GetMessage.
            Native.PeekMessageW(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, Native.GetCurrentThreadId());
            hook = Native.SetWindowsHookExW(14, callback, Native.GetModuleHandleW(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            while (!_stop.IsCancellationRequested && Native.GetMessageW(out var message, 0, 0, 0) > 0)
            { Native.TranslateMessage(ref message); Native.DispatchMessageW(ref message); }
        }
        catch (Exception error) { _error = error; _ready.Set(); }
        finally
        {
            if (hook != 0) Native.UnhookWindowsHookEx(hook);
            GC.KeepAlive(callback);
        }
    }

    private nint OnInput(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0)
            {
                var frame = _session.Latest;
                // No show (including an open editor), unavailable probe or untracked mouse move:
                // pass straight through without even querying the foreground/window tree.
                if (_events.Interested(message, frame))
                {
                    var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                    long now = Environment.TickCount64;
                    _events.Observe(message, mouse.Point.X, mouse.Point.Y, (ulong)mouse.ExtraInfo, mouse.Flags,
                        TouchInputBuffer.EventTime(now, mouse.Time), frame, Native.GetAncestor(Native.GetForegroundWindow(), 2).ToInt64());
                }
            }
        }
        catch (Exception) { Interlocked.Increment(ref _callbackErrors); }
        // Observation only: every event, including failures, continues along the original input path.
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        _stop.Cancel();
        uint threadId = Volatile.Read(ref _threadId);
        if (threadId != 0) Native.PostThreadMessageW(threadId, 0x12, 0, 0); // WM_QUIT wakes a blocking message loop.
        _thread.Join(2000);
        // Do not dispose the signals if an OS callback has not returned yet.
        if (!_thread.IsAlive) { _ready.Dispose(); _stop.Dispose(); }
    }
}
