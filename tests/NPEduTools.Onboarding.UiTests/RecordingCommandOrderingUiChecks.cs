using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingCommandOrderingChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string command in new[] { "pause", "disable" })
                foreach (bool lost in new[] { false, true })
                    try { RunRecordingCommandOrderingCheck(command, lost); }
                    catch (Exception error) { failures.Add(new InvalidOperationException(command + "/lost=" + lost + ": " + error.Message, error)); }
            foreach (bool manualFirst in new[] { false, true })
                foreach (bool lostFirst in new[] { false, true })
                    try { RunRecordingMutationSerializationCheck(manualFirst, lostFirst); }
                    catch (Exception error) { failures.Add(new InvalidOperationException("serialization/manualFirst=" + manualFirst + "/lostFirst=" + lostFirst + ": " + error.Message, error)); }
            try { RunRecordingMutationLeaseCheck(); }
            catch (Exception error) { failures.Add(new InvalidOperationException("serialization/lease: " + error.Message, error)); }
            foreach (string action in new[] { "pause", "stop" })
                foreach (string result in new[] { "confirmed", "replaced", "lost", "closing" })
                    try { RunQueuedManualRecordingCheck(action, result); }
                    catch (Exception error) { failures.Add(new InvalidOperationException("queued-manual/" + action + "/" + result + ": " + error.Message, error)); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunRecordingCommandOrderingCheck(string command, bool lost)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-command-order." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var status = Server();
        var initial = Read(status);
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        RecordingState State() => (RecordingState)type.GetProperty("State")!.GetValue(client)!;
        AutomaticRecordingState Automatic() => (AutomaticRecordingState)type.GetProperty("Automatic")!.GetValue(client)!;
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        var active = new RecordingState("Recording", "隔离旧录制状态", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        var enabled = new AutomaticRecordingState(true, (Guid)type.GetField("_client", flags)!.GetValue(client)!, "隔离旧自动录课状态", null, []);
        var confirmed = command == "pause" ? active with { Phase = "Paused", Message = "隔离暂停已确认" } : active;
        var confirmedAuto = command == "disable" ? enabled with { Enabled = false, Message = "隔离关闭已确认" } : enabled;
        bool AutomaticConfirmed() => Automatic().Enabled == confirmedAuto.Enabled &&
            Automatic().ClientId == confirmedAuto.ClientId && Automatic().Message == confirmedAuto.Message;
        Task Reply(NamedPipeServerStream server, HostRequest request, RecordingState state, AutomaticRecordingState automatic)
            => Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                "Succeeded", null, "隔离回执", Recording: state, Automatic: automatic), timeout.Token);
        try
        {
            PumpUntil(() => initial.IsCompleted, "initial recording status query did not arrive");
            var first = initial.GetAwaiter().GetResult();
            Assert(first.Capability == "recording.status", "unexpected initial query");
            var warmup = Reply(status, first, active, enabled);
            PumpUntil(() => warmup.IsCompleted && Available(), "initial recording state did not render");
            warmup.GetAwaiter().GetResult(); status.Disconnect();
            var lease = Read(status);
            PumpUntil(() => lease.IsCompleted, "initial recording lease did not arrive");
            var leaseRequest = lease.GetAwaiter().GetResult();
            Assert(leaseRequest.Automatic?.Action == "lease", "unexpected mutation during warmup");
            var leaseReply = Reply(status, leaseRequest, active, enabled);
            PumpUntil(() => leaseReply.IsCompleted, "initial recording lease did not settle");
            leaseReply.GetAwaiter().GetResult();
            // Leave the status server connected so only the command server can accept this request.
            using var mutation = Server();
            var readCommand = Read(mutation);
            var action = command == "pause"
                ? (Task)type.GetMethod("SendAsync")!.Invoke(client, ["pause", null])!
                : (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["disable", null])!;
            PumpUntil(() => readCommand.IsCompleted, "recording command did not arrive");
            var request = readCommand.GetAwaiter().GetResult();
            Assert(command == "pause" ? request.Recording is { Action: "pause" } manual && manual.Control == active.Control
                : request.Automatic is { Action: "disable" } automatic && automatic.ClientId == enabled.ClientId,
                "command lost its session or client fence");

            status.Disconnect();
            var during = Read(status);
            PumpUntil(() => during.IsCompleted, "status query during the pending command did not arrive");
            var old = during.GetAwaiter().GetResult();
            Assert(old.Capability == "recording.status" && !action.IsCompleted, "fixture did not hold a query started during the command");
            var commandReply = Reply(mutation, request, confirmed, confirmedAuto);
            PumpUntil(() => commandReply.IsCompleted && action.IsCompleted, "confirmed recording command did not settle");
            commandReply.GetAwaiter().GetResult(); action.GetAwaiter().GetResult();
            Assert(Available() && State() == confirmed && AutomaticConfirmed(), "command response did not confirm its new state");
            mutation.Dispose();

            using var fresh = Server();
            var next = Read(fresh);
            if (lost) status.Dispose();
            else
            {
                var oldReply = Reply(status, old, active, enabled);
                PumpUntil(() => oldReply.IsCompleted, "delayed old status response did not finish");
                oldReply.GetAwaiter().GetResult();
            }
            // The next request proves the old query has been processed; hold its reply for assertions.
            PumpUntil(() => next.IsCompleted, "polling did not continue after the delayed query");
            Assert(Available() && State() == confirmed && AutomaticConfirmed(),
                "a query started during the command overwrote its confirmed result or marked it unknown");
            async Task Recover()
            {
                var current = next.GetAwaiter().GetResult();
                while (true)
                {
                    Assert(current.Capability == "recording.status" || current.Automatic?.Action == "lease",
                        "polling replayed a recording mutation");
                    await Reply(fresh, current, confirmed with { Message = "隔离最新只读状态" }, confirmedAuto);
                    if (current.Capability == "recording.status") return;
                    fresh.Disconnect(); current = await Read(fresh);
                }
            }
            var recovery = Recover();
            PumpUntil(() => recovery.IsCompleted && State().Message == "隔离最新只读状态", "new status query failed to restore normal polling");
            recovery.GetAwaiter().GetResult();
            Assert(Available() && State().Phase == confirmed.Phase && AutomaticConfirmed(), "fresh polling lost the confirmed state");
            Checks.Add($"Recording {command}/lost={lost}: a status query started during the command cannot overwrite its later receipt; subsequent read-only polling works without command replay");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            PumpUntil(() => poll.IsCompleted, "recording command-order client did not stop");
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
