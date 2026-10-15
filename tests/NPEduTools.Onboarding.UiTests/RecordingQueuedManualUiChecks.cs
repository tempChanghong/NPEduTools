using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunQueuedManualRecordingCheck(string action, string result)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.queued-manual." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 3,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream stream)
        {
            await stream.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(stream, timeout.Token);
        }
        using var status = Server(); var statusRead = Read(status);
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        var initial = new RecordingState("Recording", "隔离录制中", Control: new("Manual", Guid.NewGuid(), "", 0, 1));
        var automatic = new AutomaticRecordingState(true, Guid.NewGuid(), "隔离自动录课", null, []);
        RecordingState State() => (RecordingState)type.GetProperty("State")!.GetValue(client)!;
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        Task Reply(NamedPipeServerStream stream, HostRequest request, RecordingState state) => Protocol.WriteAsync(stream,
            new HostResponse(1, request.RequestId, "Succeeded", null, "隔离回执", Recording: state, Automatic: automatic), timeout.Token);
        Task? first = null, manual = null;
        try
        {
            PumpUntil(() => statusRead.IsCompleted, "initial poll did not arrive");
            Assert(statusRead.GetAwaiter().GetResult().Capability == "recording.status", "unexpected initial poll");
            type.GetMethod("Apply", flags)!.Invoke(client,
                [new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "隔离初始状态", Recording: initial, Automatic: automatic)]);
            using var mutation = Server(); var firstRead = Read(mutation);
            first = (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["skip-day", null])!;
            PumpUntil(() => firstRead.IsCompleted, "automatic command did not arrive");
            var firstRequest = firstRead.GetAwaiter().GetResult();
            Assert(firstRequest.Automatic?.Action == "skip-day", "unexpected automatic command");

            using var command = Server(); var commandRead = Read(command);
            manual = (Task)type.GetMethod("SendAsync")!.Invoke(client, [action, null])!;
            var duplicate = (Task)type.GetMethod("SendAsync")!.Invoke(client, [action, null])!;
            var settle = Stopwatch.StartNew();
            PumpUntil(() => settle.Elapsed >= TimeSpan.FromMilliseconds(80), "dispatcher did not settle");
            Assert(!manual.IsCompleted && !commandRead.IsCompleted && duplicate.IsCompleted,
                "manual " + action + " was silently dropped while waiting for an automatic receipt, or a double tap queued twice");
            if (result is "lost" or "closing")
            {
                if (result == "closing") type.GetMethod("Detach")!.Invoke(client, null);
                else mutation.Dispose();
                PumpUntil(() => first.IsCompleted && manual.IsCompleted, "unconfirmed automatic operation left the manual action hanging");
                Assert(!commandRead.IsCompleted && (result == "closing" || !Available()),
                    "manual action was submitted after shutdown or without a fresh recording state");
                _ = first.Exception; manual.GetAwaiter().GetResult();
            }
            else
            {
                // Lease renewal must not look like a different session. A real replacement must.
                var current = initial with { Control = initial.Control! with { LeaseDeadline = 2,
                    SessionId = result == "replaced" ? Guid.NewGuid() : initial.Control!.SessionId } };
                var firstReply = Reply(mutation, firstRequest, current);
                PumpUntil(() => firstReply.IsCompleted && first.IsCompleted, "automatic receipt did not settle");
                firstReply.GetAwaiter().GetResult(); first.GetAwaiter().GetResult();
                if (result == "replaced")
                {
                    PumpUntil(() => manual.IsCompleted, "manual action did not resolve after its target session changed");
                    Assert(!commandRead.IsCompleted && State().Control == current.Control && State().Error?.Contains("会话") == true,
                        "queued manual action targeted the replacement session or failed without feedback");
                }
                else
                {
                    PumpUntil(() => commandRead.IsCompleted, "queued manual action never reached Host");
                    var request = commandRead.GetAwaiter().GetResult();
                    Assert(request.Recording?.Action == action && initial.Control!.Matches(request.Recording.Control),
                        "queued manual action lost its original session fence");
                    var final = current with { Phase = action == "stop" ? "Saved" : "Paused", Message = "隔离手动操作已确认" };
                    var receipt = Reply(command, request, final);
                    PumpUntil(() => receipt.IsCompleted && manual.IsCompleted, "manual receipt did not settle");
                    receipt.GetAwaiter().GetResult();
                    Assert(Available() && State() == final, "queued manual action was not confirmed");
                }
                manual.GetAwaiter().GetResult();
            }
            Checks.Add($"Queued manual {action}/{result}: wait behind automatic receipt, coalesce double taps, retain session fence and cancel safely; fake pipe only");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null); timeout.Cancel();
            PumpUntil(() => poll.IsCompleted && (first?.IsCompleted ?? true) && (manual?.IsCompleted ?? true), "queued manual fixture did not stop");
            _ = first?.Exception; _ = manual?.Exception;
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
