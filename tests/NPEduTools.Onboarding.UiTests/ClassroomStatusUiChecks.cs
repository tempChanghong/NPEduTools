using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.ClassIsland.Admin;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static HostResponse ClassroomSetupResponse(string capability) => new(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "合成配置",
        Launch: capability == "classisland.config.get" ? new(new(1, "fixture-ci.exe"), null, null) : null,
        ExamAware: capability == "examaware.status" ? ReadyExamAware() : null);
    private static ClassroomSetupCheck SyntheticClassroomSetup(Func<string, CancellationToken, Task<HostResponse>>? read = null) =>
        new(read ?? ((capability, _) => Task.FromResult(ClassroomSetupResponse(capability))),
            _ => Task.FromResult(new AdminResult("Succeeded", "合成任务", new("Enabled", "已就绪", null, "Stopped", "", false))), _ => true);
    private static ClassroomModeWindow ClassroomWindow(string pipe, ClassroomSetupCheck? setup = null)
    {
        Action nothing = () => { };
        var constructor = typeof(ClassroomModeWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (ClassroomModeWindow)constructor.Invoke([pipe, nothing, nothing, nothing, setup ?? SyntheticClassroomSetup()]);
    }
    private static void ShowClassroom(ClassroomModeWindow window, ClassroomModeState state) =>
        typeof(ClassroomModeWindow).GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, [state]);
    private static ClassroomModeState ClassroomExam() => new(7, "Exam", "Incomplete", true, "隔离切换未完成",
        Actual: new("fixture-ci.exe", 1, false, "fixture-ea.exe", 7, true), CheckedAt: DateTimeOffset.Now,
        Recovery: new("Daily", false, new("fixture-ci.exe", 1, true, "fixture-ea.exe", 7, false)));
    private static bool ClassroomUnknown(ClassroomModeWindow window) =>
        typeof(ClassroomModeWindow).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null;
    private static void RunClassroomStatusChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try { RunClassroomHomePollChecks(); RunClassroomOrderingCheck(); RunClassroomLateSetupCheck(); RunClassroomDisconnectedCheck(); RunClassroomMissingSnapshotCheck(); RunClassroomSetupSupersededCheck(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunClassroomDisconnectedCheck()
    {
        string pipe = "NPEduTools.Test.classroom-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = ClassroomWindow(pipe);
        window.Width = 700; window.Height = 620;
        try
        {
            Exercise(window, owner =>
            {
                ShowClassroom(window, ClassroomExam());
                async Task<HostRequest> Read()
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                }
                var read = Read();
                PumpUntil(() => read.IsCompleted, "classroom status poll did not reach the fake Host");
                Assert(read.GetAwaiter().GetResult().Capability == "classroom.status", "unexpected isolated request");
                server.Disconnect();
                PumpUntil(() => ClassroomUnknown(window), "actual polling failure did not mark the cached state unavailable");
                Snapshot(owner, "classroom-disconnected.png");
                Assert(((TextBlock)owner.FindName("ModeTitle")).Text.Contains("未知") && ((TextBlock)owner.FindName("PauseText")).Text.Contains("无法核实"),
                    "disconnected classroom page still claims exam mode and a known recording pause");
                Assert(((TextBlock)owner.FindName("RecoveryText")).Text.Contains("未知") && !Button(owner, "RestoreButton").IsEnabled,
                    "disconnected page presents an old recovery target as current");
                var scroll = (ScrollViewer)window.Content;
                scroll.ScrollToBottom(); window.UpdateLayout(); Snapshot(owner, "classroom-disconnected-recovery.png");
                scroll.ScrollToTop();
                var reconnect = Read();
                PumpUntil(() => reconnect.IsCompleted, "classroom polling did not reconnect");
                var request = reconnect.GetAwaiter().GetResult();
                var response = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId,
                    "Succeeded", null, "隔离后台恢复", ClassroomMode: new(8, "Daily", Message: "隔离日常状态")), timeout.Token);
                PumpUntil(() => response.IsCompleted, "recovery response did not finish"); response.GetAwaiter().GetResult();
                PumpUntil(() => !ClassroomUnknown(window), "fresh snapshot did not recover the unknown classroom page");
                Assert(((TextBlock)owner.FindName("ModeTitle")).Text.Contains("日常模式") && Button(owner, "DailyButton").IsEnabled &&
                    !Button(owner, "RestoreButton").IsEnabled, "reconnected daily state did not restore the correct controls");
                Checks.Add("Real query failure displays unknown mode/pause/recovery; a fresh query restores the daily state and controls at minimum window size");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
    private static void RunClassroomMissingSnapshotCheck()
    {
        foreach (bool cancelled in new[] { true, false })
        {
            string pipe = "NPEduTools.Test.classroom-empty." + Guid.NewGuid().ToString("N");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var window = ClassroomWindow(pipe);
            try
            {
                Exercise(window, owner =>
                {
                    // Hold the initial poll so only the real read-only refresh renders a reply.
                    var initial = server.WaitForConnectionAsync(timeout.Token);
                    PumpUntil(() => initial.IsCompleted, "initial poll did not connect"); initial.GetAwaiter().GetResult();
                    var initialRead = Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                    PumpUntil(() => initialRead.IsCompleted, "initial query did not arrive"); initialRead.GetAwaiter().GetResult();
                    server.Disconnect();
                    ShowClassroom(window, new(8, "Daily", Message: "已核实日常状态"));
                    async Task Reply()
                    {
                        await server.WaitForConnectionAsync(timeout.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                        Assert(request.Capability == "classroom.refresh" && request.ClassroomMode is null, "fixture sent a mode mutation");
                        // A synthetic cancellation exercises local feedback without opening any authorization dialog.
                        await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Rejected",
                            cancelled ? "MANAGEMENT_CANCELLED" : "Unavailable", cancelled ? "隔离取消" : "隔离无状态回执"), timeout.Token);
                    }
                    var reply = Reply();
                    var command = (Task)typeof(ClassroomModeWindow).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, ["classroom.refresh", null, null, false])!;
                    PumpUntil(() => command.IsCompleted && reply.IsCompleted, "empty mode reply did not settle");
                    command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                    Assert(cancelled ? !ClassroomUnknown(window) && Button(owner, "DailyButton").IsEnabled :
                        ClassroomUnknown(window) && !Button(owner, "DailyButton").IsEnabled,
                        cancelled ? "local cancellation discarded the known state or left controls stuck disabled" : "reply without a mode snapshot retained actionable stale state");
                    Assert(((TextBlock)owner.FindName("StatusText")).Text == (cancelled ? "隔离取消" : "隔离无状态回执"), "request feedback was overwritten during final rendering");
                    window.Shutdown();
                });
            }
            finally { window.Shutdown(); }
        }
        Checks.Add("Replies without snapshots mark state unknown; synthetic local cancellation retains the known snapshot and restores controls without any mutation or dialog");
    }
    private static void RunClassroomLateSetupCheck()
    {
        string pipe = "NPEduTools.Test.classroom-setup." + Guid.NewGuid().ToString("N");
        var pending = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = SyntheticClassroomSetup((capability, token) => capability == "classisland.config.get"
            ? pending.Task.WaitAsync(token) : Task.FromResult(ClassroomSetupResponse(capability)));
        var window = ClassroomWindow(pipe, setup);
        bool Checking() => (bool)typeof(ClassroomModeWindow).GetField("_setupBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            Exercise(window, owner =>
            {
                ShowClassroom(window, ClassroomExam());
                Assert(Checking(), "visible window did not start the synthetic configuration check");
                typeof(ClassroomModeWindow).GetMethod("Disconnected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["隔离后台失联"]);
                pending.SetResult(ClassroomSetupResponse("classisland.config.get"));
                PumpUntil(() => !Checking(), "late synthetic setup report did not settle");
                Snapshot(owner, "classroom-late-setup.png");
                Assert(!((TextBlock)owner.FindName("SetupSummary")).Text.Contains("四项已就绪") && ((ItemsControl)owner.FindName("SetupItems")).Items.Count == 0,
                    "a late successful configuration report overwrites the disconnected warning with obsolete ready items");
                Checks.Add("A configuration check started before disconnection cannot repopulate obsolete ready items after its report arrives");
                ShowClassroom(window, new(8, "Daily", Message: "隔离恢复"));
                var fresh = (Task<bool>)typeof(ClassroomModeWindow).GetMethod("CheckSetupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                PumpUntil(() => fresh.IsCompleted, "new setup check after recovery did not finish");
                Assert(fresh.GetAwaiter().GetResult() && ((ItemsControl)owner.FindName("SetupItems")).Items.Count == 4, "new configuration check cannot recover after an obsolete result was rejected");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
    private static void RunClassroomSetupSupersededCheck()
    {
        string pipe = "NPEduTools.Test.classroom-check-order." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var pending = new TaskCompletionSource<HostResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = SyntheticClassroomSetup((capability, token) => capability == "classisland.config.get"
            ? pending.Task.WaitAsync(token) : Task.FromResult(ClassroomSetupResponse(capability)));
        var window = ClassroomWindow(pipe, setup);
        bool Checking() => (bool)typeof(ClassroomModeWindow).GetField("_setupBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        try
        {
            Exercise(window, owner =>
            {
                ShowClassroom(window, new(8, "Daily", Message: "原有状态"));
                Assert(Checking(), "setup fixture did not start checking");
                async Task Reply()
                {
                    await server.WaitForConnectionAsync(timeout.Token);
                    var request = await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                    Assert(request.Capability == "classroom.refresh", "unexpected setup-order request");
                    await Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                        "新核实结果", ClassroomMode: new(9, "Daily", Message: "新状态")), timeout.Token);
                }
                var reply = Reply();
                var command = (Task)typeof(ClassroomModeWindow).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, ["classroom.refresh", null, null, false])!;
                PumpUntil(() => command.IsCompleted && reply.IsCompleted, "new verification during setup did not finish");
                command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                pending.SetResult(ClassroomSetupResponse("classisland.config.get"));
                PumpUntil(() => !Checking(), "superseded configuration check did not settle");
                Assert(((TextBlock)owner.FindName("SetupSummary")).Text.Contains("重新检查") &&
                    ((ItemsControl)owner.FindName("SetupItems")).Items.Count == 0 && Button(owner, "SetupRefresh").IsEnabled,
                    "superseded setup report remains ready or leaves an endless checking message");
                Assert(((TextBlock)owner.FindName("StatusText")).Text == "新核实结果", "old setup completion overwrote new operation feedback");
                Checks.Add("Configuration completion superseded by a newer verification requests a fresh check instead of leaving ready items or an endless checking message");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
    private static void RunClassroomOrderingCheck()
    {
        string pipe = "NPEduTools.Test.classroom-order." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldStatus = Server();
        var window = ClassroomWindow(pipe);
        var current = new ClassroomModeState(8, "Daily", Message: "隔离新核实状态");
        try
        {
            Exercise(window, owner =>
            {
                string Title() => ((TextBlock)owner.FindName("ModeTitle")).Text;
                ShowClassroom(window, ClassroomExam());
                var oldRead = Read(oldStatus);
                PumpUntil(() => oldRead.IsCompleted, "initial mode poll did not reach the fake pipe");
                var oldRequest = oldRead.GetAwaiter().GetResult();
                Assert(oldRequest.Capability == "classroom.status", "unexpected initial mode query");
                using var commandServer = Server();
                async Task ReplyToRefresh()
                {
                    var request = await Read(commandServer);
                    Assert(request.Capability == "classroom.refresh" && request.ClassroomMode is null && request.ExpectedRevision is null && Protocol.Validate(request) is null,
                        "read-only refresh changed mode or carried unexpected revision parameters");
                    await Protocol.WriteAsync(commandServer, new HostResponse(Protocol.Version, request.RequestId,
                        "Succeeded", null, "隔离只读核实", ClassroomMode: current), timeout.Token);
                }
                var reply = ReplyToRefresh();
                var command = (Task)typeof(ClassroomModeWindow).GetMethod("SendAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, ["classroom.refresh", null, null, false])!;
                PumpUntil(() => command.IsCompleted && reply.IsCompleted, "read-only refresh did not finish");
                command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                Assert(Title().Contains("日常模式"), "fresh read-only result did not render");
                var oldReply = Protocol.WriteAsync(oldStatus, new HostResponse(Protocol.Version, oldRequest.RequestId,
                    "Succeeded", null, "较早的隔离状态", ClassroomMode: ClassroomExam()), timeout.Token);
                PumpUntil(() => oldReply.IsCompleted, "old mode reply did not finish");
                oldReply.GetAwaiter().GetResult(); commandServer.Dispose();
                using var freshStatus = Server();
                var freshRead = Read(freshStatus);
                PumpUntil(() => freshRead.IsCompleted || Title().Contains("考试模式"), "mode poll did not continue after the older reply");
                oldStatus.Dispose();
                Snapshot(owner, "classroom-after-old-query.png");
                Assert(Title().Contains("日常模式"), "an older poll overwrote the latest confirmed classroom mode after read-only verification");
                var freshRequest = freshRead.GetAwaiter().GetResult();
                var response = Protocol.WriteAsync(freshStatus, new HostResponse(Protocol.Version, freshRequest.RequestId,
                    "Succeeded", null, "新恢复状态", ClassroomMode: ClassroomExam() with { Revision = 9 }), timeout.Token);
                PumpUntil(() => response.IsCompleted, "fresh mode reply did not finish");
                response.GetAwaiter().GetResult();
                PumpUntil(() => Title().Contains("切换未完成"), "fresh incomplete state did not render");
                Assert(Button(owner, "RestoreButton").IsEnabled && !Button(owner, "DailyButton").IsEnabled,
                    "fresh recovery state does not offer restoration or allows a new switch");
                Checks.Add("Real read-only verification outranks an older query; a new incomplete snapshot still offers restoration and blocks a new mode switch");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
