using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static ExamAwareStatus ReadyExamAware(string name = "隔离旧方案") => new("fixture.exe", 7, "Connected",
        "隔离桥接已连接", "1.5.2", false, true, 12345, CanSetAutoStart: true, CanPresent: true,
        PreparedPlan: new(Guid.NewGuid(), new string('a', 64), name, "", [new("隔离考试", "09:00", "10:00", 5)]),
        Player: new(true, []));
    private static void ShowExamAware(ExamAwareWindow window, ExamAwareStatus? state) =>
        typeof(ExamAwareWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, [state]);
    private static Task RunExamAware(ExamAwareWindow window, string capability, ExamAwarePlanInput? plan = null) =>
        (Task)typeof(ExamAwareWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [capability, false, null, plan is null ? null : (object)7L, plan])!;
    private static void RunExamAwareStatusChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try { RunExamAwareProtocolChecks(); RunExamAwareQuickChecks(); RunExamAwareOrderingCheck(); RunExamAwareOrderingCheck(true); RunExamAwareLostReplyCheck(); RunExamAwareExportCheck(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunExamAwareLostReplyCheck()
    {
        string pipe = "NPEduTools.Test.examaware-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = new ExamAwareWindow(pipe);
        try
        {
            Exercise(window, owner =>
            {
                Assert(!((WrapPanel)owner.FindName("Actions")).IsEnabled, "unconfirmed initial backend permits commands");
                ShowExamAware(window, ReadyExamAware());
                Assert(Button(owner, "PresentPlanButton").IsEnabled && Button(owner, "EnableAutoStartButton").IsEnabled, "confirmed idle bridge did not enable configured operations");
                async Task<HostRequest> Read()
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                }
                var read = Read();
                var command = RunExamAware(window, "examaware.start");
                PumpUntil(() => read.IsCompleted, "start did not reach the isolated fake Host");
                var request = read.GetAwaiter().GetResult();
                Assert(request.Capability == "examaware.start" && Protocol.Validate(request) is null, "unexpected isolated command");
                bool presentWhileBusy = Button(owner, "PresentPlanButton").IsEnabled;
                Snapshot(owner, "examaware-pending.png");
                server.Disconnect();
                PumpUntil(() => command.IsCompleted, "lost start response did not settle");
                command.GetAwaiter().GetResult();
                Snapshot(owner, "examaware-unconfirmed.png");
                Assert(((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"), "lost operation reply leaves the old connected bridge state visible");
                Assert(!presentWhileBusy && !Button(owner, "PresentPlanButton").IsEnabled && !Button(owner, "EnableAutoStartButton").IsEnabled,
                    "pending/lost command still permits presentation or startup changes");
                Assert(((TextBlock)owner.FindName("AutoStartText")).Text.Contains("未知") &&
                    ((TextBlock)owner.FindName("PlayerStateText")).Text.Contains("未知"), "lost reply preserves old startup/player facts");
                Assert(typeof(ExamAwareWindow).GetField("_prepared", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
                    "lost reply retains an actionable prepared plan");
                var blocked = RunExamAware(window, "examaware.start");
                Assert(blocked.IsCompleted, "unknown state attempted a new start request");
                owner.Width = 650; owner.Height = 540;
                ((TextBlock)owner.FindName("Connection")).BringIntoView(); owner.UpdateLayout();
                Snapshot(owner, "examaware-disconnected-connection.png");
                ((TextBlock)owner.FindName("PlanSummaryText")).BringIntoView(); owner.UpdateLayout();
                Snapshot(owner, "examaware-disconnected-plan.png");
                Checks.Add("Real examination window and fake pipe: pending operation gates presentation; lost reply shows unknown connection/startup/player and disables commands");
                var path = (TextBox)owner.FindName("Executable");
                path.Text = "edited-fixture.exe";
                ShowExamAware(window, ReadyExamAware() with { BridgeState = "Disconnected", Message = "隔离桥接未连接", CanPresent = false, CanSetAutoStart = false });
                Assert(((WrapPanel)owner.FindName("Actions")).IsEnabled && ((WrapPanel)owner.FindName("PairingActions")).IsEnabled &&
                    !Button(owner, "PreparePlanButton").IsEnabled, "known Host without a bridge blocks repair or enables presentation");
                ShowExamAware(window, ReadyExamAware());
                Assert(path.Text == "edited-fixture.exe" && Button(owner, "PresentPlanButton").IsEnabled && Button(owner, "QuitButton").IsEnabled,
                    "fresh idle bridge does not restore controls or overwrites the path draft");
                typeof(ExamAwareWindow).GetField("_allowPreparedPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
                ShowExamAware(window, ReadyExamAware());
                Assert(!Button(owner, "PresentPlanButton").IsEnabled && Button(owner, "PreparePlanButton").IsEnabled,
                    "an invalid local replacement file re-enables the old prepared plan");
                Checks.Add("A fresh disconnected bridge allows repair; an idle bridge restores controls without overwriting path edits; invalid-file gate still rejects an old prepared plan; 650x540 layout");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
    private static void RunExamAwareOrderingCheck(bool invalidOld = false)
    {
        string pipe = "NPEduTools.Test.examaware-order." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldStatus = Server();
        var window = new ExamAwareWindow(pipe);
        var original = ReadyExamAware();
        var prepared = ReadyExamAware("隔离新方案");
        try
        {
            Exercise(window, owner =>
            {
                string Summary() => ((TextBlock)owner.FindName("PlanSummaryText")).Text;
                ShowExamAware(window, original);
                var oldRead = Read(oldStatus);
                PumpUntil(() => oldRead.IsCompleted, "initial examination poll did not reach the fake pipe");
                var oldRequest = oldRead.GetAwaiter().GetResult();
                Assert(oldRequest.Capability == "examaware.status", "unexpected initial request");
                using var commandServer = Server();
                async Task ReplyToPrepare()
                {
                    var request = await Read(commandServer);
                    Assert(request.Capability == "examaware.plan" && request.ExamPlan?.Action == "prepare" && request.ExpectedRevision == 7 && Protocol.Validate(request) is null,
                        "plan command lost its expected revision or protocol bounds");
                    await Protocol.WriteAsync(commandServer, new HostResponse(Protocol.Version, request.RequestId,
                        "Accepted", null, "隔离方案校验回执", ExamAware: prepared), timeout.Token);
                }
                var reply = ReplyToPrepare();
                // The fake Host supplies its own summary; this does not validate an actual exam file or call the bridge.
                var command = RunExamAware(window, "examaware.plan", new("prepare", Convert.ToBase64String("{}"u8.ToArray())));
                PumpUntil(() => command.IsCompleted && reply.IsCompleted, "isolated plan response did not finish");
                command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(Summary().Contains("隔离新方案"), "new preparation receipt did not render");
                var oldReply = Protocol.WriteAsync(oldStatus, new HostResponse(Protocol.Version, invalidOld ? Guid.NewGuid() : oldRequest.RequestId,
                    "Succeeded", null, "较早的隔离状态", ExamAware: original), timeout.Token);
                PumpUntil(() => oldReply.IsCompleted, "old examination status reply did not finish");
                oldReply.GetAwaiter().GetResult(); commandServer.Dispose();
                using var freshStatus = Server();
                var freshRead = Read(freshStatus);
                PumpUntil(() => freshRead.IsCompleted || Summary().Contains("隔离旧方案"), "poll did not continue after the old reply");
                oldStatus.Dispose();
                Snapshot(owner, invalidOld ? "examaware-after-old-invalid-query.png" : "examaware-after-old-query.png");
                Assert(Summary().Contains("隔离新方案") && !Summary().Contains("隔离旧方案"), "an older poll restored the previous exam plan after a new preparation receipt");
                var playing = prepared with { Player = new(true, [new("fixture-session", "ready", "隔离已放映")]) };
                var freshRequest = freshRead.GetAwaiter().GetResult();
                var freshReply = Protocol.WriteAsync(freshStatus, new HostResponse(Protocol.Version, freshRequest.RequestId,
                    "Succeeded", null, "新放映状态", ExamAware: playing), timeout.Token);
                PumpUntil(() => freshReply.IsCompleted, "fresh examination status reply did not finish");
                freshReply.GetAwaiter().GetResult();
                PumpUntil(() => ((TextBlock)owner.FindName("PlayerStateText")).Text.Contains("隔离已放映"), "current player snapshot did not render");
                Assert(!Button(owner, "PresentPlanButton").IsEnabled, "an active presentation permits a second presentation");
                Checks.Add((invalidOld ? "Old invalid query: " : "") + "Real status/plan request ordering retains revision checking and the new prepared summary; fresh active-player status blocks duplicate presentation");
                using var brokenStatus = Server();
                async Task DropStatus()
                {
                    var request = await Read(brokenStatus);
                    Assert(request.Capability == "examaware.status", "unexpected poll after a current presentation");
                    brokenStatus.Disconnect();
                }
                var loss = DropStatus();
                PumpUntil(() => loss.IsCompleted && ((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"), "current poll loss does not clear the old bridge state");
                loss.GetAwaiter().GetResult();
                Assert(!Button(owner, "PresentPlanButton").IsEnabled && ((TextBlock)owner.FindName("PlanSummaryText")).Text.Contains("未知"),
                    "current polling loss treats the old prepared summary as current");
                Checks.Add("Real current status poll loss clears actionable preparation and displays unknown presentation state");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
    private static void RunExamAwareExportCheck()
    {
        string pipe = "NPEduTools.Test.examaware-export." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string blocker = System.IO.Path.Combine(_output, "export-blocker-" + Guid.NewGuid().ToString("N"));
        string output = System.IO.Path.Combine(_output, "synthetic-pairing-" + Guid.NewGuid().ToString("N") + ".json");
        var window = new ExamAwareWindow(pipe);
        async Task Reply(bool lose = false)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
            Assert(request.Capability == "examaware.pairing.get", "export used an unexpected capability");
            if (!lose)
                await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离配对", ExamAware: ReadyExamAware() with { Message = "隔离最新桥接状态" },
                    ExamAwarePairing: new(1, "127.0.0.1", 12345, "fixture-not-a-real-key")), timeout.Token);
            server.Disconnect();
        }
        Task Export(string path) => (Task)typeof(ExamAwareWindow).GetMethod("ExportPairingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [path])!;
        try
        {
            System.IO.File.WriteAllText(blocker, "synthetic file blocks a child path");
            Exercise(window, owner =>
            {
                ShowExamAware(window, ReadyExamAware());
                var reply = Reply();
                var export = Export(System.IO.Path.Combine(blocker, "pairing.json"));
                PumpUntil(() => export.IsCompleted && reply.IsCompleted, "export file failure did not finish");
                export.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(((TextBlock)owner.FindName("Message")).Text.Contains("未能导出") &&
                    ((TextBlock)owner.FindName("Connection")).Text == "隔离最新桥接状态" && Button(owner, "QuitButton").IsEnabled,
                    "local file-write failure falsely marks the backend disconnected or leaves confirmed controls disabled");
                reply = Reply(true); export = Export(output);
                PumpUntil(() => export.IsCompleted && reply.IsCompleted, "lost export response did not finish");
                export.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(!System.IO.File.Exists(output) && !Button(owner, "QuitButton").IsEnabled &&
                    ((TextBlock)owner.FindName("Connection")).Text.Contains("未确认"), "lost export response claims connection or creates a pairing file");
                Checks.Add("Pairing export: local file failure retains a freshly confirmed bridge; transport failure shows unknown state and writes no pairing file; no real credentials or file dialog");
                window.Shutdown();
            });
        }
        finally
        {
            window.Shutdown();
            System.IO.File.Delete(blocker);
            if (System.IO.File.Exists(output)) System.IO.File.Delete(output);
        }
    }
}
