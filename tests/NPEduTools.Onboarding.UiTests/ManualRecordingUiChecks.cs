using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunManualRecordingChecks()
    {
        string pipe = "NPEduTools.Test.manual-ui." + Guid.NewGuid().ToString("N");
        var type = typeof(RecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(type, [Application.Current.Dispatcher, pipe])!;
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        RecordingState? latest = null;
        bool offline = false;
        type.GetEvent("Changed")!.AddEventHandler(client, (Action<RecordingState>)(state => latest = state));
        type.GetEvent("AutomaticChanged")!.AddEventHandler(client, (Action<AutomaticRecordingState>)(state => offline |= state.Message.Contains("连接中断")));
        var apply = type.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var active = new RecordingState("Recording", "正在录制隔离测试", Seconds: 120,
            Control: new("Manual", Guid.NewGuid(), "", 0, 0));
        try
        {
            apply.Invoke(client, [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离快照", Recording: active)]);
            PumpUntil(() => offline, "nonexistent pipe did not produce a disconnected notification");
            Assert(latest?.Message.Contains("状态未知") == true, "manual recording still reports its old active state after the actual pipe poll failed");
            Assert(!Available() && latest?.Control == active.Control, "disconnect discarded the last known session or still claims a fresh state");
            Checks.Add("Real poll against a nonexistent test pipe notifies manual recording that its current state is unknown");
            type.GetMethod("Detach")!.Invoke(client, null);
            var poll = (Task)type.GetField("_poll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            PumpUntil(() => poll.IsCompleted, "poll remained active during rendering tests");

            var idle = new RecordingState("Idle", "准备录制");
            void Show(RecordingState state) => apply.Invoke(client,
                [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离快照", Recording: state)]);
            Show(idle);
            int probes = 0;
            Func<Task<RecordingEnvironment>> probe = () =>
            {
                probes++;
                return Task.FromResult(new RecordingEnvironment(true, null,
                    [new("fixture", "隔离测试屏幕", 0, 0, 1920, 1080, true)], [], []));
            };
            var constructor = typeof(RecordingWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(c => c.GetParameters().Length == 3);
            var window = (RecordingWindow)constructor.Invoke([client, pipe, probe]);
            try
            {
                Exercise(window, owner =>
                {
                    string Text(string name) => ((TextBlock)owner.FindName(name)).Text;
                    Assert(probes == 1 && Button(owner, "RecordStart").IsEnabled, "synthetic device environment did not load");
                    Show(active with { Frames = 100, Bytes = 1048576 });
                    Assert(Text("RecordingClock") == "00:02:00" && Text("RecordingPhase") == "录制中", "confirmed recording does not render");
                    var unavailable = type.GetMethod("MarkUnavailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    unavailable.Invoke(client, [null, null]);
                    Assert(Text("RecordingClock") == "—" && Text("RecordingPhase") == "状态未知", "unknown window retains live clock or phase");
                    Assert(!Text("RecordingMetrics").Contains("100") && Text("RecordingMetrics").Contains("旧时长"), "unknown window retains live metrics");
                    Assert(!Button(owner, "RecordStart").IsEnabled && !Button(owner, "RecordPause").IsEnabled && !Button(owner, "RecordStop").IsEnabled, "unknown window still enables commands");
                    owner.Width = 540; owner.Height = 630;
                    Snapshot(owner, "manual-disconnected-minimum.png");
                    var scroll = Descendants<ScrollViewer>(owner).First();
                    scroll.ScrollToBottom(); owner.UpdateLayout();
                    Snapshot(owner, "manual-disconnected-details.png");
                    Show(new RecordingState("Saved", "已保存", OutputFile: "fixture.mp4"));
                    Assert(Available() && Text("RecordingPhase") == "已保存" && Button(owner, "OpenVideo").Visibility == Visibility.Visible, "fresh saved result did not recover controls and video entry");
                    Show(idle);
                    Assert(Button(owner, "RecordStart").IsEnabled && !Text("RecordingMetrics").Contains("100"), "fresh idle state keeps old recording data or blocks start");
                    unavailable.Invoke(client, [null, "本次操作未确认；隔离连接中断"]);
                    Assert(Text("RecordingError").Contains("连接中断"), "unconfirmed command error is missing");
                    Show(idle);
                    Assert(Text("RecordingError") == "", "fresh idle recovery leaves a stale disconnected-command error");
                    var localFeedback = (TextBlock)owner.FindName("RecordingError");
                    localFeedback.Text = "隔离设置校验提示";
                    Show(idle);
                    Assert(Text("RecordingError") == "隔离设置校验提示", "idle state cleared unrelated settings feedback");
                    localFeedback.Text = "";
                    Snapshot(owner, "manual-recovered-minimum.png");
                    Checks.Add("Real recording window: unknown clears clock/metrics and disables commands; fresh saved/idle snapshots restore; 540x630 layout uses only synthetic devices");
                    window.Shutdown();
                });
            }
            finally { window.Shutdown(); }

            Action nothing = () => { };
            var quick = new QuickAccessWindow(pipe, nothing, nothing, nothing, nothing, _ => { }, nothing, nothing);
            try
            {
                quick.UpdateRecording(active, false);
                Assert(((TextBlock)quick.FindName("RecordingStatus")).Text.Contains("未确认") &&
                    !((Button)quick.FindName("RecordingPause")).IsEnabled, "sidebar claims a live state while disconnected");
                quick.UpdateRecording(active, true);
                Assert(((TextBlock)quick.FindName("RecordingStatus")).Text.Contains("00:02:00") &&
                    ((Button)quick.FindName("RecordingPause")).IsEnabled, "sidebar does not recover on a fresh state");
                Checks.Add("Actual sidebar controls: unknown hides stale timing/control actions; a confirmed recording restores them");
            }
            finally { quick.Shutdown(); }
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            var poll = (Task)type.GetField("_poll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            PumpUntil(() => poll.IsCompleted, "isolated client did not stop");
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        RunRecordingRequestOrderingCheck();
        RunRecordingPollRecoveryChecks();
        RunManualReceiptRecoveryChecks();
    }

    private static void RunRecordingRequestOrderingCheck()
    {
        string pipe = "NPEduTools.Test.recording-order." + Guid.NewGuid().ToString("N");
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
        var changes = new List<RecordingState>();
        type.GetEvent("Changed")!.AddEventHandler(client, (Action<RecordingState>)(changes.Add));
        bool Available() => (bool)type.GetProperty("StateAvailable")!.GetValue(client)!;
        try
        {
            var oldRead = Read(oldStatus);
            PumpUntil(() => oldRead.IsCompleted, "initial status request did not reach the isolated pipe");
            Assert(oldRead.GetAwaiter().GetResult().Capability == "recording.status", "unexpected initial request");
            var active = new RecordingState("Recording", "隔离录制", Control: new("Manual", Guid.NewGuid(), "", 0, 0));
            type.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client,
                [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离快照", Recording: active)]);
            using var commandServer = Server();
            async Task RespondToPause()
            {
                var request = await Read(commandServer);
                Assert(request.Recording?.Action == "pause" && request.Recording.Control == active.Control, "pause lost its expected session fence");
                await Protocol.WriteAsync(commandServer, new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离暂停", Recording: active with { Phase = "Paused", Message = "隔离暂停已确认" }), timeout.Token);
            }
            var reply = RespondToPause();
            var command = (Task)type.GetMethod("SendAsync")!.Invoke(client, ["pause", null])!;
            PumpUntil(() => command.IsCompleted && reply.IsCompleted, "isolated pause did not finish");
            command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
            Assert(Available() && changes[^1].Phase == "Paused", "pause response did not confirm the new state");
            changes.Clear();
            oldStatus.Dispose(); // Delayed failure from a query started before that successful command.
            commandServer.Dispose();
            using var freshStatus = Server();
            var freshRead = Read(freshStatus);
            PumpUntil(() => freshRead.IsCompleted, "poll did not continue after the old request failed");
            var freshRequest = freshRead.GetAwaiter().GetResult();
            Assert(Available() && changes.All(state => !state.Message.Contains("状态未知")), "obsolete query failure overwrote a newer command result");

            type.GetMethod("MarkUnavailable", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [null, null]);
            long generation = (long)type.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            var blocked = (Task)type.GetMethod("SendAsync")!.Invoke(client, ["start", null])!;
            Assert(blocked.IsCompleted && generation == (long)type.GetField("_generation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!, "unknown state submitted a new recording command");
            var recoveredReply = Protocol.WriteAsync(freshStatus, new HostResponse(Protocol.Version, freshRequest.RequestId,
                "Succeeded", null, "隔离恢复", Recording: new("Idle", "恢复后准备录制")), timeout.Token);
            PumpUntil(() => recoveredReply.IsCompleted, "isolated status reply did not finish");
            recoveredReply.GetAwaiter().GetResult();
            PumpUntil(() => Available() && changes[^1].Phase == "Idle", "fresh polling response did not recover unknown state");
            Checks.Add("Isolated real pipe requests: keep expected session fencing; an old query failure cannot mask a later pause result; unknown blocks new start; fresh poll recovers");

            using var lostCommand = Server();
            async Task LoseStartResponse()
            {
                while (true)
                {
                    var request = await Read(lostCommand);
                    if (request.Capability == "recording.command")
                    {
                        Assert(request.Recording?.Action == "start", "unexpected synthetic command");
                        lostCommand.Disconnect(); return;
                    }
                    // The normal client may renew its lease between test steps.
                    await Protocol.WriteAsync(lostCommand, new HostResponse(Protocol.Version, request.RequestId,
                        "Succeeded", null, "隔离状态", Recording: new("Idle", "准备录制")), timeout.Token);
                    lostCommand.Disconnect();
                }
            }
            var lostReply = LoseStartResponse();
            var start = (Task)type.GetMethod("SendAsync")!.Invoke(client,
                ["start", new RecordingOptions("fixture", System.IO.Path.GetTempPath())])!;
            PumpUntil(() => start.IsCompleted && lostReply.IsCompleted, "lost command response did not settle");
            start.GetAwaiter().GetResult(); lostReply.GetAwaiter().GetResult();
            Assert(!Available() && changes[^1].Phase == "Idle" && changes[^1].Message.Contains("状态未知"), "lost start response leaves an optimistic starting phase or claims a known result");
            Checks.Add("A dropped start-command response retains the previously confirmed idle state for reconciliation and marks current status unknown");
        }
        finally
        {
            type.GetMethod("Detach")!.Invoke(client, null);
            var poll = (Task)type.GetField("_poll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!;
            PumpUntil(() => poll.IsCompleted, "request-order fixture did not stop");
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

}
