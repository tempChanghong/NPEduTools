using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassroomAcceptedFeedbackChecks()
    {
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
            Assert(protection.Capability == "noise.management.status", "confirmation path did not check scheduled protection");
            await Protocol.WriteAsync(server, new HostResponse(1, protection.RequestId, "Succeeded", null, "fixture",
                NoiseProtection: new(false, false, Guid.NewGuid(), null)), deadline.Token);
            server.Disconnect();
            var request = await Read();
            Assert(request.Capability == "classroom.set" && request.ClassroomMode?.Target == "Daily" && request.ExpectedRevision == 7,
                "wrong mode or revision on confirmed switch");
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
                    Assert(Text().Contains("正在切换") && !Text().Contains("已完成"), "acceptance falsely claims completion or gives no progress");
                    state = state with { Revision = scenario == "older" ? 7 : 9, Phase = "Idle", Mode = "Daily",
                        Actual = startup, CheckedAt = DateTimeOffset.UtcNow, MatchesMode = true, Message = "已核实并完成" };
                    if (scenario == "failure") state = state with { Mode = "Unconfigured", Actual = null,
                        CheckedAt = null, MatchesMode = null, Message = "检查未通过：隔离桥接验证失败" };
                    if (scenario == "incomplete") state = state with { Phase = "Incomplete", AutomaticPaused = true,
                        Actual = null, CheckedAt = null, MatchesMode = null, Message = "隔离切换未完成" };
                    if (scenario == "superseded") state = state with { RecentRequests = [state.RecentRequests![0], Guid.NewGuid()] };
                    ShowClassroom(window, state);
                }
                if (scenario is "success" or "fast-success") Assert(Text().Contains("本次切换已完成"), "confirmed result lacks completion feedback");
                else if (scenario is "failure" or "fast-failure" or "incomplete") Assert(Text().Contains("未完成") && Text().Contains("隔离"), "async failure loses its cause");
                else if (scenario == "superseded") Assert(Text().Contains("待核对") && !Text().Contains("已完成"), "another request completed the wrong switch");
                else Assert(Text().Contains("正在切换") && !Text().Contains("已完成"), "older snapshot completed the switch");
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
