using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassroomHomePollChecks()
    {
        foreach (string scenario in new[] { "request-id", "version", "zero-length", "empty-message", "missing-mode", "cancel-wait" })
            RunClassroomHomePollCheck(scenario);
    }

    private static void RunClassroomHomePollCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.classroom-home." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var window = IsolatedMainWindow(pipe);
        var lifetime = (CancellationTokenSource)typeof(MainWindow).GetField("_lifetime", flags)!.GetValue(window)!;
        var show = typeof(MainWindow).GetMethod("ShowClassroomModeState", flags)!;
        Task? poll = null;
        string Title() => ((TextBlock)window.FindName("ClassroomModeTitle")).Text;
        string Detail() => ((TextBlock)window.FindName("ClassroomModeDetail")).Text;
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
                    show.Invoke(window, [new ClassroomModeState(7, "Exam", AutomaticPaused: true)]);
                    Assert(Title().Contains("考试模式") && Detail().Contains("暂停"), "isolated confirmed exam state did not render");
                    var read = Read();
                    poll = (Task)typeof(MainWindow).GetMethod("ClassroomModePollAsync", flags)!.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "home classroom status query did not arrive");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "classroom.status" && request.ClassroomMode is null, "home polling sent a mode mutation");
                    if (scenario == "cancel-wait")
                    {
                        lifetime.Cancel();
                        PumpUntil(() => poll.IsCompleted, "home poll did not stop after cancellation");
                        poll.GetAwaiter().GetResult();
                        Assert(Title().Contains("未知") && Detail().Contains("无法核实"), "cancelled query retained a confirmed mode");
                        Checks.Add("cancel-wait: stopping while a home query is pending clears the confirmed mode/pause and completes the poll normally");
                        return;
                    }
                    async Task InvalidReply()
                    {
                        if (scenario == "zero-length")
                        {
                            byte[] header = new byte[4];
                            BinaryPrimitives.WriteInt32LittleEndian(header, 0);
                            await server.WriteAsync(header, timeout.Token);
                            await server.FlushAsync(timeout.Token);
                        }
                        else if (scenario == "empty-message") await Protocol.WriteAsync<object?>(server, null, timeout.Token);
                        else await Protocol.WriteAsync(server, new HostResponse(scenario == "version" ? Protocol.Version + 1 : Protocol.Version,
                            scenario == "request-id" ? Guid.NewGuid() : request.RequestId, "Succeeded", null, "隔离无效模式回执"), timeout.Token);
                    }
                    var reply = InvalidReply();
                    PumpUntil(() => reply.IsCompleted && (Title().Contains("未知") || poll.IsCompleted), "home invalid reply did not settle");
                    reply.GetAwaiter().GetResult();
                    Assert(!poll.IsFaulted, scenario + ": invalid classroom status receipt terminated the home poll: " + poll.Exception?.GetBaseException().Message);
                    Assert(Title().Contains("未知") && Detail().Contains("无法核实"), "invalid response kept a stale confirmed mode/pause");
                    if (scenario == "request-id")
                    {
                        ((ScrollViewer)owner.FindName("HomePage")).ScrollToTop();
                        owner.UpdateLayout();
                        Snapshot(owner, "classroom-home-unknown.png");
                    }
                    server.Disconnect();
                    var recovery = Read();
                    PumpUntil(() => recovery.IsCompleted, "home classroom poll did not reconnect after the invalid response");
                    var fresh = recovery.GetAwaiter().GetResult();
                    Assert(fresh.Capability == "classroom.status" && fresh.ClassroomMode is null, "recovery sent a mode mutation");
                    var recovered = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, fresh.RequestId,
                        "Succeeded", null, "隔离恢复", ClassroomMode: new(8, "Daily")), timeout.Token);
                    PumpUntil(() => recovered.IsCompleted && Title().Contains("日常模式"), "valid home reply did not restore the daily mode");
                    recovered.GetAwaiter().GetResult();
                    Assert(!poll.IsCompleted && !Detail().Contains("无法核实") && Detail().Contains("原有启用状态"), "home mode/pause did not recover through the same poll");
                    Checks.Add(scenario + ": invalid home mode receipt marks mode/pause unknown; the same read-only poll recovers on a valid daily response");
                }
                finally { lifetime.Cancel(); window.Close(); }
            });
        }
        finally
        {
            lifetime.Cancel(); window.Close();
            if (poll is not null)
            {
                PumpUntil(() => poll.IsCompleted, "isolated home mode poll did not stop");
                if (poll.IsFaulted) _ = poll.Exception; // Observe the old implementation's fault without masking the regression assertion.
                else poll.GetAwaiter().GetResult();
            }
        }
    }
}
