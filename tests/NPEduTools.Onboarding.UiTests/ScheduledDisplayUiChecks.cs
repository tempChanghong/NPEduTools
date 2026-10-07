using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunScheduledDisplayChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            RunScheduledReturnCheck(false, true, "Succeeded");
            RunScheduledReturnCheck(true, false, "Succeeded");
            RunScheduledReturnCheck(false, true, "Rejected");
            RunScheduledReturnCheck(false, true, "Disconnected");
            RunScheduledReturnCheck(false, false, "Succeeded");
            RunScheduledReturnCheck(false, false, "Rejected");
            RunScheduledReturnCheck(false, false, "Disconnected");
            var failures = new List<Exception>();
            foreach (string invalid in new[] { "RequestId", "Version", "ZeroLength", "EmptyMessage" })
            foreach (var identity in new[] { (false, false), (false, true), (true, false) })
            {
                try { RunScheduledReturnCheck(identity.Item1, identity.Item2, invalid); }
                catch (Exception error) { failures.Add(new InvalidOperationException($"Return {invalid}, identity {identity}: {error.Message}", error)); }
            }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunScheduledReturnCheck(bool newInstance, bool newSession, string outcome)
    {
        string pipe = "NPEduTools.Test.scheduled-display." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var instance = Guid.NewGuid(); var session = Guid.NewGuid();
        var noise = new NoiseState(instance, 1, "Active", "隔离采样", "fixture", "合成麦克风",
            DateTimeOffset.Now, -57, "Good", new(30, 29.6, 0.98, -78.6, -48.3, 0, 100),
            [new(1, -65, "Good"), new(1.5, -57, "Good")], SessionId: session);
        var display = new NoiseDisplayState("FALLBACK", "隔离旧会话", true, instance, session,
            new(DateTimeOffset.Now.AddMinutes(-1), DateTimeOffset.Now.AddMinutes(20)), ReturnMinutes: 10);
        // Never invoke MainWindow.Start, any actual Host, microphone or school endpoint.
        var window = new ScheduledNoiseWindow(pipe, () => { }) { Width = 1100, Height = 780, Left = 80, Top = 60, Topmost = false };
        var method = typeof(ScheduledNoiseWindow).GetMethod("ReturnAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    window.Apply(display, noise);
                    async Task<HostRequest> Read()
                    {
                        await server.WaitForConnectionAsync(timeout.Token);
                        return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
                    }
                    var read = Read();
                    var operation = (Task)method.Invoke(window, null)!;
                    PumpUntil(() => read.IsCompleted, "return request did not reach the fake Host");
                    var request = read.GetAwaiter().GetResult();
                    Assert(request.Capability == "noise.display.return" && request.NoiseDisplay == new NoiseDisplayCommand("return", instance, session) &&
                        Protocol.Validate(request) is null, "return carried unexpected identity or capture command");
                    Assert(!Button(owner, "ReturnButton").IsEnabled, "return button remains enabled during the request");
                    var duplicate = (Task)method.Invoke(window, null)!;
                    Assert(duplicate.IsCompletedSuccessfully, "repeated return was not locally ignored while waiting");
                    var currentNoise = noise with { InstanceId = newInstance ? Guid.NewGuid() : instance, SessionId = newSession ? Guid.NewGuid() : session, CurrentDbfs = -42 };
                    var currentDisplay = display with { InstanceId = currentNoise.InstanceId, SessionId = currentNoise.SessionId, Message = "隔离当前会话", ReturnMinutes = 5 };
                    window.Apply(currentDisplay, currentNoise);
                    if (outcome == "Disconnected") server.Disconnect();
                    else
                    {
                        var response = outcome == "ZeroLength" ? server.WriteAsync(new byte[4], timeout.Token).AsTask() :
                            outcome == "EmptyMessage" ? Protocol.WriteAsync<HostResponse?>(server, null, timeout.Token) :
                            Protocol.WriteAsync(server, new HostResponse(outcome == "Version" ? Protocol.Version + 1 : Protocol.Version,
                                outcome == "RequestId" ? Guid.NewGuid() : request.RequestId,
                                outcome is "RequestId" or "Version" ? "Succeeded" : outcome,
                                outcome == "Rejected" ? "DISPLAY_STORE_UNAVAILABLE" : null,
                                "隔离返回回执"), timeout.Token);
                        PumpUntil(() => response.IsCompleted, "return response write did not finish"); response.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => operation.IsCompleted, "return operation did not settle"); operation.GetAwaiter().GetResult();
                    bool changed = newInstance || newSession;
                    if (changed)
                    {
                        Snapshot(owner, $"scheduled-stale-{(newInstance ? "host" : "session")}-{outcome}.png");
                        Assert(window.IsVisible, "an old return success hid the newly active monitoring page");
                        Assert(((TextBlock)owner.FindName("MessageText")).Text == "隔离当前会话", "an old return error overwrote the new monitoring message");
                        Assert(((TextBlock)owner.FindName("LevelText")).Text == "-42.0" && Button(owner, "ReturnButton").IsEnabled &&
                            Button(owner, "ReturnButton").Content.ToString()!.Contains("5"), "fresh monitoring data or return controls were lost");

                    }
                    else if (outcome == "Succeeded") Assert(!window.IsVisible, "same-session success no longer returns to the board");
                    else
                    {
                        Assert(window.IsVisible && Button(owner, "ReturnButton").IsEnabled, "same-session failure hides the page or blocks retry");
                        Assert(((TextBlock)owner.FindName("MessageText")).Text == (outcome == "Rejected" ? "隔离返回回执" :
                            "后台暂未确认返回期限，请等待连接恢复。没有停止监测。"), "same-session failure feedback is missing");
                        if (outcome == "RequestId") Snapshot(owner, "scheduled-return-unconfirmed.png");
                    }
                    if (changed || outcome != "Succeeded")
                    {
                        // Only explicit retry sends another request, using the current session and a fresh correlation ID.
                        if (server.IsConnected) server.Disconnect();
                        var newRead = Read(); var newOperation = (Task)method.Invoke(window, null)!;
                        PumpUntil(() => newRead.IsCompleted, "fresh return did not connect");
                        var newRequest = newRead.GetAwaiter().GetResult();
                        Assert(newRequest.RequestId != request.RequestId && newRequest.Capability == "noise.display.return" &&
                            Protocol.Validate(newRequest) is null &&
                            newRequest.NoiseDisplay == new NoiseDisplayCommand("return", currentNoise.InstanceId, currentNoise.SessionId!.Value),
                            "fresh return has invalid correlation, capability or session identity");
                        var freshReply = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, newRequest.RequestId, "Succeeded", null, "隔离新返回成功"), timeout.Token);
                        PumpUntil(() => freshReply.IsCompleted && newOperation.IsCompleted, "fresh return did not settle");
                        freshReply.GetAwaiter().GetResult(); newOperation.GetAwaiter().GetResult();
                        Assert(!window.IsVisible, "fresh return success did not hide its own monitoring page");
                    }
                    Checks.Add($"Return {outcome}: {(newInstance ? "new Host" : newSession ? "new session" : "same session")}; correct visibility, feedback and retry identity; no capture command");
                }
                finally { window.Shutdown(); }
            });
        }
        finally { window.Shutdown(); }
    }
}
