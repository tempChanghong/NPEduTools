using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingPollRecoveryChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "request-id", "version", "zero-length", "oversized", "empty-message", "invalid-json", "lease" })
                RunRecordingPollRecoveryCheck(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunRecordingPollRecoveryCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-poll." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var broken = Server();
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        RecordingState State() => (RecordingState)type.GetProperty("State")!.GetValue(client)!;
        var automatic = new AutomaticRecordingState(true, Guid.NewGuid(), "隔离自动录课", null, []);
        var active = new RecordingState("Recording", "隔离已确认录制", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        var automaticEvents = new List<AutomaticRecordingState>();
        type.GetEvent("AutomaticChanged")!.AddEventHandler(client, (Action<AutomaticRecordingState>)(automaticEvents.Add));
        try
        {
            var read = Read(broken);
            PumpUntil(() => read.IsCompleted, "isolated status request did not arrive");
            var request = read.GetAwaiter().GetResult();
            Assert(request.Capability == "recording.status", "unexpected initial request");
            type.GetMethod("Apply", flags)!.Invoke(client,
                [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离快照", Recording: active, Automatic: automatic)]);
            if (scenario == "lease")
            {
                var statusReply = Protocol.WriteAsync(broken, new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离正常状态", Recording: active, Automatic: automatic), timeout.Token);
                PumpUntil(() => statusReply.IsCompleted, "status response before lease did not finish");
                statusReply.GetAwaiter().GetResult();
                broken.Disconnect();
                var lease = Read(broken);
                PumpUntil(() => lease.IsCompleted, "isolated lease request did not arrive");
                request = lease.GetAwaiter().GetResult();
                Assert(request.Automatic?.Action == "lease", "unexpected mutation before malformed lease response");
            }
            async Task InvalidReply()
            {
                if (scenario is "request-id" or "version" or "lease")
                    await Protocol.WriteAsync(broken, new HostResponse(scenario == "version" ? Protocol.Version + 1 : Protocol.Version,
                        scenario == "version" ? request.RequestId : Guid.NewGuid(), "Succeeded", null, "隔离无效回执"), timeout.Token);
                else if (scenario is "zero-length" or "oversized")
                {
                    byte[] header = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(header, scenario == "zero-length" ? 0 : Protocol.MaxFrameBytes + 1);
                    await broken.WriteAsync(header, timeout.Token);
                    await broken.FlushAsync(timeout.Token);
                }
                else if (scenario == "empty-message") await Protocol.WriteAsync<object?>(broken, null, timeout.Token);
                else await Protocol.WriteAsync(broken, "invalid HostResponse", timeout.Token);
            }
            var invalidReply = InvalidReply();
            PumpUntil(() => invalidReply.IsCompleted && (!Available() || poll.IsCompleted), "invalid reply did not invalidate the recording status");
            invalidReply.GetAwaiter().GetResult();
            Assert(!poll.IsFaulted, scenario + ": malformed response terminated recording polling: " + poll.Exception?.GetBaseException().Message);
            Assert(!Available() && State().Message.Contains("状态未知") && State().Control == active.Control,
                "invalid reply still claims a confirmed state or discards the last session");
            Assert(automaticEvents[^1].Message.Contains("连接中断"), "automatic recording was not notified about unknown state");

            using var fresh = Server();
            async Task ServeRecovery()
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        var next = await Read(fresh);
                        Assert(next.Capability == "recording.status" || next.Automatic?.Action == "lease", "poll recovery submitted a recording mutation");
                        await Protocol.WriteAsync(fresh, new HostResponse(Protocol.Version, next.RequestId, "Succeeded", null,
                            "隔离正常回执", Recording: new("Idle", "隔离轮询已恢复"),
                            Automatic: automatic with { Enabled = false, Message = "隔离自动状态已恢复" }), timeout.Token);
                        fresh.Disconnect();
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            var recovery = ServeRecovery();
            PumpUntil(() => Available() && State().Message == "隔离轮询已恢复", "valid response did not restore polling without restarting the client");
            Assert(!poll.IsCompleted && automaticEvents[^1].Message == "隔离自动状态已恢复", "manual and automatic polling did not both recover");
            timeout.Cancel();
            PumpUntil(() => recovery.IsCompleted, "recovery pipe did not stop");
            recovery.GetAwaiter().GetResult();
            Checks.Add(scenario + ": invalid status/lease receipt marks both recording views unknown, retains the session, and recovers through normal polling without capture or mutation");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            PumpUntil(() => poll.IsCompleted, "isolated recording poll did not stop");
            try { ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) when (poll.IsFaulted) { _ = poll.Exception; } // Preserve the regression assertion on the old implementation.
        }
    }
}
