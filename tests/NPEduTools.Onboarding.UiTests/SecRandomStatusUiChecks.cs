using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunSecRandomStatusChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            RunSecRandomAccessChecks();
            RunSecRandomRequestOrderingCheck();
            RunSecRandomLostReplyCheck();
            RunSecRandomSidebarLostReplyCheck();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunSecRandomLostReplyCheck()
    {
        string pipe = "NPEduTools.Test.secrandom-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = new SecRandomWindow(pipe);
        var render = typeof(SecRandomWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var ready = new SecRandomState("fixture.exe", 7, "Ready", DateTimeOffset.Now,
            new(Guid.NewGuid(), "quick-draw", "Succeeded", "隔离历史结果", DateTimeOffset.Now,
                Winner: new("fixture-student", "测试姓名", "")));
        try
        {
            Exercise(window, owner =>
            {
                render.Invoke(window, [ready]);
                Assert(Button(owner, "DrawButton").IsEnabled, "confirmed ready state blocks draw");
                async Task DropReply()
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                    Assert(request.Capability == "secrandom.command" && request.SecRandom?.Action == "quick-draw",
                        "unexpected request routed into isolated fixture");
                    server.Disconnect(); // Only this fake Host sees the request; no actual SecRandom or draw.
                }
                var reply = DropReply();
                var command = (Task)typeof(SecRandomWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, ["quick-draw", false, null])!;
                PumpUntil(() => command.IsCompleted && reply.IsCompleted, "dropped draw response did not settle");
                command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"), "lost response keeps an old ready connection label");
                Assert(((TextBlock)owner.FindName("OperationText")).Text.Contains("仅供核对") &&
                    ((TextBlock)owner.FindName("WinnerText")).Text.Contains("上次回执"), "disconnect hides history or presents the old winner as the new result");
                owner.Width = 620; owner.Height = 540;
                Snapshot(owner, "secrandom-unconfirmed.png");
                Descendants<ScrollViewer>(owner).First().ScrollToBottom(); owner.UpdateLayout();
                Snapshot(owner, "secrandom-history-minimum.png");
                Assert(!Button(owner, "DrawButton").IsEnabled, "lost draw response re-enables another draw using the old ready snapshot");
                var blocked = (Task)typeof(SecRandomWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, ["quick-draw", false, null])!;
                Assert(blocked.IsCompleted, "unknown state attempts another draw request");
                render.Invoke(window, [ready with { Operation = ready.Operation! with { State = "Unknown", Winner = null } }]);
                Assert(!Button(owner, "DrawButton").IsEnabled && Button(owner, "AcknowledgeButton").IsEnabled, "fresh unknown state bypasses manual reconciliation");
                render.Invoke(window, [ready with { ExecutablePath = null, Operation = null }]);
                Assert(!Button(owner, "DrawButton").IsEnabled && ((WrapPanel)owner.FindName("ConfigActions")).IsEnabled, "unconfigured state does not allow configuration or incorrectly permits drawing");
                Checks.Add("Real point-name window and isolated pipe: a lost draw response disables commands until a new Host snapshot confirms the result");
                Checks.Add("Disconnected history is labelled as a previous receipt; unknown requires reconciliation; unconfigured permits setup; minimum 620x540 layout");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }

    private static void RunSecRandomRequestOrderingCheck()
    {
        string pipe = "NPEduTools.Test.secrandom-order." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldStatus = Server();
        var window = new SecRandomWindow(pipe);
        var render = typeof(SecRandomWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
        var ready = new SecRandomState("fixture.exe", 7, "Ready", DateTimeOffset.Now, null);
        try
        {
            Exercise(window, owner =>
            {
                render.Invoke(window, [ready]);
                var oldRead = Read(oldStatus);
                PumpUntil(() => oldRead.IsCompleted, "initial status poll did not reach the isolated pipe");
                var oldRequest = oldRead.GetAwaiter().GetResult();
                Assert(oldRequest.Capability == "secrandom.status", "unexpected initial status request");
                using var commandServer = Server();
                SecRandomState? uncertain = null;
                async Task RespondToDraw()
                {
                    var request = await Read(commandServer);
                    Assert(request.Capability == "secrandom.command" && request.SecRandom?.Action == "quick-draw" && request.ExpectedRevision == 7,
                        "draw lost its expected revision or used the wrong capability");
                    uncertain = ready with { Revision = 8, Operation = new(request.RequestId, "quick-draw", "Unknown", "隔离结果待核实", DateTimeOffset.Now) };
                    await Protocol.WriteAsync(commandServer, new HostResponse(Protocol.Version, request.RequestId,
                        "Accepted", null, "隔离请求", SecRandom: uncertain), timeout.Token);
                }
                var reply = RespondToDraw();
                var command = (Task)typeof(SecRandomWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, ["quick-draw", false, null])!;
                PumpUntil(() => command.IsCompleted && reply.IsCompleted, "isolated draw response did not finish");
                command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(!Button(owner, "DrawButton").IsEnabled && Button(owner, "AcknowledgeButton").IsEnabled, "unknown response is not gated for manual reconciliation");
                var oldReply = Protocol.WriteAsync(oldStatus, new HostResponse(Protocol.Version, oldRequest.RequestId,
                    "Succeeded", null, "较早的隔离快照", SecRandom: ready), timeout.Token);
                PumpUntil(() => oldReply.IsCompleted, "old status reply did not finish");
                oldReply.GetAwaiter().GetResult();
                commandServer.Dispose();
                using var freshStatus = Server();
                var freshRead = Read(freshStatus);
                PumpUntil(() => freshRead.IsCompleted || Button(owner, "DrawButton").IsEnabled,
                    "old reply neither rendered nor allowed polling to continue");
                oldStatus.Dispose();
                Snapshot(owner, "secrandom-after-old-query.png");
                Assert(!Button(owner, "DrawButton").IsEnabled && ((TextBlock)owner.FindName("OperationText")).Text.Contains("待核实"),
                    "an old ready poll overwrote the new unknown operation and re-enabled draw");
                var recovered = uncertain! with { Operation = uncertain!.Operation! with { State = "Acknowledged", Message = "隔离核实完成" } };
                var freshRequest = freshRead.GetAwaiter().GetResult();
                var freshReply = Protocol.WriteAsync(freshStatus, new HostResponse(Protocol.Version, freshRequest.RequestId,
                    "Succeeded", null, "新快照", SecRandom: recovered), timeout.Token);
                PumpUntil(() => freshReply.IsCompleted, "fresh status reply did not finish");
                freshReply.GetAwaiter().GetResult();
                PumpUntil(() => Button(owner, "DrawButton").IsEnabled, "fresh acknowledged status did not restore commands");
                Assert(((TextBlock)owner.FindName("OperationText")).Text.Contains("已核实"), "fresh operation did not render");
                Checks.Add("Real poll/command ordering preserves the draw revision and newer unknown result; only a fresh acknowledged snapshot restores draw");
                using var brokenStatus = Server();
                async Task DropStatus()
                {
                    var request = await Read(brokenStatus);
                    Assert(request.Capability == "secrandom.status", "unexpected request after recovery");
                    brokenStatus.Disconnect();
                }
                var brokenReply = DropStatus();
                PumpUntil(() => brokenReply.IsCompleted && ((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"),
                    "failed current poll did not mark the backend unavailable");
                brokenReply.GetAwaiter().GetResult();
                Assert(!Button(owner, "DrawButton").IsEnabled && ((TextBlock)owner.FindName("OperationText")).Text.Contains("仅供核对"),
                    "current polling loss enables drawing or discards the historical receipt");
                Checks.Add("A current real status poll failing after recovery disables commands while retaining explicitly historical operation evidence");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }

    private static void RunSecRandomSidebarLostReplyCheck()
    {
        string pipe = "NPEduTools.Test.secrandom-sidebar." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = new SecRandomWindow(pipe);
        int draws = 0;
        async Task RespondUntilLoss()
        {
            SecRandomOperation? operation = null;
            while (true)
            {
                await server.WaitForConnectionAsync(timeout.Token);
                var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                if (request.Capability == "secrandom.command")
                {
                    Assert(request.SecRandom?.Action == "quick-draw" && request.ExpectedRevision == 7, "sidebar did not use the fresh draw configuration");
                    draws++;
                    operation = new(request.RequestId, "quick-draw", "Running", "隔离闪抽处理中", DateTimeOffset.Now);
                }
                else
                {
                    Assert(request.Capability == "secrandom.status", "sidebar requested an unexpected capability");
                    if (operation is not null) { server.Disconnect(); return; }
                }
                await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                    operation is null ? "Succeeded" : "Accepted", null, "隔离回执",
                    SecRandom: new("fixture.exe", 7, "Ready", DateTimeOffset.Now, operation)), timeout.Token);
                server.Disconnect();
            }
        }
        try
        {
            Exercise(window, owner =>
            {
                var replies = RespondUntilLoss();
                var command = window.QuickDrawFromSidebarAsync();
                PumpUntil(() => command.IsCompleted && replies.IsCompleted, "sidebar request did not settle after polling loss");
                replies.GetAwaiter().GetResult();
                Assert(command.GetAwaiter().GetResult().Contains("未收到可靠回执") && draws == 1, "sidebar retries the draw or claims success without a final receipt");
                Assert(!Button(owner, "DrawButton").IsEnabled && ((TextBlock)owner.FindName("OperationText")).Text.Contains("仅供核对"),
                    "sidebar reconnect guidance presents an old operation as current or enables repeat draw");
                Checks.Add("Sidebar uses a fresh revision, sends one draw and preserves its request receipt; a lost final status disables commands and never automatically redraws");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
