using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNoiseAccessChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string entry in new[] { "poll", "select", "stop", "management" })
                try { RunNoiseAccessCheck(entry); }
                catch (Exception error) { failures.Add(new InvalidOperationException("Noise access/" + entry + ": " + error.Message, error)); }
            foreach (string operation in new[] { "configure", "authorize" })
                try { RunNoiseManagementAccessCheck(operation); }
                catch (Exception error) { failures.Add(new InvalidOperationException("Noise management access/" + operation + ": " + error.Message, error)); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunNoiseAccessCheck(string entry)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string pipe = "NPEduTools.Test.noise-access." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var initial = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        var first = Read(initial);
        var window = new NoiseWindow(pipe);
        var busy = typeof(NoiseWindow).GetField("_busy", flags)!;
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        Task? command = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args) { eventError = args.Exception; args.Handled = true; }
        var state = new NoiseState(Guid.NewGuid(), 7, entry == "select" ? "Stopped" : "Active", "隔离合成监测状态",
            "fixture", "合成麦克风", DateTimeOffset.UtcNow, -57, "Good",
            new(30, 29.6, 29.6 / 30, -78.6, -48.3, 0, 100), [new(1, -57, "Good")],
            SessionId: Guid.NewGuid(), SelectedDeviceId: "fixture");
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    busy.SetValue(window, true);
                    PumpUntil(() => first.IsCompleted, "initial synthetic noise device request did not arrive");
                    var request = first.GetAwaiter().GetResult();
                    Assert(request.Capability == "noise.devices", "unexpected initial query");
                    var warmup = Protocol.WriteAsync(initial, new HostResponse(Protocol.Version, request.RequestId,
                        "Succeeded", null, "合成设备", Noise: state, NoiseDevices: [new("fixture", "合成麦克风")]), timeout.Token);
                    PumpUntil(() => warmup.IsCompleted, "initial signal write stalled");
                    var ownership = ReplyManualFixtureAsync(initial, state, timeout.Token);
                    PumpUntil(() => ownership.IsCompleted && ((TextBlock)owner.FindName("SummaryText")).Text.Contains("29.6"),
                        "synthetic initial state did not render");
                    warmup.GetAwaiter().GetResult(); initial.Dispose();
                    // The duplex HostClient cannot open a write-only server. No real ACL changes are made.
                    using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    busy.SetValue(window, false);
                    if (entry is "select" or "stop")
                    {
                        command = (Task)typeof(NoiseWindow).GetMethod("RunAsync", flags)!.Invoke(window,
                            ["noise.command", new NoiseCommand(entry, state.InstanceId, state.Revision, entry == "select" ? "fixture" : null)])!;
                        PumpUntil(() => command.IsCompleted, "access-denied noise command did not settle");
                        command.GetAwaiter().GetResult();
                        Assert(!(bool)busy.GetValue(window)!, "access denial left noise commands busy");
                    }
                    else if (entry == "management")
                    {
                        busy.SetValue(window, true); // Keep this check confined to the actual settings entry event.
                        SynchronizationContext.SetSynchronizationContext(context);
                        typeof(NoiseWindow).GetMethod("ManagementClicked", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
                        SettleManagementEvent(context);
                        Assert(eventError is null && ((TextBlock)owner.FindName("MessageText")).Text.Contains("无法读取后台管理设置"),
                            "access denial escaped the management entry: " + eventError?.Message);
                        Assert(!Application.Current.Windows.Cast<Window>().Any(w => w.Owner == window), "access denial opened a management dialog");
                        busy.SetValue(window, false);
                    }
                    PumpUntil(() => ((TextBlock)owner.FindName("StateText")).Text.Contains("未知"),
                        "access-denied poll stopped and kept the old sampling state");
                    Assert(((TextBlock)owner.FindName("LevelText")).Text == "—" &&
                        !((TextBlock)owner.FindName("SummaryText")).Text.Contains("29.6") &&
                        ((Canvas)owner.FindName("TrendCanvas")).Children.Count == 0,
                        "access denial retained a live level, summary or trend");
                    Assert(!Button(owner, "StartButton").IsEnabled && !Button(owner, "StopButton").IsEnabled &&
                        !Button(owner, "SaveDeviceButton").IsEnabled, "access denial left capture controls enabled");
                    denied.Dispose();
                    using var recovered = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var read = Read(recovered);
                    PumpUntil(() => read.IsCompleted, "noise polling did not recover after access was restored");
                    var fresh = read.GetAwaiter().GetResult();
                    Assert(fresh.Capability == "noise.status" && fresh.Noise is null, "recovery replayed a command");
                    var reply = Protocol.WriteAsync(recovered, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离恢复", Noise: state with { State = "Active", Revision = 8, CurrentDbfs = -42 }), timeout.Token);
                    PumpUntil(() => reply.IsCompleted, "recovery signal write stalled");
                    var restoredOwnership = ReplyManualFixtureAsync(recovered, state with { State = "Active" }, timeout.Token);
                    PumpUntil(() => restoredOwnership.IsCompleted && ((TextBlock)owner.FindName("LevelText")).Text == "-42.0",
                        "fresh status failed to restore the noise display");
                    reply.GetAwaiter().GetResult();
                    Assert(Button(owner, "StopButton").IsEnabled && !Button(owner, "StartButton").IsEnabled,
                        "fresh active state did not restore controls");
                    Checks.Add($"Noise access/{entry}: denied duplex access becomes unknown; read-only polling recovers without capture, selection or stop replay");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown(); SettleManagementEvent(context);
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
            if (command?.IsFaulted == true) _ = command.Exception;
        }
    }

    private static void RunNoiseManagementAccessCheck(string operation)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.noise-management-access." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var state = new NoiseProtectionState(operation == "authorize", true, Guid.NewGuid(),
            operation == "authorize" ? Guid.NewGuid() : null);
        HostRequest? target = operation == "authorize"
            ? new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("stop", state.InstanceId, 7)) : null;
        var owner = new Window { Title = "隔离管理访问检查", Width = 600, Height = 400 };
        owner.Show();
        var type = typeof(NoiseWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        var dialog = (Window)type.GetConstructors(flags).Single().Invoke([owner, pipe, state, target, null, null]);
        var submit = (Button)type.GetField("_submit", flags)!.GetValue(dialog)!;
        var message = (TextBlock)type.GetField("_message", flags)!.GetValue(dialog)!;
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args) { eventError = args.Exception; args.Handled = true; }
        void Submit()
        {
            ((PasswordBox)type.GetField("_old", flags)!.GetValue(dialog)!).Password = "fixture-teacher-2026";
            if (operation == "configure")
                ((PasswordBox)type.GetField("_new", flags)!.GetValue(dialog)!).Password =
                    ((PasswordBox)type.GetField("_confirm", flags)!.GetValue(dialog)!).Password = "fixture-new-secret-2026";
            SynchronizationContext.SetSynchronizationContext(context);
            submit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        }
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(dialog, window =>
            {
                try
                {
                    Submit(); SettleManagementEvent(context);
                    Assert(eventError is null, "access denial escaped management submit: " + eventError?.Message);
                    Assert(dialog.IsVisible && dialog.DialogResult is null && type.GetProperty("Ticket")!.GetValue(dialog) is null &&
                        submit.IsEnabled && message.Text.Contains("无法确认"), "denied access granted authorization, closed the dialog or blocked retry");
                    denied.Dispose();
                    using var recovered = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task<HostRequest> Read()
                    {
                        await recovered.WaitForConnectionAsync(timeout.Token);
                        return await Protocol.ReadAsync<HostRequest>(recovered, timeout.Token);
                    }
                    var read = Read();
                    var idle = Task.Delay(150);
                    PumpUntil(() => idle.IsCompleted, "retry observation did not settle");
                    Assert(!read.IsCompleted, "management submit automatically replayed after access recovery");
                    Submit();
                    PumpUntil(() => read.IsCompleted, "explicit management retry did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "noise.management.command" && request.NoiseManagement?.Action == operation &&
                        Protocol.Validate(request) is null, "explicit retry sent the wrong management command");
                    if (target is not null)
                        Assert(request.NoiseManagement is { Purpose: "stop" } grant && grant.TargetRequestId == target.RequestId &&
                            grant.InstanceId == state.InstanceId && grant.SessionId == state.SessionId, "retry lost its authorization scope");
                    var ticket = target is null ? (Guid?)null : Guid.NewGuid();
                    var reply = Protocol.WriteAsync(recovered, new HostResponse(Protocol.Version, request.RequestId,
                        "Succeeded", null, "隔离管理回执", NoiseProtection: state with { Ticket = ticket }), timeout.Token);
                    PumpUntil(() => reply.IsCompleted, "explicit retry response did not finish");
                    reply.GetAwaiter().GetResult(); SettleManagementEvent(context);
                    Assert(dialog.DialogResult == true && Equals(type.GetProperty("Ticket")!.GetValue(dialog), ticket),
                        "fresh explicit retry did not complete management validation");
                    Checks.Add($"Noise management access/{operation}: denied access creates no grant and keeps the dialog retryable; only explicit resubmit completes with the original scope");
                }
                finally { dialog.Close(); }
            });
        }
        finally
        {
            dialog.Close(); owner.Close(); SettleManagementEvent(context);
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
