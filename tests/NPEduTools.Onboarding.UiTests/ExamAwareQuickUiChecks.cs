using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    // Observe async-void completion without changing the production event handler.
    private sealed class QuickEventContext : SynchronizationContext
    {
        private readonly DispatcherSynchronizationContext _dispatcher = new(Application.Current.Dispatcher);
        public int Active { get; private set; }
        public override void OperationStarted() => Active++;
        public override void OperationCompleted() => Active--;
        public override void Post(SendOrPostCallback callback, object? state) => _dispatcher.Post(callback, state);
        public override void Send(SendOrPostCallback callback, object? state) => _dispatcher.Send(callback, state);
        public override SynchronizationContext CreateCopy() => this;
    }

    private static void RunExamAwareQuickChecks()
    {
        RunExamAwareQuickAccessCheck();
        foreach (string scenario in new[] { "request-id", "version", "zero-length", "empty-message", "disconnected", "rejected", "accepted", "stopped" })
            RunExamAwareQuickCheck(scenario);
    }

    private static void RunExamAwareQuickAccessCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.examaware-quick-access." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // Deny the real duplex client by exposing a write-only test server, without changing ACLs.
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        ExamAwareWindow? Fallback() => (ExamAwareWindow?)typeof(MainWindow).GetField("_examAwareWindow", flags)!.GetValue(window);
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            eventError = args.Exception;
            args.Handled = true; // Observe the baseline async-void crash without terminating the fixture.
        }
        SynchronizationContext.SetSynchronizationContext(context);
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(window, _ =>
            {
                try
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                    typeof(MainWindow).GetMethod("OpenExamAwareQuick", flags)!.Invoke(window, null);
                    PumpUntil(() => context.Active == 0, "access-denied quick event did not complete");
                    var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                    PumpUntil(() => idle.IsCompleted, "quick access error dispatch did not settle");
                    Assert(eventError is null, "access-denied quick entry raised an unhandled " + eventError?.GetType().Name + ": " + eventError?.Message);
                    var fallback = Fallback();
                    Assert(fallback?.IsVisible == true, "access denial did not open the examination management window");
                    Assert(((TextBlock)fallback!.FindName("Connection")).Text.Contains("未确认") &&
                        !Button(fallback, "PresentPlanButton").IsEnabled && !Button(fallback, "EnableAutoStartButton").IsEnabled,
                        "access denial presented an actionable bridge or startup state");

                    denied.Dispose();
                    using var recovered = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task Reply()
                    {
                        await recovered.WaitForConnectionAsync(timeout.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(recovered, timeout.Token);
                        Assert(request.Capability == "examaware.status" && request.ExamPlan is null && request.AutoStartEnabled is null,
                            "recovery replayed a start or configuration command");
                        await Protocol.WriteAsync(recovered, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "隔离连接恢复", ExamAware: ReadyExamAware()), timeout.Token);
                    }
                    var reply = Reply();
                    PumpUntil(() => reply.IsCompleted && ((TextBlock)fallback.FindName("Connection")).Text == "隔离桥接已连接",
                        "management window failed to recover through read-only polling");
                    reply.GetAwaiter().GetResult();
                    Assert(eventError is null && Button(fallback, "PresentPlanButton").IsEnabled,
                        "confirmed recovery failed to restore normal management actions");
                    Checks.Add("ExamAware quick access denial: async-void event settles without a crash; unconfirmed management fallback recovers through status polling without replaying startup");
                }
                finally { Fallback()?.Shutdown(); window.Close(); }
            });
        }
        finally
        {
            Fallback()?.Shutdown(); window.Close();
            if (context.Active > 0) PumpUntil(() => context.Active == 0, "quick access event did not stop");
            var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            PumpUntil(() => idle.IsCompleted, "quick access cleanup did not settle");
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void RunExamAwareQuickCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.examaware-quick." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        var context = new QuickEventContext();
        var previous = SynchronizationContext.Current;
        Exception? eventError = null;
        ExamAwareWindow? Fallback() => (ExamAwareWindow?)typeof(MainWindow).GetField("_examAwareWindow", flags)!.GetValue(window);
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs args)
        {
            eventError = args.Exception;
            args.Handled = true; // Fixture captures the old async-void crash as a failed assertion.
        }
        SynchronizationContext.SetSynchronizationContext(context);
        Application.Current.DispatcherUnhandledException += OnError;
        try
        {
            Exercise(window, owner =>
            {
                string stage = "request";
                try
                {
                    var read = Read();
                    SynchronizationContext.SetSynchronizationContext(context);
                    typeof(MainWindow).GetMethod("OpenExamAwareQuick", flags)!.Invoke(window, null);
                    Assert(context.Active == 1, "quick event did not start");
                    PumpUntil(() => read.IsCompleted, "quick launch request did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "examaware.start" && Protocol.Validate(request) is null,
                        "quick fixture sent an unexpected command");
                    if (scenario == "stopped") window.Close();
                    else if (scenario == "disconnected") server.Disconnect();
                    else
                    {
                        stage = "reply";
                        Task reply = Task.Run(() => scenario switch
                        {
                            "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                            "empty-message" => Protocol.WriteAsync<object?>(server, null, timeout.Token),
                            _ => Protocol.WriteAsync(server, new HostResponse(
                                scenario == "version" ? Protocol.Version + 1 : Protocol.Version,
                                scenario == "request-id" ? Guid.NewGuid() : request.RequestId,
                                scenario == "accepted" ? "Accepted" : "Rejected", null, "隔离启动回执"), timeout.Token)
                        });
                        PumpUntil(() => reply.IsCompleted, "quick response did not finish writing");
                        reply.GetAwaiter().GetResult();
                    }
                    stage = "event completion";
                    PumpUntil(() => context.Active == 0, "quick event did not complete");
                    var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                    PumpUntil(() => idle.IsCompleted, "quick event exception dispatch did not settle");
                    Assert(eventError is null, scenario + ": quick event raised an unhandled exception: " + eventError?.Message);
                    bool shouldOpen = scenario is not ("accepted" or "stopped");
                    Assert(shouldOpen ? Fallback()?.IsVisible == true : Fallback() is null,
                        scenario + ": fallback visibility ignored the response or the stopped lifetime");
                    if (shouldOpen)
                    {
                        var fallback = Fallback()!;
                        Assert(((TextBlock)fallback.FindName("Connection")).Text.Contains("未确认"),
                            "unconfirmed quick receipt appeared connected");
                        if (scenario == "request-id") Snapshot(fallback, "examaware-quick-unconfirmed.png");
                        if (server.IsConnected) server.Disconnect();
                        stage = "status recovery";
                        var query = Read();
                        PumpUntil(() => query.IsCompleted, "fallback did not query fresh status");
                        var fresh = query.GetAwaiter().GetResult();
                        Assert(fresh.Capability == "examaware.status", "fallback replayed a start command");
                        var restored = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                            "Succeeded", null, "隔离恢复", ExamAware: ReadyExamAware()), timeout.Token);
                        PumpUntil(() => restored.IsCompleted &&
                            ((TextBlock)fallback.FindName("Connection")).Text == "隔离桥接已连接",
                            "fallback did not recover on fresh status");
                        restored.GetAwaiter().GetResult();
                        if (scenario == "request-id") Snapshot(fallback, "examaware-quick-recovered.png");
                    }
                    Checks.Add("ExamAware quick " + scenario + ": event settles without a crash; fallback recovery is read-only and stopped windows stay closed");
                }
                catch (Exception error) { throw new InvalidOperationException(scenario + " failed during " + stage + ": " + error, error); }
                finally { Fallback()?.Shutdown(); window.Close(); }
            });
        }
        finally
        {
            Fallback()?.Shutdown(); window.Close();
            if (context.Active > 0) PumpUntil(() => context.Active == 0, "isolated quick event did not stop");
            var idle = Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
            PumpUntil(() => idle.IsCompleted, "isolated quick event cleanup did not settle");
            Application.Current.DispatcherUnhandledException -= OnError;
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
