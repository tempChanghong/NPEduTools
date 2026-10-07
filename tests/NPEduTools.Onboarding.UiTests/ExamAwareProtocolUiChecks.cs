using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunExamAwareProtocolChecks()
    {
        var failures = new List<Exception>();
        foreach (string operation in new[] { "poll", "plan", "export" })
            foreach (string invalid in new[] { "request-id", "version", "zero-length", "empty-message" })
                try { RunExamAwareProtocolCheck(operation, invalid); }
                catch (Exception error) { failures.Add(new InvalidOperationException(operation + "/" + invalid + ": " + error.Message, error)); }
        if (failures.Count > 0) throw new AggregateException("ExamAware protocol recovery failed", failures);
    }

    private static void RunExamAwareProtocolCheck(string operation, string invalid)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.examaware-protocol." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string file = Path.Combine(_output, "synthetic-existing-pairing-" + Guid.NewGuid().ToString("N") + ".json");
        const string sentinel = "synthetic existing file; never real credentials";
        var window = new ExamAwareWindow(pipe);
        Task? command = null;
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        async Task<HostRequest> Read()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        try
        {
            if (operation == "export") File.WriteAllText(file, sentinel);
            Exercise(window, owner =>
            {
                try
                {
                    ShowExamAware(window, ReadyExamAware());
                    ((TextBox)owner.FindName("Executable")).Text = "edited-fixture.exe";
                    Assert(Button(owner, "PresentPlanButton").IsEnabled, "isolated ready plan did not render");
                    var read = Read();
                    if (operation == "plan")
                        command = RunExamAware(window, "examaware.plan", new("prepare", Convert.ToBase64String("{}"u8.ToArray())));
                    else if (operation == "export")
                        command = (Task)typeof(ExamAwareWindow).GetMethod("ExportPairingAsync", flags)!.Invoke(window, [file])!;
                    PumpUntil(() => read.IsCompleted, "examination request did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    string capability = operation switch { "poll" => "examaware.status", "plan" => "examaware.plan", _ => "examaware.pairing.get" };
                    Assert(request.Capability == capability && Protocol.Validate(request) is null, "unexpected examination request");
                    if (operation == "plan")
                        Assert(request.ExpectedRevision == 7 && request.ExamPlan?.Action == "prepare", "plan request lost revision checking");
                    Task reply = invalid switch
                    {
                        "zero-length" => server.WriteAsync(new byte[4], timeout.Token).AsTask(),
                        "empty-message" => Protocol.WriteAsync<object?>(server, null, timeout.Token),
                        _ => Protocol.WriteAsync(server, new HostResponse(
                            invalid == "version" ? Protocol.Version + 1 : Protocol.Version,
                            invalid == "request-id" ? Guid.NewGuid() : request.RequestId, "Accepted", null, "无效隔离回执",
                            ExamAware: ReadyExamAware("不可采信的方案"),
                            ExamAwarePairing: new(1, "127.0.0.1", 12345, "fixture-not-a-real-key")), timeout.Token)
                    };
                    PumpUntil(() => reply.IsCompleted && (command is null || command.IsCompleted), "invalid examination reply did not settle");
                    reply.GetAwaiter().GetResult();
                    if (command is not null)
                    {
                        Assert(!command.IsFaulted, "invalid receipt escaped the operation: " + command.Exception?.GetBaseException().Message);
                        command.GetAwaiter().GetResult();
                        Assert(!(bool)typeof(ExamAwareWindow).GetField("_busy", flags)!.GetValue(window)!, "invalid receipt retained the busy gate");
                        Assert(Text("Message").Contains(operation == "export" ? "未能导出" : "未收到"), "invalid receipt retained the waiting message");
                    }
                    var idle = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
                    PumpUntil(() => idle.IsCompleted, "examination state rendering did not settle");
                    Assert(Text("Connection").Contains("未确认"), "invalid receipt kept a stale connected bridge");
                    Assert(Text("AutoStartText").Contains("未知") && Text("PlayerStateText").Contains("未知") &&
                        Text("PlanSummaryText").Contains("未知"), "invalid receipt retained old startup/player/plan facts");
                    Assert(!Button(owner, "PresentPlanButton").IsEnabled && !Button(owner, "QuitButton").IsEnabled &&
                        !Button(owner, "EnableAutoStartButton").IsEnabled &&
                        typeof(ExamAwareWindow).GetField("_prepared", flags)!.GetValue(window) is null,
                        "invalid receipt retained actionable controls or a prepared plan");
                    if (operation == "export") Assert(File.ReadAllText(file) == sentinel, "invalid pairing response overwrote an existing file");
                    if (operation == "poll" && invalid == "request-id") Snapshot(owner, "examaware-protocol-unknown.png");
                    server.Disconnect();
                    var recovery = Read();
                    PumpUntil(() => recovery.IsCompleted, "examination polling did not recover");
                    var fresh = recovery.GetAwaiter().GetResult();
                    Assert(fresh.Capability == "examaware.status", "recovery replayed a command or export");
                    var restored = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离恢复", ExamAware: ReadyExamAware("隔离恢复方案")), timeout.Token);
                    PumpUntil(() => restored.IsCompleted && Text("Connection") == "隔离桥接已连接", "fresh examination state did not render");
                    restored.GetAwaiter().GetResult();
                    Assert(Button(owner, "PresentPlanButton").IsEnabled && Button(owner, "QuitButton").IsEnabled &&
                        Text("PlanSummaryText").Contains("隔离恢复方案") &&
                        ((TextBox)owner.FindName("Executable")).Text == "edited-fixture.exe", "recovery lost controls, fresh plan or edited path");
                    if (operation == "export") Assert(File.ReadAllText(file) == sentinel, "status recovery retried the pairing write");
                    if (operation == "poll" && invalid == "request-id") Snapshot(owner, "examaware-protocol-recovered.png");
                    Checks.Add(operation + "/" + invalid + ": unknown state clears old facts; a fresh read-only poll restores controls without replay or pairing writes");
                }
                finally { window.Shutdown(); }
            });
        }
        finally
        {
            window.Shutdown();
            if (command?.IsFaulted == true) _ = command.Exception;
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
