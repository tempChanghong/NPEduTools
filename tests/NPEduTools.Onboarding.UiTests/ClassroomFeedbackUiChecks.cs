using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassroomAcceptedFeedbackChecks()
    {
        var live = new NoiseProtectionState(true, true, Guid.NewGuid(), Guid.NewGuid());
        var switchTarget = new HostRequest(1, Guid.NewGuid(), "classroom.retry", ExpectedRevision: 7,
            ClassroomMode: new("Daily", true, true));
        var proof = new ClassroomActionPolicyState(new string('a', 64), "usb", "both", "either");
        Assert(ClassroomActionAccess.AuthorizeCommand(live, switchTarget, "default", "fixture-only").Purpose == "change-environment" &&
            ClassroomActionAccess.Blocked(live with { ClassroomPolicy = proof }, "either", "switch-classroom-mode")?.Contains("先到") == true &&
            ClassroomActionAccess.Blocked(live with { ClassroomPolicy = proof with { SwitchClassroomMode = "deny" } }, "deny", "switch-classroom-mode")?.Contains("禁止现场") == true,
            "inherited switch must retain the old ticket; deny and protected-stop rules must precede a new switch authorization");
        Checks.Add("Scheduled classroom switching distinguishes deny, independent stop, and legacy interruption scope");
        foreach (string scenario in new[] { "success", "failure", "incomplete", "superseded", "older", "fast-success", "fast-failure" })
            RunClassroomAcceptedFeedbackCheck(scenario);
    }

    private static void RunClassroomAcceptedFeedbackCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-accepted." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = ClassroomWindow(pipe);
        var initialState = new ClassroomModeState(7, Message: "尚未设置课堂模式。");
        var startup = new ClassroomStartupSnapshot("fixture-ci.exe", 1, true, "fixture-ea.exe", 1, false);
        ClassroomModeState? accepted = null;
        string Text() => ((TextBlock)window.FindName("OperationFeedbackText")).Text;
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(deadline.Token);
            return await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
        }
        async Task Initial()
        {
            var request = await Read();
            Assert(request.Capability == "classroom.status", "unexpected initial switch query");
            await Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Succeeded", null, "fixture", ClassroomMode: initialState), deadline.Token);
        }
        async Task Accept()
        {
            var protection = await Read();
            Assert(protection.Capability == ClassroomActionAccess.SwitchStatusCapability, "confirmation path did not check classroom mode permissions");
            if (scenario == "older")
            {
                foreach (var fallback in new[] { ClassroomActionAccess.StatusCapability, "noise.management.status" })
                {
                    await Protocol.WriteAsync(server, new HostResponse(1, protection.RequestId, "Rejected", "UnknownCapability", "older host"), deadline.Token);
                    server.Disconnect(); protection = await Read();
                    Assert(protection.Capability == fallback, "older host capability negotiation lost its current stop policy");
                }
            }
            var policy = scenario == "fast-success" ? new ClassroomActionPolicyState(new string('b',64), "deny", "deny", "allow") : null;
            var current = new NoiseProtectionState(false, false, Guid.NewGuid(), null, ManagementSource: "School", ClassroomPolicy: policy);
            await Protocol.WriteAsync(server, new HostResponse(1, protection.RequestId, "Succeeded", null, "fixture",
                NoiseProtection: current), deadline.Token);
            server.Disconnect();
            Guid? confirmedId = null, ticket = null;
            if (policy is not null)
            {
                var grant = await Read();
                Assert(grant.Capability == "noise.management.command" && grant.NoiseManagement is
                    { Purpose: "switch-classroom-mode", Secret: null, SessionId: null } && grant.NoiseManagement.ClassroomPolicyVersion == policy.Version,
                    "managed idle switch must use the classroom action, current version and no password for allow");
                confirmedId = grant.NoiseManagement!.TargetRequestId; ticket = Guid.NewGuid();
                await Protocol.WriteAsync(server, new HostResponse(1, grant.RequestId, "Succeeded", null, "fixture", NoiseProtection: current with { Ticket = ticket }), deadline.Token);
                server.Disconnect();
            }
            var request = await Read();
            Assert(request.Capability == "classroom.set" && request.ClassroomMode?.Target == "Daily" && request.ExpectedRevision == 7,
                "wrong mode or revision on confirmed switch");
            Assert(request.NoiseAuthorization == ticket && (confirmedId is null || request.RequestId == confirmedId),
                "switch used a different operation or inherited an unrelated ticket");
            accepted = initialState with { Revision = 8, Phase = "Checking", Message = "正在检查切换条件", RecentRequests = [request.RequestId] };
            if (scenario == "fast-success") accepted = accepted with { Mode = "Daily", Phase = "Idle", Actual = startup,
                CheckedAt = DateTimeOffset.UtcNow, MatchesMode = true, Message = "已核实并完成" };
            if (scenario == "fast-failure") accepted = accepted with { Phase = "Idle", Message = "检查未通过：隔离桥接验证失败" };
            await Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Accepted", null, "fixture", ClassroomMode: accepted), deadline.Token);
        }
        try
        {
            Exercise(window, owner =>
            {
                var initial = Initial();
                PumpUntil(() => initial.IsCompleted && !ClassroomUnknown(window), "initial accepted-switch state missing");
                initial.GetAwaiter().GetResult(); server.Disconnect();
                var receipt = Accept();
                var command = (Task)typeof(NPEduTools.App.ClassroomModeWindow).GetMethod("SendAsync", flags)!
                    .Invoke(window, ["classroom.set", "Daily", 7L, false])!;
                PumpUntil(() => receipt.IsCompleted && command.IsCompleted, "accepted switch receipt missing");
                receipt.GetAwaiter().GetResult(); command.GetAwaiter().GetResult(); server.Disconnect();
                var state = accepted!;
                if (!scenario.StartsWith("fast-", StringComparison.Ordinal))
                {
                    Assert(Text().Contains("设置进行中") && !Text().Contains("本次仅更新设置"), "acceptance falsely claims completion or gives no progress");
                    state = state with { Revision = scenario == "older" ? 7 : 9, Phase = "Idle", Mode = "Daily",
                        Actual = startup, CheckedAt = DateTimeOffset.UtcNow, MatchesMode = true, Message = "已核实并完成" };
                    if (scenario == "failure") state = state with { Mode = "Unconfigured", Actual = null,
                        CheckedAt = null, MatchesMode = null, Message = "检查未通过：隔离桥接验证失败" };
                    if (scenario == "incomplete") state = state with { Phase = "Incomplete", AutomaticPaused = true,
                        Actual = null, CheckedAt = null, MatchesMode = null, Message = "隔离切换未完成" };
                    if (scenario == "superseded") state = state with { RecentRequests = [state.RecentRequests![0], Guid.NewGuid()] };
                    ShowClassroom(window, state);
                }
                if (scenario is "success" or "fast-success") Assert(Text().Contains("本次仅更新设置"), "settings-only result lacks scoped completion feedback");
                else if (scenario is "failure" or "fast-failure" or "incomplete") Assert(Text().Contains("未完成") && Text().Contains("隔离"), "async failure loses its cause");
                else if (scenario == "superseded") Assert(Text().Contains("待核对") && !Text().Contains("已完成"), "another request completed the wrong switch");
                else Assert(Text().Contains("设置进行中") && !Text().Contains("本次仅更新设置"), "older snapshot completed the switch");
                ShowClassroom(window, state);
                Assert(((TextBlock)window.FindName("OperationFeedbackText")).Visibility == Visibility.Visible, "ordinary rendering hides operation result");
                Checks.Add(scenario + ": post-confirmation/private Host exchange reports progress and request-bound completion or failure; no external software or capture");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }

    private static void RunClassroomRejectedFeedbackCheck()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-feedback." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = ClassroomWindow(pipe);
        var state = new ClassroomModeState(7, Message: "尚未设置课堂模式。");
        async Task Reply(string capability, string outcome, string? error, string message)
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
            Assert(request.Capability == capability, "unexpected feedback fixture request");
            await Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, outcome, error, message,
                ClassroomMode: state), deadline.Token);
        }
        try
        {
            Exercise(window, owner =>
            {
                var initial = Reply("classroom.status", "Succeeded", null, state.Message);
                PumpUntil(() => initial.IsCompleted && !ClassroomUnknown(window), "initial classroom state missing");
                initial.GetAwaiter().GetResult(); server.Disconnect();
                var response = Reply("classroom.refresh", "Rejected", "ClassroomStorageUnavailable", "隔离模式记录不可访问。");
                var command = (Task)typeof(NPEduTools.App.ClassroomModeWindow).GetMethod("SendAsync", flags)!
                    .Invoke(window, ["classroom.refresh", null, null, false])!;
                PumpUntil(() => response.IsCompleted && command.IsCompleted, "rejected verification did not settle");
                response.GetAwaiter().GetResult(); command.GetAwaiter().GetResult(); server.Disconnect();
                // Render is the same path used by the next ordinary status poll.
                ShowClassroom(window, state);
                var feedback = (TextBlock)window.FindName("OperationFeedbackText");
                Assert(feedback.Visibility == Visibility.Visible && feedback.Text.Contains("模式记录不可访问"),
                    "ordinary polling erased the rejected operation reason");
                Assert(((TextBlock)window.FindName("ActualClassIslandText")).Text.Contains("尚未核实"),
                    "rejected verification falsely confirmed startup state");
                Checks.Add("Rejected classroom verification retains a separate visible cause after ordinary status rendering; no mode mutation or real external tools");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); }
    }
}
