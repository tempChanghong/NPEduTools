using System.IO.Pipes;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void WorkspacePresentationAccessFailure()
    {
        string pipe = "NPEduTools.Test.study-pulse-denied." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var main = PrivateWorkspace(pipe);
        var window = new StudyWindow(pipe, () => { }, muted: true);
        window.Apply(StudyFixture(), null);
        typeof(MainWindow).GetField("_studyWindow", WorkspaceFlags)!.SetValue(main, window);
        Task? poll = null, server = null;
        try
        {
            poll = (Task)typeof(MainWindow).GetMethod("NoiseDisplayPollAsync", WorkspaceFlags)!.Invoke(main, null)!;
            PumpUntil(() => poll.IsCompleted || (long)typeof(StudyWindow).GetField("_presentationSequence", WorkspaceFlags)!.GetValue(window)! >= 2,
                "denied presentation did not settle");
            Assert(!poll.IsCompleted, "denied presentation receipt terminated the display polling task: " + poll.Exception?.GetBaseException().GetType().Name);
            denied.Dispose();
            server = Task.Run(() => WorkspaceProxy(pipe, request =>
            {
                Assert(request.Noise is null && request.ManualStudy is null && request.StudyHide is null, "recovery replayed capture/hide");
                return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "isolated recovered pipe",
                    Study: request.Capability.StartsWith("study.status", StringComparison.Ordinal) ? StudyFixture() : null));
            }, stop.Token));
            PumpUntil(() => window.IsVisible || poll.IsCompleted, "read-only recovery did not show a fresh study state");
            Assert(!poll.IsCompleted && window.IsVisible, "display poll could not recover after denied receipt");
            Checks.Add("Actual MainWindow denied presentation pipe: polling survives repeated access failures and recovers read-only without capture or replay");
        }
        finally
        {
            main.Close(); stop.Cancel();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "denied display cleanup stalled"); _ = poll.Exception; }
            if (server is not null) try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
    private static void WorkspaceSeatingAccessFailure() => StudyDeniedCommand("seating");
    private static void WorkspaceManualEndAccessFailure() => StudyDeniedCommand("end");
    private static void StudyDeniedCommand(string scenario)
    {
        string pipe = "NPEduTools.Test.study-command-denied." + Guid.NewGuid().ToString("N");
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = new StudyWindow(pipe, () => { }, muted: true, manual: scenario == "end");
        var state = StudyFixture();
        if (scenario == "end") state = state with { Manual = new(Guid.NewGuid(), 1, state.Execution!.SessionId, "Active", null, null, null, false, 600),
            Execution = state.Execution! with { Owner = "Manual" } };
        else state = state with { Seating = new("READY", state.Version, 1, new string('a', 64), 1, 24, 1, 1) };
        try
        {
            window.Apply(state, null);
            var action = (Task)typeof(StudyWindow).GetMethod(scenario == "end" ? "EndAsync" : "ReportSeatingAsync", WorkspaceFlags)!.Invoke(window, null)!;
            PumpUntil(() => action.IsCompleted, "denied study command did not settle");
            Assert(!action.IsFaulted, scenario + ": access refusal escaped the awaited study action: " + action.Exception?.GetBaseException().GetType().Name);
            if (scenario == "end") Assert(((TextBlock)((StudySurface)window.Content).FindName("SessionNote")).Text.Contains("待确认"), "denied end falsely claimed success or omitted feedback");
            Checks.Add(scenario + ": actual private pipe access refusal settles without fault; end remains unconfirmed, no real capture or server mutation");
        }
        finally { window.Shutdown(); }
    }
}
