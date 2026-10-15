using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunRecordingMutationSerializationCheck(bool manualFirst, bool lostFirst)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.recording-mutations." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 3,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var status = Server();
        var statusRead = Read(status);
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        var poll = (Task)type.GetField("_poll", flags)!.GetValue(client)!;
        var active = new RecordingState("Recording", "隔离录制", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        var enabled = new AutomaticRecordingState(true, (Guid)type.GetField("_client", flags)!.GetValue(client)!, "隔离自动录课", null, []);
        var firstState = manualFirst ? active with { Phase = "Paused", Message = "隔离暂停已确认" } : active;
        var firstAuto = enabled with { Message = "隔离首项操作已确认" };
        var finalAuto = enabled with { Enabled = false, Message = "隔离最后关闭已确认" };
        Task Reply(NamedPipeServerStream server, HostRequest request, RecordingState recording, AutomaticRecordingState automatic) =>
            Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Succeeded", null, "隔离回执",
                Recording: recording, Automatic: automatic), timeout.Token);
        Task? first = null, second = null;
        try
        {
            PumpUntil(() => statusRead.IsCompleted, "initial poll did not arrive");
            Assert(statusRead.GetAwaiter().GetResult().Capability == "recording.status", "unexpected initial poll");
            // Hold the unrelated poll so only command receipts can change the cached state.
            type.GetMethod("Apply", flags)!.Invoke(client,
                [new HostResponse(1, Guid.NewGuid(), "Succeeded", null, "隔离初始状态", Recording: active, Automatic: enabled)]);
            using var firstServer = Server(); var firstRead = Read(firstServer);
            first = manualFirst ? (Task)type.GetMethod("SendAsync")!.Invoke(client, ["pause", null])!
                : (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["skip-day", null])!;
            PumpUntil(() => firstRead.IsCompleted, "first command did not arrive");
            var firstRequest = firstRead.GetAwaiter().GetResult();
            Assert(manualFirst ? firstRequest.Recording?.Action == "pause" : firstRequest.Automatic?.Action == "skip-day", "unexpected first command");

            using var secondServer = Server(); var secondRead = Read(secondServer);
            second = (Task)type.GetMethod("SetAutomaticAsync")!.Invoke(client, ["disable", null])!;
            var settle = Stopwatch.StartNew();
            PumpUntil(() => settle.Elapsed >= TimeSpan.FromMilliseconds(150), "dispatcher did not settle");
            bool overlapped = secondRead.IsCompleted;
            if (overlapped)
            {
                // Reproduce the original race: the later disable is confirmed before
                // the delayed earlier receipt arrives carrying Enabled=true.
                var later = Reply(secondServer, secondRead.GetAwaiter().GetResult(), firstState, finalAuto);
                PumpUntil(() => later.IsCompleted && second.IsCompleted, "later command did not settle");
                later.GetAwaiter().GetResult(); second.GetAwaiter().GetResult();
            }
            if (lostFirst)
            {
                firstServer.Dispose();
                PumpUntil(() => first.IsCompleted, "lost first command did not settle");
                if (manualFirst) first.GetAwaiter().GetResult(); // Manual failures become an explicit unknown state.
                else Assert(first.Exception?.GetBaseException() is System.IO.IOException, "lost automatic command did not report uncertainty");
            }
            else
            {
                var earlier = Reply(firstServer, firstRequest, firstState, firstAuto);
                PumpUntil(() => earlier.IsCompleted && first.IsCompleted, "first command did not settle");
                earlier.GetAwaiter().GetResult(); first.GetAwaiter().GetResult();
            }
            if (!overlapped)
            {
                PumpUntil(() => secondRead.IsCompleted, "queued automatic disable was dropped");
                var laterRequest = secondRead.GetAwaiter().GetResult();
                Assert(laterRequest.Automatic?.Action == "disable", "queued command lost its action");
                var later = Reply(secondServer, laterRequest, firstState, finalAuto);
                PumpUntil(() => later.IsCompleted && second.IsCompleted, "queued command did not settle");
                later.GetAwaiter().GetResult(); second.GetAwaiter().GetResult();
            }
            var observed = (AutomaticRecordingState)type.GetProperty("Automatic")!.GetValue(client)!;
            Assert(!overlapped && !observed.Enabled && observed.Message == finalAuto.Message,
                "recording mutations overlapped and an older receipt can overwrite the confirmed disable: overlapped=" + overlapped + ", enabled=" + observed.Enabled);
            Assert((bool)type.GetProperty("StateAvailable")!.GetValue(client)!, "serialized command result is not available");
            Assert(((RecordingState)type.GetProperty("State")!.GetValue(client)!).Phase == firstState.Phase, "automatic command lost the manual recording phase");
            Checks.Add($"Recording mutation serialization/manualFirst={manualFirst}/lostFirst={lostFirst}: delayed earlier receipt cannot overtake queued disable; a failed command releases the queue; fake pipe only");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            timeout.Cancel();
            PumpUntil(() => poll.IsCompleted && (first?.IsCompleted ?? true) && (second?.IsCompleted ?? true), "recording mutation fixture did not stop");
            _ = first?.Exception; _ = second?.Exception;
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
