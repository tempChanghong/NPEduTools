using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.Contracts;
using NPEduTools.App;

internal static partial class Program
{
    private static void RunClassroomProtocolChecks()
    {
        var failures = new List<Exception>();
        foreach (string operation in new[] { "poll", "refresh" })
            foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message" })
                try { RunClassroomProtocolCheck(operation, invalid); }
                catch (Exception error) { failures.Add(new InvalidOperationException(operation + "/" + invalid + ": " + error.Message, error)); }
        if (failures.Count > 0) throw new AggregateException("Classroom protocol recovery failed", failures);
    }

    private static void RunClassroomProtocolCheck(string operation, string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-protocol." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = ClassroomWindow(pipe);
        window.Width = 700; window.Height = 620;
        Task? command = null;
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ShowClassroom(window, ClassroomExam());
                    Assert(Button(owner, "RestoreButton").IsEnabled && !Button(owner, "DailyButton").IsEnabled,
                        "isolated incomplete mode did not offer restoration");
                    var read = Read();
                    if (operation == "refresh")
                        command = (Task)typeof(ClassroomModeWindow).GetMethod("SendAsync", flags)!
                            .Invoke(window, ["classroom.refresh", null, null, false])!;
                    PumpUntil(() => read.IsCompleted, "classroom query did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == (operation == "poll" ? "classroom.status" : "classroom.refresh") &&
                        request.ClassroomMode is null && request.ExpectedRevision is null && Protocol.Validate(request) is null,
                        "fixture sent a mode mutation or unexpected query");
                    Task reply = invalid switch
                    {
                        "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                        "empty-message" => Protocol.WriteAsync<object?>(server, null, timeout.Token),
                        _ => Protocol.WriteAsync(server, new HostResponse(
                            invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                            invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null, "无效隔离回执",
                            ClassroomMode: new(8, "Daily", Message: "不可采信的日常状态")), timeout.Token)
                    };
                    PumpUntil(() => reply.IsCompleted && (command is null || command.IsCompleted), "invalid classroom response did not settle");
                    reply.GetAwaiter().GetResult();
                    if (command is not null)
                    {
                        Assert(!command.IsFaulted, "invalid receipt escaped the refresh: " + command.Exception?.GetBaseException().Message);
                        command.GetAwaiter().GetResult();
                        Assert(!(bool)typeof(ClassroomModeWindow).GetField("_sending", flags)!.GetValue(window)!, "invalid refresh retained its pending gate");
                    }
                    var idle = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                    PumpUntil(() => idle.IsCompleted, "classroom failure rendering did not settle");
                    Assert(ClassroomUnknown(window), "invalid receipt retained a confirmed mode");
                    Assert(Text("ModeTitle").Contains("未知") && Text("PauseText").Contains("无法核实") &&
                        Text("ActualText").Contains("未知") && Text("RecoveryText").Contains("未知"),
                        "invalid receipt retained old mode, pause, startup or recovery facts");
                    Assert(!Button(owner, "DailyButton").IsEnabled && !Button(owner, "ExamButton").IsEnabled &&
                        !Button(owner, "RestoreButton").IsEnabled && !Button(owner, "RefreshButton").IsEnabled &&
                        !Button(owner, "RetryButton").IsEnabled && !((CheckBox)owner.FindName("SwitchRunning")).IsEnabled,
                        "invalid receipt retained actionable mode controls");
                    Assert(((ItemsControl)owner.FindName("SetupItems")).Items.Count == 0 &&
                        Text("SetupSummary").Contains("失效"), "invalid receipt retained obsolete configuration checks");
                    if (operation == "poll" && invalid == "request-id")
                    {
                        Snapshot(owner, "classroom-protocol-unknown.png");
                        ((ScrollViewer)owner.Content).ScrollToBottom(); owner.UpdateLayout();
                        Snapshot(owner, "classroom-protocol-unknown-recovery.png");
                        ((ScrollViewer)owner.Content).ScrollToTop();
                    }
                    server.Disconnect();
                    var recovery = Read();
                    PumpUntil(() => recovery.IsCompleted, "classroom polling did not recover");
                    var fresh = recovery.GetAwaiter().GetResult();
                    Assert(fresh.Capability == "classroom.status" && fresh.ClassroomMode is null &&
                        fresh.ExpectedRevision is null, "recovery replayed an operation or changed mode");
                    var restored = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离恢复", ClassroomMode: new(9, "Daily", Message: "隔离日常状态")), timeout.Token);
                    PumpUntil(() => restored.IsCompleted && !ClassroomUnknown(window), "fresh classroom state did not render");
                    restored.GetAwaiter().GetResult();
                    Assert(Text("ModeTitle").Contains("日常模式") && Button(owner, "DailyButton").IsEnabled &&
                        Button(owner, "ExamButton").IsEnabled && Button(owner, "RefreshButton").IsEnabled &&
                        !Button(owner, "RestoreButton").IsEnabled && !Text("RecoveryText").Contains("未知"),
                        "fresh daily snapshot did not restore the correct controls");
                    if (operation == "poll" && invalid == "request-id") Snapshot(owner, "classroom-protocol-recovered.png");
                    Checks.Add(operation + "/" + invalid + ": invalid classroom reply clears mode/pause/startup/recovery facts; read-only polling restores daily controls");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown();
            if (command?.IsFaulted == true) _ = command.Exception;
        }
    }
}
