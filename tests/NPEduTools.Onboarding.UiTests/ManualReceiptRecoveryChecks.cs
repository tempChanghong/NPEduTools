using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunManualReceiptRecoveryChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "missing", "request-id", "version", "rejected", "confirmed" })
                RunManualReceiptRecoveryCheck(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunManualReceiptRecoveryCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.manual-receipt." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldStatus = Server();
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        RecordingState State() => (RecordingState)type.GetProperty("State")!.GetValue(client)!;
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        var active = new RecordingState("Recording", "隔离已确认录制", Seconds: 30,
            Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        var paused = active with { Phase = "Paused", Message = "隔离暂停已确认" };
        var reconciled = paused with { Message = "隔离轮询核实暂停" };
        try
        {
            var initial = Read(oldStatus);
            PumpUntil(() => initial.IsCompleted, "initial status request did not arrive");
            Assert(initial.GetAwaiter().GetResult().Capability == "recording.status", "unexpected initial request");
            type.GetMethod("Apply", flags)!.Invoke(client,
                [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离快照", Recording: active)]);
            using var commandServer = Server();
            async Task Reply()
            {
                var request = await Read(commandServer);
                Assert(request.Capability == "recording.command" && request.Recording?.Action == "pause" &&
                    request.Recording.Control == active.Control, "pause command lost its expected session fence");
                await Protocol.WriteAsync(commandServer, new HostResponse(scenario == "version" ? Protocol.Version + 1 : Protocol.Version,
                    scenario == "request-id" ? Guid.NewGuid() : request.RequestId, scenario == "rejected" ? "Rejected" : "Succeeded",
                    scenario == "rejected" ? "FixtureRejected" : null, scenario == "rejected" ? "隔离拒绝：无法暂停" : "隔离命令结果",
                    Recording: scenario == "missing" ? null : scenario == "rejected" ? active : paused), timeout.Token);
            }
            var reply = Reply();
            var action = (Task)type.GetMethod("SendAsync")!.Invoke(client, ["pause", null])!;
            PumpUntil(() => action.IsCompleted && reply.IsCompleted, "manual command did not settle");
            action.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
            if (scenario is "missing" or "request-id" or "version")
            {
                Assert(!Available() && State().Phase == active.Phase && State().Control == active.Control &&
                    State().Error?.Contains("本次操作未确认") == true,
                    scenario + ": invalid manual receipt retains a confirmed or optimistic recording state");
                Assert(type.GetField("_pending", flags)!.GetValue(client) is null, "invalid receipt leaves the pause pending indefinitely");
                long generation = (long)type.GetField("_generation", flags)!.GetValue(client)!;
                var blocked = (Task)type.GetMethod("SendAsync")!.Invoke(client, ["stop", null])!;
                Assert(blocked.IsCompleted && generation == (long)type.GetField("_generation", flags)!.GetValue(client)!,
                    "unknown recording state submitted a second command before reconciliation");
            }
            else if (scenario == "rejected")
            {
                Assert(Available() && State().Phase == "Recording" && State().Control == active.Control &&
                    State().Error?.Contains("无法暂停") == true, "authoritative rejection lost the confirmed state or rejection reason");
            }
            else Assert(Available() && State() == paused, "valid pause receipt did not confirm the pause");
            commandServer.Dispose();

            using var fresh = Server();
            async Task ServeRecovery()
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        var request = await Read(fresh);
                        Assert(request.Capability == "recording.status" || request.Automatic?.Action == "lease",
                            "recovery replayed a manual mutation");
                        await Protocol.WriteAsync(fresh, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "隔离核对", Recording: reconciled), timeout.Token);
                        fresh.Disconnect();
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            var recovery = ServeRecovery();
            oldStatus.Dispose();
            PumpUntil(() => Available() && State() == reconciled, "fresh status did not recover the same client from the manual receipt failure");
            timeout.Cancel();
            PumpUntil(() => recovery.IsCompleted, "manual receipt test server did not stop");
            recovery.GetAwaiter().GetResult();
            Checks.Add(scenario + ": manual receipt keeps the session fence, distinguishes unknown from rejected/confirmed, and recovers through status polling without replay");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
            PumpUntil(() => poll.IsCompleted, "manual receipt test client did not stop");
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
