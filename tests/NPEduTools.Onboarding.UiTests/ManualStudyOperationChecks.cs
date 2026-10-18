using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static StudyHostState ManualOperationState()
    {
        var state = StudyFixture();
        return state with { Mode = "Manual", ClockSource = "Windows", Execution = state.Execution! with { Owner = "Manual" },
            Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, true, 600) };
    }
    private static void RunManualStudyOperationChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var errors = new List<Exception>();
            foreach (string scenario in new[] { "replacement-rejection", "replacement-disconnect", "same-session", "same-session-ended", "shutdown" })
                try { ManualEndReceiptCheck(scenario); } catch (Exception e) { errors.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
            foreach (var check in new Action[] { ManualEndPendingCheck, ManualHoldReplacementCheck, RunStudyHoldIntentCheck })
                try { check(); } catch (Exception e) { errors.Add(new InvalidOperationException(check.Method.Name + ": " + e.Message, e)); }
            if (errors.Count > 0) throw new AggregateException(errors);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void ManualEndReceiptCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.manual-end-order." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var state = ManualOperationState();
        var window = new StudyWindow(pipe, () => { }, muted: true, manual: true) { Width = 640, Height = 480 };
        var surface = (StudySurface)window.Content;
        Task? action = null;
        try
        {
            window.Apply(state, null);
            async Task<HostRequest> Read()
            {
                await server.WaitForConnectionAsync(deadline.Token);
                return await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
            }
            var read = Read();
            action = (Task)typeof(StudyWindow).GetMethod("EndAsync", WorkspaceFlags)!.Invoke(window, null)!;
            PumpUntil(() => read.IsCompleted, "manual end did not reach private pipe");
            var request = read.GetAwaiter().GetResult();
            Assert(request.ManualStudy is { Action: "end" } end && end.InstanceId == state.Manual!.InstanceId && end.SessionId == state.Manual.SessionId,
                "end request lost its original session identity");
            if (scenario.StartsWith("replacement-", StringComparison.Ordinal))
                window.Apply(state with { Version = new string('b', 64), Manual = state.Manual! with { SessionId = Guid.NewGuid(), Revision = 2 } }, null);
            if (scenario == "same-session-ended") window.Apply(state with { Phase = "Ended", Manual = state.Manual! with { State = "Ended" } }, null);
            if (scenario == "shutdown") window.Shutdown();
            if (scenario == "replacement-disconnect") server.Disconnect();
            else
            {
                var write = Protocol.WriteAsync(server, new HostResponse(1, request.RequestId, "Rejected", "MANUAL_STATE_CHANGED", "old-session-end-rejected"), deadline.Token);
                PumpUntil(() => write.IsCompleted, "old end response did not finish"); write.GetAwaiter().GetResult();
            }
            PumpUntil(() => action.IsCompleted, "manual end did not settle"); action.GetAwaiter().GetResult();
            var error = (string?)typeof(StudySurface).GetField("_returnError", WorkspaceFlags)!.GetValue(surface);
            Assert(scenario == "same-session" ? error == "old-session-end-rejected" : error is null,
                "obsolete end feedback crossed a new session or shutdown, or a current rejection was hidden");
            Checks.Add(scenario + ": real StudyWindow end/private pipe fences late error/disconnect to its original manual session; no actual capture");
        }
        finally
        {
            server.Dispose(); window.Shutdown();
            if (action is not null) { PumpUntil(() => action.IsCompleted, "manual end cleanup stalled"); _ = action.Exception; }
        }
    }
    private static void ManualEndPendingCheck()
    {
        var clock = new StudyUiClock(); var state = ManualOperationState();
        int ends = 0, hides = 0;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var surface = new StudySurface(() => { hides++; return Task.CompletedTask; }, () => { }, true, clock,
            end: () => { ends++; return pending.Task; });
        var owner = new Window { Content = surface, Width = 900, Height = 600 };
        try
        {
            Exercise(owner, _ =>
            {
                surface.Apply(state, null);
                void HoldEnd() => typeof(StudySurface).GetMethod("BeginEndHold", WorkspaceFlags)!.Invoke(surface, null);
                HoldEnd(); clock.Ms += 2000; surface.Apply(state, null);
                PumpUntil(() => ends == 1, "first held end did not start");
                HoldEnd(); clock.Ms += 2000; surface.Apply(state, null);
                var settle = Task.Delay(150); PumpUntil(() => settle.IsCompleted, "duplicate hold observation stalled");
                Assert(ends == 1 && !((Button)surface.FindName("EndButton")).IsEnabled && !((Button)surface.FindName("HoldButton")).IsEnabled,
                    "pending manual end allowed duplicate end or conflicting hide");
                surface.BeginHold(); clock.Ms += 2000; surface.Apply(state, null);
                settle = Task.Delay(100); PumpUntil(() => settle.IsCompleted, "hide observation stalled");
                Assert(hides == 0, "pending end allowed a simultaneous hide request");
                pending.TrySetResult();
                PumpUntil(() => ((Button)surface.FindName("EndButton")).IsEnabled, "end receipt did not release operation controls");
                Checks.Add("Actual long-end gesture: one pending end owns controls, duplicate end/hide are blocked, completion restores controls; synthetic only");
                owner.Close();
            });
        }
        finally { pending.TrySetResult(); surface.Stop(); owner.Close(); }
    }
    private static void ManualHoldReplacementCheck()
    {
        var clock = new StudyUiClock(); var state = ManualOperationState(); int ends = 0;
        var surface = new StudySurface(() => Task.CompletedTask, () => { }, true, clock, end: () => { ends++; return Task.CompletedTask; });
        var owner = new Window { Content = surface, Width = 900, Height = 600 };
        try
        {
            Exercise(owner, _ =>
            {
                surface.Apply(state, null);
                var hold = typeof(StudySurface).GetMethod("BeginEndHold", WorkspaceFlags)!;
                hold.Invoke(surface, null); clock.Ms += 1000;
                state = state with { Version = new string('b', 64), Manual = state.Manual! with { SessionId = Guid.NewGuid(), Revision = 2 } };
                surface.Apply(state, null); clock.Ms += 1000; surface.Apply(state, null);
                var settle = Task.Delay(150); PumpUntil(() => settle.IsCompleted, "new session gesture observation stalled");
                Assert(ends == 0, "an old finger hold ended the replacement session");
                hold.Invoke(surface, null); clock.Ms += 2000; surface.Apply(state, null);
                PumpUntil(() => ends == 1, "fresh explicit hold could not end the replacement session");
                Checks.Add("Actual long-end gesture: session replacement cancels the old hold; a new explicit hold remains usable, no capture");
                owner.Close();
            });
        }
        finally { surface.Stop(); owner.Close(); }
    }
}
