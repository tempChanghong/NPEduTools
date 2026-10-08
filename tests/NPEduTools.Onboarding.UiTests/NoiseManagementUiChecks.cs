using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunNoiseManagementChecks()
    {
        var failures = new List<Exception>();
        foreach (string ownerAction in new[] { "close", "keep", "hide" })
            foreach (string operation in new[] { "configure", "authorize", "unprotected" })
                try { RunNoiseManagementOwnerCheck(operation, ownerAction); }
                catch (Exception error) { failures.Add(new InvalidOperationException(operation + "/owner=" + ownerAction + ": " + error.Message, error)); }
        foreach (string operation in new[] { "configure", "authorize", "entry" })
            foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message" })
                try
                {
                    if (operation == "entry") RunNoiseManagementEntryCheck(invalid);
                    else RunNoiseManagementDialogCheck(operation, invalid);
                }
                catch (Exception error) { failures.Add(new InvalidOperationException(operation + "/" + invalid + ": " + error.Message, error)); }
        if (failures.Count > 0) throw new AggregateException("Noise management protocol recovery failed", failures);
    }

    private static void RunNoiseManagementOwnerCheck(string operation, string ownerAction)
    {
        bool closeOwner = ownerAction == "close";
        string pipe = "NPEduTools.Test.noise-management-owner." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var type = typeof(NoiseWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        var state = new NoiseProtectionState(operation == "authorize", true, Guid.NewGuid(),
            operation == "authorize" ? Guid.NewGuid() : null);
        var target = new HostRequest(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("stop", state.InstanceId, 7));
        var owner = new Window { Title = "隔离管理入口关闭测试", Width = 600, Height = 400 };
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        int dialogs = 0;
        var cancelDialog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        cancelDialog.Tick += (_, _) =>
        {
            foreach (var dialog in Application.Current.Windows.Cast<Window>().Where(w => w.GetType() == type && w.Owner == owner).ToArray())
            {
                dialogs++;
                dialog.Close(); // Live controls must still open normally; no synthetic password is submitted.
            }
        };
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        try
        {
            owner.Show();
            var read = Read();
            var method = type.GetMethod(operation == "configure" ? "ConfigureAsync" : "AuthorizeAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
            var action = (Task)method.Invoke(null, operation == "configure" ? [owner, pipe] : [owner, pipe, target])!;
            PumpUntil(() => read.IsCompleted, "management owner status query did not arrive");
            var request = read.GetAwaiter().GetResult();
            Assert(request.Capability == "noise.management.status" && Protocol.Validate(request) is null, "management entry sent a mutation");
            if (closeOwner) owner.Close();
            else if (ownerAction == "hide") owner.Hide();
            cancelDialog.Start();
            var reply = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                "Succeeded", null, "隔离管理状态", NoiseProtection: state), timeout.Token);
            PumpUntil(() => reply.IsCompleted, "management owner reply did not finish"); reply.GetAwaiter().GetResult();
            PumpUntil(() => action.IsCompleted, "management entry did not settle after the delayed reply");
            Assert(!action.IsFaulted && !action.IsCanceled,
                "delayed management status attempted to reopen a closed owner: " + action.Exception?.GetBaseException().Message);
            if (operation != "configure")
            {
                var authorized = ((Task<HostRequest?>)action).GetAwaiter().GetResult();
                Assert(closeOwner || operation == "authorize" ? authorized is null : ReferenceEquals(authorized, target),
                    "management entry returned an operation after its owner closed or changed normal unprotected behavior");
            }
            Assert(dialogs == (!closeOwner && operation != "unprotected" ? 1 : 0), "management entry opened an unexpected modal");
            Checks.Add(operation + "/owner=" + ownerAction + ": delayed status settles; closed owner receives no modal or operation; live and hidden entry retain normal behavior");
        }
        finally
        {
            cancelDialog.Stop(); owner.Close();
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static Task InvalidManagementReply(NamedPipeServerStream server, HostRequest request, string invalid,
        NoiseProtectionState state, CancellationToken token) => invalid switch
    {
        "zero-length" => server.WriteAsync(new byte[4], token).AsTask(),
        "empty-message" => Protocol.WriteAsync<object?>(server, null, token),
        _ => Protocol.WriteAsync(server, new HostResponse(
            invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
            invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null, "不可采信的管理回执",
            NoiseProtection: state with { Ticket = Guid.NewGuid() }), token)
    };

    private static void SettleManagementEvent(QuickEventContext context)
    {
        PumpUntil(() => context.Active == 0, "management event did not settle");
        var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        PumpUntil(() => idle.IsCompleted, "management exception dispatch did not settle");
    }

    private static void RunNoiseManagementDialogCheck(string operation, string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.noise-management-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        var state = new NoiseProtectionState(operation == "authorize", true, instance,
            operation == "authorize" ? session : null);
        HostRequest? target = operation == "authorize"
            ? new(Protocol.Version, Guid.NewGuid(), "noise.command", Noise: new("stop", instance, 7)) : null;
        var owner = new Window { Title = "隔离管理验证测试", Width = 600, Height = 400 };
        var type = typeof(NoiseWindow).Assembly.GetType("NPEduTools.App.NoiseManagementDialog")!;
        owner.Show();
        Window dialog;
        try { dialog = (Window)type.GetConstructors(flags).Single().Invoke([owner, pipe, state, target]); }
        catch { owner.Close(); throw; }
        var submit = (Button)type.GetField("_submit", flags)!.GetValue(dialog)!;
        var message = (TextBlock)type.GetField("_message", flags)!.GetValue(dialog)!;
        PasswordBox Secret(string name) => (PasswordBox)type.GetField(name, flags)!.GetValue(dialog)!;
        object? Ticket() => type.GetProperty("Ticket")!.GetValue(dialog);
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args) { eventError = args.Exception; args.Handled = true; }
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        void Fill()
        {
            Secret("_old").Password = "fixture-teacher-2026";
            if (operation == "configure") Secret("_new").Password = Secret("_confirm").Password = "fixture-new-secret-2026";
        }
        void VerifyRequest(HostRequest request)
        {
            Assert(request.Capability == "noise.management.command" && request.NoiseManagement?.Action == operation &&
                Protocol.Validate(request) is null, "unexpected management command");
            if (target is not null)
                Assert(request.NoiseManagement is { Purpose: "stop" } grant && grant.TargetRequestId == target.RequestId &&
                    grant.InstanceId == instance && grant.SessionId == session, "authorization lost its action/session scope");
        }
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(dialog, window =>
            {
                try
                {
                    Fill(); var read = Read();
                    SynchronizationContext.SetSynchronizationContext(context);
                    submit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(context.Active == 1 && !submit.IsEnabled, "management submit did not enter its pending gate");
                    PumpUntil(() => read.IsCompleted, "management command did not arrive");
                    var request = read.GetAwaiter().GetResult(); VerifyRequest(request);
                    var reply = InvalidManagementReply(server, request, invalid, state, timeout.Token);
                    PumpUntil(() => reply.IsCompleted, "invalid management reply did not finish"); reply.GetAwaiter().GetResult();
                    SettleManagementEvent(context);
                    Assert(eventError is null, "invalid management reply escaped its event: " + eventError?.Message);
                    Assert(dialog.IsVisible && dialog.DialogResult is null && Ticket() is null && submit.IsEnabled &&
                        message.Text.Contains("无法确认") && message.Text.Contains("未继续"),
                        "invalid receipt closed the dialog, granted authorization or blocked retry");
                    if (invalid == "request-id") Snapshot(window, "noise-management-" + operation + "-unconfirmed.png");
                    // Only a new explicit submit can retry; its request has a new correlation id.
                    server.Disconnect(); Fill(); var retry = Read();
                    SynchronizationContext.SetSynchronizationContext(context);
                    submit.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    PumpUntil(() => retry.IsCompleted, "explicit management retry did not arrive");
                    var fresh = retry.GetAwaiter().GetResult(); VerifyRequest(fresh);
                    Assert(fresh.RequestId != request.RequestId, "retry reused the old management request");
                    var ticket = operation == "authorize" ? Guid.NewGuid() : (Guid?)null;
                    var restored = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离管理结果", NoiseProtection: state with { Ticket = ticket }), timeout.Token);
                    PumpUntil(() => restored.IsCompleted, "valid management retry did not finish"); restored.GetAwaiter().GetResult();
                    SettleManagementEvent(context);
                    Assert(eventError is null && dialog.DialogResult == true && !dialog.IsVisible && Equals(Ticket(), ticket),
                        "valid explicit retry did not complete the dialog");
                    Assert(Secret("_old").Password == "" && Secret("_new").Password == "" && Secret("_confirm").Password == "",
                        "closed management dialog retained synthetic secrets");
                    Checks.Add(operation + "/" + invalid + ": invalid receipt never grants or closes; a scoped explicit retry completes normally");
                }
                finally { dialog.Close(); }
            });
        }
        finally
        {
            dialog.Close(); owner.Close();
            SettleManagementEvent(context);
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void RunNoiseManagementEntryCheck(string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.noise-management-entry." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream stream)
        {
            await stream.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
        }
        using var initial = Server(); var initialRead = Read(initial);
        var window = new NoiseWindow(pipe);
        using var server = Server();
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args) { eventError = args.Exception; args.Handled = true; }
        void Open()
        {
            SynchronizationContext.SetSynchronizationContext(context);
            typeof(NoiseWindow).GetMethod("ManagementClicked", flags)!.Invoke(window, [window, new RoutedEventArgs()]);
        }
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    // Hold constructor enumeration on its own pipe; never enumerate real devices.
                    PumpUntil(() => initialRead.IsCompleted, "initial isolated device query did not arrive");
                    Assert(initialRead.GetAwaiter().GetResult().Capability == "noise.devices", "unexpected constructor query");
                    var read = Read(server); Open();
                    PumpUntil(() => read.IsCompleted, "management status query did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "noise.management.status", "entry sent a mutation");
                    var reply = InvalidManagementReply(server, request, invalid, new(false, true, Guid.NewGuid(), null), timeout.Token);
                    PumpUntil(() => reply.IsCompleted, "invalid management status did not finish"); reply.GetAwaiter().GetResult();
                    SettleManagementEvent(context);
                    Assert(eventError is null && ((TextBlock)owner.FindName("MessageText")).Text.Contains("无法读取后台管理设置"),
                        "invalid management status escaped the entry event or lost its retry guidance: " + eventError?.Message);
                    Assert(!Application.Current.Windows.Cast<Window>().Any(w => w.Owner == window),
                        "invalid status opened a management dialog");
                    server.Disconnect(); var retry = Read(server); Open();
                    PumpUntil(() => retry.IsCompleted, "manual management entry retry did not arrive");
                    var fresh = retry.GetAwaiter().GetResult();
                    Assert(fresh.Capability == "noise.management.status" && fresh.RequestId != request.RequestId,
                        "management entry retry sent an operation or reused its correlation");
                    server.Disconnect(); SettleManagementEvent(context);
                    Assert(eventError is null && window.IsVisible, "retry failure crashed or closed the noise page");
                    Checks.Add("entry/" + invalid + ": invalid status opens no dialog; guidance remains and manual retry sends only a fresh status query");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown(); SettleManagementEvent(context);
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
