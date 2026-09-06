using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;

namespace NPEduTools.PowerPoint.Diagnostics;

[SupportedOSPlatform("windows")]
internal sealed class InputObserver : IDisposable
{
    private readonly ProbeSession _session;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly Channel<InputSample> _events = Channel.CreateBounded<InputSample>(2048);
    private Exception? _error;
    private long _dropped, _callbackErrors;
    private bool _tracking;
    public long Dropped => Interlocked.Read(ref _dropped);
    public long CallbackErrors => Interlocked.Read(ref _callbackErrors);
    public bool TryRead(out InputSample? input) => _events.Reader.TryRead(out input);

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
            hook = Native.SetWindowsHookExW(14, callback, Native.GetModuleHandleW(null), 0);
            if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ready.Set();
            while (!_stop.IsCancellationRequested) { Native.Pump(); Thread.Sleep(5); }
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
                string? kind = message switch { 0x201 => "Down", 0x202 => "Up", 0x200 when _tracking => "Move", 0x204 => "RightDown", _ => null };
                if (kind is not null)
                {
                    var mouse = Marshal.PtrToStructure<Native.MouseData>(data);
                    var frame = _session.Latest;
                    ShowTarget? target = null;
                    if (Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds <= 750 && frame.Snapshot.Status == "Showing")
                    {
                        nint foreground = Native.GetAncestor(Native.GetForegroundWindow(), 2);
                        nint hit = Native.GetAncestor(Native.WindowFromPoint(mouse.Point), 2);
                        if (foreground == hit)
                            target = frame.Snapshot.Windows.FirstOrDefault(x => x.Hwnd == hit.ToInt64() &&
                                mouse.Point.X >= x.Left && mouse.Point.X < x.Right && mouse.Point.Y >= x.Top && mouse.Point.Y < x.Bottom);
                        if (target is not null)
                        {
                            Native.GetWindowThreadProcessId(hit, out uint pid);
                            if (pid != target.ProcessId) target = null;
                        }
                    }
                    bool wasTracking = _tracking;
                    if (kind == "Down" && target is not null) _tracking = true;
                    if (kind is "Up" or "RightDown" || target is null) _tracking = false;
                    if (target is not null || wasTracking)
                    {
                        // Events outside the show are represented only as cancellation, with no coordinates.
                        var sample = new InputSample(Environment.TickCount64, target is null ? "Cancel" : kind,
                            target is null ? 0 : mouse.Point.X, target is null ? 0 : mouse.Point.Y,
                            target is null ? 0 : (ulong)mouse.ExtraInfo, target is null ? 0 : mouse.Flags, target);
                        if (!_events.Writer.TryWrite(sample)) Interlocked.Increment(ref _dropped);
                    }
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
        _thread.Join(2000);
        // Do not dispose the signals if an OS callback has not returned yet.
        if (!_thread.IsAlive) { _ready.Dispose(); _stop.Dispose(); }
    }
}
