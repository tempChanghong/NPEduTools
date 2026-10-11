using System.IO;
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
    private static Task ClassIslandLaunch(MainWindow window, Func<string, string, string?, Task<AdminResult>> run) =>
        (Task)typeof(MainWindow).GetMethod("StartClassIslandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [run])!;
    private static object? LaunchField(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
    private static void RunClassIslandLaunchChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "stopped", "stopped-success", "unknown", "exception", "elevation-unknown", "cancelled-restart", "verification-lost" })
                RunClassIslandLaunchCheck(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunClassIslandLaunchCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.launch-ui." + Guid.NewGuid().ToString("N");
        var window = IsolatedMainWindow(pipe);
        var lifetime = (CancellationTokenSource)LaunchField(window, "_lifetime")!;
        Task? poll = null;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    AdminField(window, "_savedPath", AdminFixturePath);
                    AdminField(window, "_configurationLoaded", true);
                    AdminField(window, "_configurationRevision", 7L);
                    ((TextBox)window.FindName("ExecutablePathBox")).Text = AdminFixturePath;
                    var status = scenario == "cancelled-restart" ? AdminFixtureStatus with { ProcessState = "Standard", ProcessMessage = "隔离普通权限实例" } : AdminFixtureStatus;
                    AdminSnapshot(window, AdminFixturePath, status); AdminRefreshControls(window);
                    if (scenario == "cancelled-restart") AdminField(window, "_restartOfferedFor", AdminFixturePath);
                    int calls = 0;
                    var helper = new TaskCompletionSource<AdminResult>();
                    Task<AdminResult> Run(string action, string path, string? fingerprint)
                    {
                        calls++;
                        Assert(path == AdminFixturePath && action == (scenario == "cancelled-restart" || calls > 1 ? "elevate" : "launch"), "unexpected helper launch target or action");
                        if (scenario == "exception") return Task.FromException<AdminResult>(new IOException("isolated helper failure"));
                        if (scenario == "elevation-unknown")
                        {
                            Assert(calls < 3 && fingerprint == (calls == 1 ? null : status.Fingerprint), "elevation reused the wrong fingerprint or replayed");
                            return Task.FromResult(calls == 1 ? new AdminResult("NeedsElevation", "隔离需要授权", status) : new AdminResult("Unknown", "隔离管理员回执丢失"));
                        }
                        return helper.Task;
                    }
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    async Task<HostRequest> Read(NamedPipeServerStream server, string capability)
                    {
                        await server.WaitForConnectionAsync(deadline.Token);
                        var request = await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
                        Assert(request.Capability == capability && Protocol.Validate(request) is null && !Protocol.NoiseInterruption(request), "unexpected verification request");
                        return request;
                    }
                    using var verifyServer = scenario == "verification-lost" ? Server() : null;
                    var verifyRead = verifyServer is null ? null : Read(verifyServer, "classisland.verify");
                    var operation = ClassIslandLaunch(window, Run);
                    if (scenario is "stopped" or "stopped-success")
                    {
                        lifetime.Cancel();
                        helper.SetResult(new(scenario == "stopped" ? "NeedsElevation" : "Succeeded", "隔离后台停止后的迟到回执", status));
                    }
                    else if (scenario is not ("exception" or "elevation-unknown"))
                        helper.SetResult(scenario switch
                        {
                            "cancelled-restart" => new("Cancelled", "隔离取消重启授权"),
                            "verification-lost" => new("Succeeded", "隔离进程已确认", status),
                            _ => new("Unknown", "隔离启动回执丢失")
                        });
                    if (verifyRead is not null)
                    {
                        PumpUntil(() => verifyRead.IsCompleted, "connection verification was not dispatched");
                        var verify = verifyRead.GetAwaiter().GetResult();
                        Assert(verify.ExecutablePath == AdminFixturePath && verify.ExpectedRevision == 7, "verification did not use the saved path and revision");
                        verifyServer!.Disconnect();
                    }
                    PumpUntil(() => operation.IsCompleted, "synthetic launch did not finish"); operation.GetAwaiter().GetResult();
                    Assert(calls == (scenario == "elevation-unknown" ? 2 : 1), "unexpected retry or elevation after the lifetime stopped");
                    if (scenario is "unknown" or "exception" or "elevation-unknown")
                    {
                        AssertAdminUnknown(window);
                        Assert(LaunchField(window, "_pendingStartId") is null && LaunchField(window, "_unifiedVerification") is null,
                            "unconfirmed helper result started verification");
                        var query = AdminRun(window, "status", (_, _, fingerprint) =>
                        {
                            Assert(fingerprint is null, "fresh administrator query reused an uncertain fingerprint");
                            return Task.FromResult(new AdminResult("Succeeded", "隔离新权限", status with { ProcessState = "Stopped", ProcessMessage = "隔离未运行" }));
                        });
                        PumpUntil(() => query.IsCompleted, "fresh administrator query did not finish"); query.GetAwaiter().GetResult();
                        Assert(AdminText(window, "AdminProcessStatus") == "隔离未运行", "fresh state did not recover administrator display");
                    }
                    else if (scenario == "cancelled-restart")
                    {
                        Assert(AdminText(window, "AdminProcessStatus") == status.ProcessMessage &&
                            (string?)LaunchField(window, "_restartOfferedFor") == AdminFixturePath && Button(window, "StartButton").Content.ToString() == "管理员重启",
                            "cancelled restart lost the unchanged process snapshot or retry choice");
                    }
                    else if (scenario is "stopped" or "stopped-success")
                    {
                        Assert(!((string?)LaunchField(window, "_launchMessage"))!.Contains("迟到") && LaunchField(window, "_pendingStartId") is null,
                            "stopped lifetime applied a late launch receipt or dispatched verification");
                    }
                    else
                    {
                        Assert((Guid?)LaunchField(window, "_pendingStartId") == verifyRead!.Result.RequestId &&
                            (Guid?)LaunchField(window, "_unifiedVerification") == verifyRead.Result.RequestId && !Button(window, "StartButton").IsEnabled &&
                            AdminText(window, "AdminProcessStatus") == status.ProcessMessage && LaunchField(window, "_adminStatus") is not null,
                            "lost verification receipt lost its correlation or allowed another launch");
                        verifyServer!.Dispose();
                        using var queryServer = Server(); var read = Read(queryServer, "classisland.execution.get");
                        poll = (Task)typeof(MainWindow).GetMethod("ManagementLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [lifetime.Token])!;
                        PumpUntil(() => read.IsCompleted, "verification recovery did not query its execution");
                        var request = read.GetAwaiter().GetResult();
                        Assert(request.OperationId == verifyRead.Result.RequestId, "verification recovery queried an unrelated execution");
                        var now = DateTimeOffset.UtcNow;
                        var reply = Protocol.WriteAsync(queryServer, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, "隔离恢复",
                            Launch: new(new(7, AdminFixturePath), new(verifyRead.Result.RequestId, AdminFixturePath, now, now, "Succeeded", null, "隔离连接已就绪"))), deadline.Token);
                        PumpUntil(() => reply.IsCompleted, "verification recovery response did not finish"); reply.GetAwaiter().GetResult();
                        PumpUntil(() => LaunchField(window, "_pendingStartId") is null, "verification recovery did not finish its pending operation");
                        Assert((bool)LaunchField(window, "_launchSucceeded")! && LaunchField(window, "_unifiedVerification") is null && Button(window, "StartButton").IsEnabled && calls == 1,
                            "successful correlated query failed to recover or restarted the helper");
                    }
                    Checks.Add($"ClassIsland launch {scenario}: unknown state or unchanged cancellation handled; no unintended replay or post-stop work");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "launch recovery poll did not stop"); poll.GetAwaiter().GetResult(); }
        }
    }
}
