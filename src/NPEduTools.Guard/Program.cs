using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NPEduTools.Core;
using NPEduTools.Contracts;

namespace NPEduTools.Guard;

internal static class Program
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder information, uint length, out uint needed);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessShutdownParameters(uint level, uint flags);
    private static bool Interactive()
    {
        var desktop = OpenInputDesktop(0, false, 0x101); if (desktop == IntPtr.Zero) return false;
        try { var name = new StringBuilder(256); return GetUserObjectInformation(desktop, 2, name, 512, out _) && name.ToString() == "Default"; }
        finally { CloseDesktop(desktop); }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        // A Windows session number can change across logon; default task actions resolve it here.
        if (args.Length is 1 or 2 && args[0] == "--default-pipe")
            args = args.Length == 1 ? ["--pipe", PipeEndpoint.DefaultName] : ["--pipe", PipeEndpoint.DefaultName, args[1]];
        if (args.Length is not (2 or 3) || args[0] != "--pipe" || args[1].Length is 0 or > 200 || args[1].IndexOfAny(['/', '\\', ':', '"', '\r', '\n']) >= 0 ||
            args[1].StartsWith("--", StringComparison.Ordinal) || args.Length == 3 && args[2] is not ("--at-login" or "--maintenance")) return;
        string pipe = args[1];
        var files = GuardFiles.ForPipe(pipe);
        var registration = files.Read<GuardRegistration>("registration.json");
        if (registration is null || registration.Generation == Guid.Empty) return;
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (!string.Equals(registration.BundleDirectory, Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return;
        if (args.Length == 3 && args[2] == "--maintenance")
        {
            // An explicit maintenance command, never exposed by the client. Stop is persisted first,
            // so a live App/Host cannot re-register or relaunch the guardian during maintenance.
            files.Stop(registration.Generation, "Maintenance");
            new GuardTasks(root, pipe, registration.Upstream).Unregister();
            return;
        }
        if (args.Length == 3 && args[2] == "--at-login")
        {
            files.ResumeLogin(registration.Generation);
            if (!files.Stopped(registration.Generation)) new GuardTasks(root, pipe, registration.Upstream).Run("Guard");
            return; // One owning task for the resident process; logon is a short bootstrap only.
        }
        using var singleton = new Mutex(false, $@"Local\{pipe}.Guard", out bool created);
        if (!created) return;
        if (files.Stopped(registration.Generation)) return;
        var processes = new GuardProcesses(Path.Combine(root, "NPEduTools.App.exe"), Path.Combine(root, "Host", "NPEduTools.Host.exe"), pipe, registration.Upstream);
        var supervisor = new GuardSupervisor(files, registration, processes);
        // Receive the cancellable query before the App/Host (Windows default: 0x280).
        // This also guarantees our window receives the final cancel/confirm notification.
        if (!SetProcessShutdownParameters(0x3ff, 0)) return;
        var clock = Stopwatch.StartNew();
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        void Stop(string reason)
        {
            try { supervisor.Finish(reason); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally { timer.Stop(); context.ExitThread(); }
        }
        using var sessionWindow = new SessionWindow(pipe, Stop, supervisor.Suspend, supervisor.Resume, supervisor.BeginSessionEnd, supervisor.CancelSessionEnd);
        timer.Tick += (_, _) =>
        {
            try { if (!supervisor.Tick(clock.Elapsed.TotalSeconds, Interactive())) { timer.Stop(); context.ExitThread(); } }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Stop("GuardStorageUnavailable"); }
        };
        timer.Start(); Application.Run(context);
    }

    // Invisible top-level window receives shutdown/power broadcasts on the same thread as the timer.
    // A message-only window would miss these broadcasts.
    private sealed class SessionWindow : NativeWindow, IDisposable
    {
        private readonly Action<string> _stop;
        private readonly Action _suspend, _resume;
        private readonly Action _beginSession, _cancelSession;
        public SessionWindow(string pipe, Action<string> stop, Action suspend, Action resume, Action beginSession, Action cancelSession)
        {
            _stop = stop; _suspend = suspend; _resume = resume; _beginSession = beginSession; _cancelSession = cancelSession;
            CreateHandle(new CreateParams { Caption = "NPEduTools Guard " + pipe });
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0011)
            {
                try { _beginSession(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _stop("GuardStorageUnavailable"); }
                message.Result = (IntPtr)1; return;
            }
            if (message.Msg == 0x0016)
            {
                if (message.WParam != IntPtr.Zero) _stop("WindowsSessionEnding");
                else try { _cancelSession(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _stop("GuardStorageUnavailable"); }
            }
            if (message.Msg == 0x0218 && message.WParam.ToInt64() == 4) _suspend();
            if (message.Msg == 0x0218 && message.WParam.ToInt64() is 7 or 18) _resume();
            base.WndProc(ref message);
        }
        public void Dispose() => DestroyHandle();
    }
}
