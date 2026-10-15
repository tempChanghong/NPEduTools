using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingMutationLeaseCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-mutation-lease." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var mutations = new ConcurrentQueue<HostRequest>();
        var handlers = new ConcurrentBag<Task>();
        var active = new RecordingState("Recording", "隔离录制继续", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        var enabled = new AutomaticRecordingState(true, Guid.Empty, "隔离自动录课", null, []);
        var receipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int leases = 0;
        var server = Task.Run(async () => {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var stream = new NamedPipeServerStream(pipe, PipeDirection.InOut, 8,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await stream.WaitForConnectionAsync(stop.Token); }
                    catch { stream.Dispose(); throw; }
                    handlers.Add(Task.Run(async () => {
                        using (stream)
                        {
                            try
                            {
                                var request = await Protocol.ReadAsync<HostRequest>(stream, stop.Token);
                                if (request.Automatic?.Action == "lease") Interlocked.Increment(ref leases);
                                else if (request.Capability != "recording.status")
                                { mutations.Enqueue(request); await receipt.Task.WaitAsync(stop.Token); }
                                await Protocol.WriteAsync(stream, new HostResponse(1, request.RequestId, "Succeeded", null,
                                    "隔离状态", Recording: active, Automatic: enabled), stop.Token);
                            }
                            catch (Exception error) when (error is IOException or OperationCanceledException) { }
                        }
                    }));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        Task? first = null, queued = null;
        try
        {
            PumpUntil(() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!, "initial lease fixture did not connect");
            first = (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["skip-day", null])!;
            PumpUntil(() => mutations.Count == 1, "first mutation did not arrive");
            queued = (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["disable", null])!;
            PumpUntil(() => Volatile.Read(ref leases) >= 2, "pending mutation blocked lease renewal", 8);
            Assert(!first.IsCompleted && !queued.IsCompleted && mutations.Count == 1,
                "queued mutation was sent before the first completed, or pending work stopped lease renewal");
            type.GetMethod("Detach")!.Invoke(client, null);
            PumpUntil(() => poll.IsCompleted && first.IsCompleted && queued.IsCompleted, "client shutdown did not cancel the queued mutation");
            Assert(queued.IsCanceled && mutations.All(request => request.Automatic?.Action == "skip-day"),
                "shutdown submitted or completed an unexecuted queued disable");
            _ = first.Exception;
            Checks.Add("Pending recording mutation keeps status polling and lease renewal alive; client shutdown cancels its queued command before submission");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            stop.Cancel();
            PumpUntil(() => poll.IsCompleted && (first?.IsCompleted ?? true) && (queued?.IsCompleted ?? true) && server.IsCompleted && handlers.All(task => task.IsCompleted),
                "recording mutation lease fixture did not stop");
            _ = first?.Exception; _ = queued?.Exception;
            server.GetAwaiter().GetResult();
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
