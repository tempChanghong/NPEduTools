using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunColdTrayExitCheck()
    {
        string pipe = "NPEduTools.Test.cold-tray-exit." + Guid.NewGuid().ToString("N");
        using var hostMutex = new Mutex(false, @"Local\" + pipe + ".Host");
        using var stop = new CancellationTokenSource();
        var main = PrivateWorkspace(pipe);
        int sourceCount = 0, visibleCount = 0, beginCount = 0;
        bool preparedBeforeBegin = false, validDialog = false;
        main.SourceInitialized += (_, _) => Interlocked.Increment(ref sourceCount);
        main.IsVisibleChanged += (_, _) => { if (main.IsVisible) Interlocked.Increment(ref visibleCount); };
        var requests = new ConcurrentQueue<string>();
        var failures = new List<Exception>();
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        StudyExitState? exit = null;
        var dialogType = typeof(MainWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        var closeProbeDialog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        closeProbeDialog.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window =>
                window.GetType() == dialogType && window.IsVisible && ReferenceEquals(window.Owner, main));
            if (dialog is null) return;
            validDialog = !main.IsVisible && new WindowInteropHelper(main).Handle != 0;
            // Stop after observing the real modal; this probe never submits a
            // password, completes shutdown, or invokes a real Host/photo service.
            dialogType.GetField("_completed", WorkspaceFlags)!.SetValue(dialog, true);
            dialog.Close();
        };
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            requests.Enqueue(request.Capability);
            if (request.Capability == "study.exit.begin")
            {
                Interlocked.Increment(ref beginCount);
                preparedBeforeBegin = Volatile.Read(ref sourceCount) == 1 && Volatile.Read(ref visibleCount) == 0;
                exit = new(request.RequestId, "Waiting", null, 30, 0, "Disabled", "NotRequired");
            }
            return Task.FromResult(request.Capability switch
            {
                "study.exit.begin" or "study.exit.status" => new HostResponse(1, request.RequestId, "Succeeded", null,
                    "Synthetic exit verification", StudyExit: exit),
                "classroom.status" => new(1, request.RequestId, "Succeeded", null,
                    "Synthetic idle classroom mode", ClassroomMode: new()),
                "noise.management.status" => new(1, request.RequestId, "Succeeded", null,
                    "Synthetic protected session", NoiseProtection: new(true, true, instance, session, ManagementSource: "School")),
                _ => new(1, request.RequestId, "Rejected", "ProbeUnexpectedCapability", "No side effects implemented by this probe")
            });
        }, stop.Token));
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs args) { failures.Add(args.Exception); args.Handled = true; }
        Application.Current.DispatcherUnhandledException += Unhandled;
        try
        {
            Assert(!main.IsVisible && new WindowInteropHelper(main).Handle == 0,
                "cold tray fixture must use a never-shown main window");
            closeProbeDialog.Start();
            WorkspaceCall(main, "StopClicked", main, new RoutedEventArgs());
            PumpUntil(() => !(bool)typeof(MainWindow).GetField("_exitBusy", WorkspaceFlags)!.GetValue(main)!,
                "cold tray exit verification did not settle");
            Assert(failures.Count == 0 && validDialog, "cold tray exit never showed a valid password modal: " + string.Join("\n", failures));
            Assert(preparedBeforeBegin && beginCount == 1 && sourceCount == 1 && visibleCount == 0 && !main.IsVisible,
                "exit intent started before owner preparation, or verification exposed the hidden workspace");
            Assert(requests.All(capability => capability is "study.exit.begin" or "study.exit.status" or "classroom.status" or "noise.management.status"),
                "probe sent a password, stop or photo operation without authorization");
            Checks.Add("Cold tray stop: real MainWindow exit handler initializes its hidden owner before the timed intent, opens verification and leaves capture/backend untouched; private pipe only");
        }
        finally
        {
            closeProbeDialog.Stop();
            foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(window => window.GetType() == dialogType && ReferenceEquals(window.Owner, main)).ToArray())
            { dialogType.GetField("_completed", WorkspaceFlags)!.SetValue(dialog, true); dialog.Close(); }
            main.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Application.Current.DispatcherUnhandledException -= Unhandled;
        }
    }
}
