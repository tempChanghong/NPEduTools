using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static MainWindow IsolatedMainWindow(string pipe)
    {
        // Only XAML and handlers. No tray, stores, recording poll, guard or MainWindow.Start.
        var constructor = typeof(MainWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (MainWindow)constructor.Invoke([pipe, null, false]);
    }
    private static Task ChangeTouch(MainWindow window, string action) =>
        (Task)typeof(MainWindow).GetMethod("ChangeTouchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [action])!;
    private static void ShowTouch(MainWindow window, TouchAssistState state) =>
        typeof(MainWindow).GetMethod("ApplyTouch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "合成状态", TouchAssist: state)]);
    private static void RunTouchStatusChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try { RunTouchOrderingCheck(false); RunTouchOrderingCheck(true); RunTouchOrderingCheck(false, lostCommand: true); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunTouchOrderingCheck(bool oldFailure, bool lostCommand = false)
    {
        string pipe = "NPEduTools.Test.touch-ui." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldServer = Server();
        var window = IsolatedMainWindow(pipe);
        if (lostCommand) { window.Width = 840; window.Height = 600; }
        var lifetime = (CancellationTokenSource)typeof(MainWindow).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Task? poll = null;
        var stopped = new TouchAssistState(false, false, false, "隔离已停止");
        var running = new TouchAssistState(true, false, false, "隔离正在辅助");
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    ShowTouch(window, stopped);
                    var oldRead = Read(oldServer);
                    poll = (Task)typeof(MainWindow).GetMethod("TouchPollAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [lifetime.Token])!;
                    PumpUntil(() => oldRead.IsCompleted, "initial touch query did not arrive");
                    var oldRequest = oldRead.GetAwaiter().GetResult();
                    Assert(oldRequest.Capability == "presentation.touch.status", "unexpected initial touch query");
                    using var commandServer = Server();
                    async Task Reply()
                    {
                        var request = await Read(commandServer);
                        Assert(request.Capability == "presentation.touch.enable" && Protocol.Validate(request) is null && !Protocol.NoiseInterruption(request),
                            "fixture sent an unexpected command or requested management authorization");
                        if (lostCommand) { commandServer.Disconnect(); return; }
                        await Protocol.WriteAsync(commandServer, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                            "隔离开启成功", TouchAssist: running), timeout.Token);
                    }
                    var reply = Reply(); var command = ChangeTouch(window, "enable");
                    Assert(!Button(owner, "TouchPowerButton").IsEnabled && !Button(owner, "TouchPauseButton").IsEnabled && !Check(owner, "TouchCompatibility").IsEnabled,
                        "touch controls remain enabled while processing");
                    PumpUntil(() => command.IsCompleted && reply.IsCompleted, "touch operation did not finish");
                    command.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
                    if (lostCommand) Assert(!Button(owner, "TouchPowerButton").IsEnabled && ((TextBlock)owner.FindName("TouchStatusText")).Text.Contains("暂未确认"),
                        "lost operation response was presented as a known state");
                    else Assert(Button(owner, "TouchPowerButton").Content.ToString() == "停止辅助", "new touch state did not render");
                    string power = Button(owner, "TouchPowerButton").Content.ToString()!;
                    string status = ((TextBlock)owner.FindName("TouchStatusText")).Text;
                    commandServer.Dispose();
                    using var freshServer = Server(); var freshRead = Read(freshServer);
                    if (oldFailure) oldServer.Disconnect();
                    else
                    {
                        var oldReply = Protocol.WriteAsync(oldServer, new HostResponse(Protocol.Version, oldRequest.RequestId, "Succeeded", null,
                            "隔离旧状态", TouchAssist: stopped), timeout.Token);
                        PumpUntil(() => oldReply.IsCompleted, "old touch reply did not finish"); oldReply.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => freshRead.IsCompleted || Button(owner, "TouchPowerButton").Content.ToString() != power ||
                        ((TextBlock)owner.FindName("TouchStatusText")).Text != status, "touch query did not resume after the older result");
                    ((ScrollViewer)owner.FindName("HomePage")).ScrollToBottom(); owner.UpdateLayout();
                    Snapshot(owner, lostCommand ? "touch-lost-operation.png" : oldFailure ? "touch-after-old-error.png" : "touch-after-old-query.png");
                    Assert(Button(owner, "TouchPowerButton").Content.ToString() == power && Button(owner, "TouchPowerButton").IsEnabled == !lostCommand &&
                        ((TextBlock)owner.FindName("TouchStatusText")).Text == status, "an older query or its error overwrote the newer touch operation result");
                    var freshRequest = freshRead.GetAwaiter().GetResult();
                    Assert(freshRequest.Capability == "presentation.touch.status", "touch operation was replayed instead of reading fresh status");
                    var freshReply = Protocol.WriteAsync(freshServer, new HostResponse(Protocol.Version, freshRequest.RequestId, "Succeeded", null,
                        "隔离新暂停", TouchAssist: running with { Paused = true, State = "隔离已暂停" }), timeout.Token);
                    PumpUntil(() => freshReply.IsCompleted, "fresh touch response did not finish"); freshReply.GetAwaiter().GetResult();
                    PumpUntil(() => Button(owner, "TouchPauseButton").Content.ToString() == "继续辅助", "fresh touch state was ignored");
                    Assert(Button(owner, "TouchPowerButton").IsEnabled && Button(owner, "TouchPauseButton").IsEnabled && Check(owner, "TouchCompatibility").IsEnabled,
                        "fresh state did not recover the available touch controls");
                    Checks.Add(lostCommand ? "Lost operation receipt remains uncertain despite an older status reply; a fresh query restores controls without replaying the command" :
                        $"Old touch query {(oldFailure ? "error" : "response")} cannot replace the successful operation; fresh paused snapshot restores current controls");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "isolated touch poll did not stop when its window closed"); poll.GetAwaiter().GetResult(); }
        }
    }
}
