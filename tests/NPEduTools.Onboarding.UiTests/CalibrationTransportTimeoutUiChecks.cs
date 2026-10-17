using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunCalibrationTransportTimeoutChecks()
    {
        var failures = new List<Exception>();
        foreach (string operation in new[] { "read", "command", "refresh", "management", "reference-management" })
        {
            try { CalibrationTransportTimeoutCheck(operation); }
            catch (Exception error) { failures.Add(new InvalidOperationException(operation + ": " + error.Message, error)); }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
    }

    private static void CalibrationTransportTimeoutCheck(string operation)
    {
        string pipe = "NPEduTools.Test.calibration-timeout." + Guid.NewGuid().ToString("N");
        var previous = SynchronizationContext.Current;
        var context = new QuickEventContext();
        SynchronizationContext.SetSynchronizationContext(context);
        bool reference = operation == "reference-management";
        Window window = reference ? new NoiseReferenceWindow(pipe) : new NoiseCalibrationWindow(pipe);
        var type = window.GetType();
        var instance = Guid.NewGuid();
        var quick = new NoiseCalibrationState(instance, 1, Guid.NewGuid(), "synthetic-mic", "scope", "Ready", 0,
            [], new(false, null, null, "fixture suggestion"), null, false, null);
        var external = new NoiseReferenceState(instance, 1, "Ready", "synthetic-mic", "Synthetic", 0, 0,
            new(Guid.NewGuid(), "synthetic-mic", "fixture", "Slow", 2, 2, null, []),
            new(false, null, null, "fixture evaluation"), null, false, null);
        type.GetMethod("Render", WorkspaceFlags | System.Reflection.BindingFlags.DeclaredOnly)!
            .Invoke(window, [reference ? (object)external : quick]);
        type.GetField("_connected", WorkspaceFlags)!.SetValue(window, true);
        type.GetMethod("UpdateButtons", WorkspaceFlags)!.Invoke(window, null);
        ((PasswordBox)window.FindName("ManagementSecret")).Password = "synthetic-admin-password";
        Exception? eventError = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args)
        { eventError = args.Exception; args.Handled = true; }
        Task Read() => (Task)type.GetMethod("ReadAsync", WorkspaceFlags)!.Invoke(window, null)!;
        Application.Current.DispatcherUnhandledException += OnError;
        using var stop = new CancellationTokenSource();
        Task? server = null;
        int received = 0;
        try
        {
            // No server exists: exercise the real 1.5-second Named Pipe connection timeout.
            if (operation is "read" or "command")
            {
                Task pending;
                if (operation == "read") pending = Read();
                else
                {
                    var method = type.GetMethod("SendAsync", WorkspaceFlags)!;
                    var arguments = method.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
                    arguments[0] = "begin";
                    pending = (Task)method.Invoke(window, arguments)!;
                }
                PumpUntil(() => pending.IsCompleted, "absent Host request did not settle");
                Assert(!pending.IsFaulted, "Named Pipe timeout escaped the calibration task: " + pending.Exception?.GetBaseException().GetType().Name);
                pending.GetAwaiter().GetResult();
            }
            else
            {
                Click(window, operation == "refresh" ? "RefreshStatus" : "ConfigureManagement");
                SettleManagementEvent(context);
                Assert(eventError is null, "Named Pipe timeout escaped the calibration button: " + eventError?.GetType().Name);
            }
            Assert(!(bool)type.GetField("_busy", WorkspaceFlags)!.GetValue(window)! &&
                !(bool)type.GetField("_verifying", WorkspaceFlags)!.GetValue(window)!, "timeout left calibration busy");
            Assert(!(bool)type.GetField("_connected", WorkspaceFlags)!.GetValue(window)! &&
                !Button(window, "StartSample").IsEnabled && Button(window, "RefreshStatus").IsEnabled,
                "timeout retained actionable old state or prevented refresh");
            Assert(((TextBlock)window.FindName("OperationMessage")).Text.Contains("后台") ||
                ((TextBlock)window.FindName("OperationMessage")).Text.Contains("未确认"), "timeout lacked usable feedback");
            server = Task.Run(() => WorkspaceProxy(pipe, request =>
            {
                Interlocked.Increment(ref received);
                Assert(request.Capability is "noise.management.status" or "noise.calibration.status" or "noise.reference.status",
                    "recovery replayed a calibration or management mutation");
                return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "read-only recovery",
                    NoiseProtection: request.Capability == "noise.management.status" ? new(false, true, instance, null) : null,
                    NoiseCalibration: request.Capability == "noise.calibration.status" ? quick with { Revision = 2 } : null,
                    NoiseReference: request.Capability == "noise.reference.status" ? external with { Revision = 2 } : null));
            }, stop.Token));
            var recovery = Read();
            PumpUntil(() => recovery.IsCompleted, "explicit calibration refresh did not recover");
            recovery.GetAwaiter().GetResult();
            Assert(Button(window, "BeginDraft").IsEnabled && Volatile.Read(ref received) == 2,
                "valid explicit status refresh did not restore calibration or sent extra requests");
            Checks.Add(operation + ": absent Host produces a real connection timeout, UI stays responsive with unknown state; explicit read-only recovery never replays mutations; no sensors");
        }
        finally
        {
            window.Close(); stop.Cancel();
            if (server is not null) try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            SettleManagementEvent(context);
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
