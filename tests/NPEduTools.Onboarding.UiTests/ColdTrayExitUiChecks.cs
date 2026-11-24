using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunColdTrayExitCheck()
    {
        foreach (string scenario in new[] { "Cancel", "Busy", "ClassroomUnknown", "RecordingUnknown", "CredentialUnavailable", "Verify", "PrepareReplyLost" })
            RunColdTrayExitScenario(scenario);
    }
    private static void RunColdTrayExitScenario(string scenario)
    {
        string pipe = "NPEduTools.Test.cold-tray-exit." + Guid.NewGuid().ToString("N");
        using var hostMutex = new Mutex(false, @"Local\" + pipe + ".Host");
        using var stop = new CancellationTokenSource();
        var main = PrivateWorkspace(pipe);
        bool losePrepareReply = scenario == "PrepareReplyLost";
        int sourceCount = 0, visibleCount = 0, beginCount = 0;
        bool preparedBeforeBegin = false, validDialog = false, confirmed = false;
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
            var confirmation = Application.Current.Windows.OfType<StudyActionConfirmation>().FirstOrDefault(window => window.IsVisible);
            if (confirmation is not null)
            {
                try
                {
                    Assert(!confirmed && ReferenceEquals(confirmation.Owner, main) && !main.IsVisible && confirmation.Topmost &&
                        requests.All(capability => losePrepareReply && (capability is "recording.status" or "recording.automatic")),
                        "tray confirmation must appear before initiating exit without opening the main window");
                    var cancel = Button(confirmation, "CancelAction");
                    Assert(cancel.IsDefault && cancel.IsCancel && cancel.IsFocused, "safe confirmation must default to cancel");
                    Assert(((TextBlock)confirmation.FindName("EffectText")).Text.Contains("自动录课会关闭"), "exit omitted the automatic recording consequence");
                    confirmed = true;
                    if (scenario == "Cancel") { Snapshot(confirmation, "tray-exit-confirmation.png"); confirmation.Close(); }
                    else Click(confirmation, "ConfirmAction");
                }
                catch (Exception error) { failures.Add(error); confirmation.Close(); }
                return;
            }
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window =>
                window.GetType() == dialogType && window.IsVisible && ReferenceEquals(window.Owner, main));
            if (dialog is null) return;
            validDialog = !main.IsVisible && new WindowInteropHelper(main).Handle != 0 && dialog.Topmost &&
                dialog.WindowStartupLocation == WindowStartupLocation.CenterScreen;
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
                exit = new(request.RequestId, losePrepareReply ? "NotRequired" : "Waiting", null, 30, 0, "Disabled", "NotRequired");
            }
            if (request.Capability == "noise.management.prepare-exit" && losePrepareReply)
                throw new IOException("Drop the response after the synthetic Host accepts prepare-exit");
            return Task.FromResult(request.Capability switch
            {
                "study.exit.begin" or "study.exit.status" => new HostResponse(1, request.RequestId, "Succeeded", null,
                    "Synthetic exit verification", StudyExit: exit),
                "classroom.status" when scenario != "ClassroomUnknown" => new(1, request.RequestId, "Succeeded", null,
                    "Synthetic classroom mode", ClassroomMode: new(Phase: scenario == "Busy" ? "Switching" : "Idle")),
                "recording.status" when scenario != "RecordingUnknown" => new(1, request.RequestId, "Succeeded", null,
                    "Synthetic idle recording status", Recording: new("Idle", "fixture"), Automatic: new(false, Guid.Empty, "fixture", null, [])),
                "recording.automatic" when losePrepareReply && request.Automatic?.Action == "lease" => new(1, request.RequestId, "Succeeded", null, "Synthetic recording lease"),
                "noise.management.status" => new(1, request.RequestId, "Succeeded", null,
                    "Synthetic protected session", NoiseProtection: new(!losePrepareReply, scenario != "CredentialUnavailable", instance, session, ManagementSource: "School")),
                _ => new(1, request.RequestId, "Rejected", "ProbeUnexpectedCapability", "No side effects implemented by this probe")
            });
        }, stop.Token));
        void Unhandled(object sender, DispatcherUnhandledExceptionEventArgs args) { failures.Add(args.Exception); args.Handled = true; }
        Application.Current.DispatcherUnhandledException += Unhandled;
        try
        {
            Assert(!main.IsVisible && new WindowInteropHelper(main).Handle == 0,
                "cold tray fixture must use a never-shown main window");
            if (losePrepareReply)
            {
                WorkspaceCall(main, "InitializeRecording");
                var recording = (RecordingClient)typeof(MainWindow).GetField("_recording", WorkspaceFlags)!.GetValue(main)!;
                PumpUntil(() => recording.StateAvailable && recording.AutomaticAvailable, "recording client did not receive initial state");
            }
            closeProbeDialog.Start();
            WorkspaceCall(main, "StopClicked", main, new RoutedEventArgs());
            PumpUntil(() => !(bool)typeof(MainWindow).GetField("_exitBusy", WorkspaceFlags)!.GetValue(main)!,
                "cold tray exit verification did not settle");
            Assert(failures.Count == 0 && confirmed, "cold tray confirmation did not settle: " + string.Join("\n", failures));
            if (losePrepareReply)
            {
                var feedback = ((TextBlock)main.FindName("HomeMessage")).Text;
                Assert(beginCount == 1 && requests.Count(capability => capability == "noise.management.prepare-exit") == 1 && !requests.Contains("host.stop") &&
                    feedback.Contains("本场监测或录课设置可能已变化") && !feedback.Contains("未发送"), "lost prepare response was reported as unsent or replayed");
            }
            else if (scenario == "Verify")
            {
                Assert(validDialog && preparedBeforeBegin && beginCount == 1 && sourceCount == 1 && visibleCount == 0 && !main.IsVisible,
                    "exit intent started before owner preparation, or verification exposed the hidden workspace");
                Assert(requests.Take(4).SequenceEqual(new[] { "classroom.status", "recording.status", "noise.management.status", "study.exit.begin" }),
                    "known blockers were not checked before exit intent");
            }
            else Assert(beginCount == 0 && !validDialog && (scenario != "Cancel" || requests.IsEmpty),
                "cancellation or an already known blocker started strict exit verification");
            Assert(requests.All(capability => capability is "study.exit.begin" or "study.exit.status" or "classroom.status" or "recording.status" or "noise.management.status" ||
                       scenario == "Busy" && (capability is "classisland.config.get" or "examaware.status") ||
                       losePrepareReply && (capability is "study.photos.flush" or "noise.management.prepare-exit" or "recording.automatic")),
                "unexpected traffic during " + scenario + ": " + string.Join(", ", requests));
            Checks.Add($"Tray exit {scenario}: real confirmation and read-only precheck; only a verified target can enter the timed check; private pipe only");
        }
        finally
        {
            closeProbeDialog.Stop();
            foreach (var confirm in Application.Current.Windows.OfType<StudyActionConfirmation>().ToArray()) confirm.Close();
            foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(window => window.GetType() == dialogType && ReferenceEquals(window.Owner, main)).ToArray())
            { dialogType.GetField("_completed", WorkspaceFlags)!.SetValue(dialog, true); dialog.Close(); }
            main.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Application.Current.DispatcherUnhandledException -= Unhandled;
        }
    }
}
