using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Text;

namespace NPEduTools.PowerPoint.Diagnostics;

public sealed record AssistStatus(string State, long Sent, long NativeAdvances, string? Error = null);

[SupportedOSPlatform("windows")]
public sealed class PowerPointTouchAssist
{
    private volatile bool _enabled = true, _allowUnmarked;
    public bool Enabled { get => _enabled; set => _enabled = value; }
    public bool AllowUnmarkedMouse { get => _allowUnmarked; set => _allowUnmarked = value; }
    public event Action<AssistStatus>? StatusChanged;
    public static int RunProbeWorker() { Native.SetProcessDpiAwarenessContext(-4); return ProbeSession.Worker(); }

    public async Task RunAsync(Func<ProcessStartInfo> workerStart, CancellationToken cancellationToken)
    {
        Native.SetProcessDpiAwarenessContext(-4);
        using var instance = new Mutex(false, $"Local\\NPEduTools.PowerPoint.TouchAssist.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}", out bool created);
        if (!created) throw new InvalidOperationException("触摸辅助已由另一个实例运行，请先停止该实例。");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var probe = new ProbeSession(workerStart);
        Task monitor = probe.RunAsync(_ => { }, stop.Token);
        try
        {
            using var observer = new InputObserver(probe);
            var gesture = new TouchAssistGesture();
            AssistTap? pending = null;
            long pendingFrame = 0, dropped = 0, errors = 0, sent = 0, native = 0;
            bool previousEnabled = _enabled, previousCompat = _allowUnmarked;
            AssistStatus? lastStatus = null;
            string? failure = null;
            while (!stop.IsCancellationRequested)
            {
                if (previousEnabled != _enabled || previousCompat != _allowUnmarked)
                {
                    gesture.Reset(); pending = null; failure = null;
                    previousEnabled = _enabled; previousCompat = _allowUnmarked;
                }
                gesture.AllowUnmarkedMouse = _allowUnmarked;
                if (observer.Dropped != dropped || observer.CallbackErrors != errors)
                {
                    dropped = observer.Dropped; errors = observer.CallbackErrors;
                    gesture.Reset(); pending = null;
                    while (observer.TryRead(out _)) { }
                }
                var frame = probe.Latest;
                bool fresh = Stopwatch.GetElapsedTime(frame.ReceivedAt).TotalMilliseconds <= 750;
                var target = fresh && frame.Snapshot.Status == "Showing" && frame.Snapshot.Windows.Length == 1 ? frame.Snapshot.Windows[0] : null;
                int batch = 0;
                while (batch++ < 2048 && observer.TryRead(out var sample))
                {
                    if (!_enabled || failure is not null) continue;
                    if (sample!.Kind is "Down" or "RightDown" or "Cancel") pending = null;
                    if (Environment.TickCount64 - sample.TimeMs > 500) { gesture.Reset(); continue; }
                    if (gesture.Accept(sample) is { } tap)
                    { pending = tap; pendingFrame = probe.Latest.ReceivedAt; }
                }
                if (!_enabled || target is null || !TouchAssistGesture.Ready(target)) { gesture.Reset(); pending = null; }
                if (pending is { } candidate)
                {
                    if (Environment.TickCount64 - candidate.ReleasedAt > 600) pending = null;
                    else if (frame.ReceivedAt > pendingFrame && target is not null)
                    {
                        pending = null;
                        if (TouchAssistGesture.AlreadyChanged(candidate.Before, target)) native++;
                        else if (_enabled && !stop.IsCancellationRequested && TargetStillActive(target, candidate.X, candidate.Y))
                        {
                            int result = PostSpace(target);
                            if (result == 0) sent++;
                            else { failure = result == 5 ? "权限不同，无法发送翻页消息" : "发送失败，已暂停；可关闭后重试"; }
                        }
                    }
                }
                string state = failure is not null ? "发送已暂停" : !_enabled ? "已暂停" : target is null ? "等待 PowerPoint 放映" :
                    !TouchAssistGesture.Ready(target) ? "书写或非普通放映，暂不辅助" : "轻点翻页已开启";
                var status = new AssistStatus(state, sent, native, failure);
                if (status != lastStatus) { lastStatus = status; StatusChanged?.Invoke(status); }
                await Task.Delay(10, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { stop.Cancel(); await monitor; }
    }

    internal static bool TargetStillActive(ShowTarget target, int x, int y)
    {
        nint hwnd = (nint)target.Hwnd;
        if (hwnd == 0 || Native.GetAncestor(Native.GetForegroundWindow(), 2) != hwnd ||
            Native.GetAncestor(Native.WindowFromPoint(new() { X = x, Y = y }), 2) != hwnd) return false;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != target.ProcessId) return false;
        var name = new StringBuilder(128);
        Native.GetClassNameW(hwnd, name, name.Capacity);
        if (name.ToString() != "screenClass") return false;
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            return process.ProcessName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime().Ticks == target.ProcessStartedUtcTicks;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    internal static int PostSpace(ShowTarget target)
    {
        // Address the show HWND rather than the global foreground keyboard queue.
        // 0x39 is the space scan code; one down/up pair, never an automatic retry.
        if (!Native.PostMessageW((nint)target.Hwnd, 0x100, 0x20, 0x00390001)) return Math.Max(1, Marshal.GetLastWin32Error());
        if (!Native.PostMessageW((nint)target.Hwnd, 0x101, 0x20, unchecked((nint)(int)0xc0390001))) return Math.Max(1, Marshal.GetLastWin32Error());
        return 0;
    }
}
