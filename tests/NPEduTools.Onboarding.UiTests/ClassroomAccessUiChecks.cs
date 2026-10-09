using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassroomAccessChecks()
    {
        var failures = new List<Exception>();
        foreach (string entry in new[] { "home", "poll", "refresh" })
            try { RunClassroomAccessCheck(entry); }
            catch (Exception error) { failures.Add(new InvalidOperationException("Classroom access/" + entry + ": " + error.Message, error)); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private static void RunClassroomAccessCheck(string entry)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-access." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // A write-only fixture rejects the real duplex client without changing pipe ACLs.
        using var denied = new NamedPipeServerStream(pipe, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Window window = entry == "home" ? IsolatedMainWindow(pipe) : ClassroomWindow(pipe);
        var mode = window as ClassroomModeWindow;
        var home = window as MainWindow;
        var sending = typeof(ClassroomModeWindow).GetField("_sending", flags)!;
        if (mode is not null) sending.SetValue(mode, true); // Keep polling out of explicit command setup.
        Task? poll = null, command = null;
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        string Title() => Text(home is null ? "ModeTitle" : "ClassroomModeTitle");
        bool Unknown() => home is null ? ClassroomUnknown(mode!) : Title().Contains("未知");
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    if (home is not null)
                    {
                        typeof(MainWindow).GetMethod("ShowClassroomModeState", flags)!.Invoke(home, [ClassroomExam()]);
                        Assert(!Unknown(), "synthetic confirmed home mode did not render");
                        poll = (Task)typeof(MainWindow).GetMethod("ClassroomModePollAsync", flags)!.Invoke(home, null)!;
                        PumpUntil(() => Unknown() || poll.IsCompleted, "access-denied home query did not settle");
                        Assert(!poll.IsFaulted, "access denial terminated home polling: " + poll.Exception?.GetBaseException().GetType().Name);
                    }
                    else
                    {
                        sending.SetValue(mode, false); ShowClassroom(mode!, ClassroomExam());
                        Assert(Button(owner, "RestoreButton").IsEnabled, "synthetic recovery state did not render");
                        if (entry == "refresh")
                        {
                            command = (Task)typeof(ClassroomModeWindow).GetMethod("SendAsync", flags)!
                                .Invoke(mode, ["classroom.refresh", null, null, false])!;
                            PumpUntil(() => command.IsCompleted, "access-denied refresh did not settle");
                            command.GetAwaiter().GetResult(); // Baseline leaks UnauthorizedAccessException.
                        }
                        else PumpUntil(Unknown, "access-denied status poll retained the old confirmed mode");
                    }
                    Assert(Unknown() && Title().Contains("未知"), "access denial retained the old confirmed mode");
                    if (mode is not null)
                    {
                        Assert(!Button(owner, "DailyButton").IsEnabled && !Button(owner, "ExamButton").IsEnabled &&
                            !Button(owner, "RestoreButton").IsEnabled && !Button(owner, "RefreshButton").IsEnabled &&
                            !Button(owner, "RetryButton").IsEnabled && !((CheckBox)owner.FindName("SwitchRunning")).IsEnabled,
                            "access denial retained actionable classroom controls");
                        Assert(Text("ActualText").Contains("未知") && Text("RecoveryText").Contains("未知") &&
                            Text("PauseText").Contains("无法核实") && Text("SetupSummary").Contains("失效") &&
                            ((ItemsControl)owner.FindName("SetupItems")).Items.Count == 0 && !(bool)sending.GetValue(mode)!,
                            "access denial retained startup, recovery, pause, setup or pending-operation facts");
                    }
                    else Assert(Text("ClassroomModeDetail").Contains("无法核实"), "home retained a confirmed pause state");

                    denied.Dispose();
                    using var recovered = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task Reply()
                    {
                        await recovered.WaitForConnectionAsync(timeout.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(recovered, timeout.Token);
                        Assert(request.Capability == "classroom.status" && request.ClassroomMode is null && request.ExpectedRevision is null,
                            "recovery replayed an operation instead of reading status");
                        await Protocol.WriteAsync(recovered, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "隔离只读恢复", ClassroomMode: new(9, "Daily", Message: "隔离日常状态")), timeout.Token);
                    }
                    var reply = Reply();
                    PumpUntil(() => reply.IsCompleted && Title().Contains("日常模式"), "classroom polling failed to recover after access was restored");
                    reply.GetAwaiter().GetResult();
                    if (mode is not null)
                        Assert(Button(owner, "DailyButton").IsEnabled && Button(owner, "ExamButton").IsEnabled &&
                            Button(owner, "RefreshButton").IsEnabled && !Button(owner, "RestoreButton").IsEnabled,
                            "fresh daily state did not restore normal controls");
                    else Assert(poll is { IsCompleted: false }, "home recovery replaced or stopped its original poll");
                    Checks.Add("Classroom access/" + entry + ": access denial clears stale mode facts and controls; the same read-only poll restores daily state without replaying a switch");
                }
                finally { if (mode is not null) mode.Shutdown(); else window.Close(); }
            });
        }
        finally
        {
            if (mode is not null) mode.Shutdown(); else window.Close();
            if (poll is not null)
            {
                PumpUntil(() => poll.IsCompleted, "isolated home classroom poll did not stop");
                if (poll.IsFaulted) _ = poll.Exception; else poll.GetAwaiter().GetResult();
            }
            if (command?.IsFaulted == true) _ = command.Exception;
        }
    }
}
