using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunAutomaticRecoveryChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "dropped", "missing", "mismatch", "rejected", "confirmed" })
                RunAutomaticReceiptCheck(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunAutomaticReceiptCheck(string scenario)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string pipe = "NPEduTools.Test.automatic-recovery." + Guid.NewGuid().ToString("N");
        string preferences = StartupPreferencesStore.PathFor(pipe);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldStatus = Server();
        var clientType = typeof(AutoRecordingWindow).Assembly.GetType("NPEduTools.App.RecordingClient")!;
        var client = Activator.CreateInstance(clientType, [Application.Current.Dispatcher, pipe])!;
        var observations = new List<AutomaticRecordingState>();
        clientType.GetEvent("AutomaticChanged")!.AddEventHandler(client, (Action<AutomaticRecordingState>)(observations.Add));
        var apply = clientType.GetMethod("Apply", flags)!;
        var enabled = new AutomaticRecordingState(true, Guid.NewGuid(), "隔离快照：自动录课已开启", null, []);
        var active = new RecordingState("Recording", "隔离快照：正在录制", Control: new("Automatic", Guid.NewGuid(), "fixture", 0, 0));
        var recovered = enabled with { Enabled = false, Message = "隔离快照：自动录课已关闭" };
        AutoRecordingWindow? window = null;
        bool Available() => (bool)clientType.GetProperty("StateAvailable")!.GetValue(client)!;
        try
        {
            var read = Read(oldStatus);
            PumpUntil(() => read.IsCompleted, "initial poll did not reach test server");
            Assert(read.GetAwaiter().GetResult().Capability == "recording.status", "unexpected initial poll");
            // Keep this pre-command query pending so a lost receipt cannot be hidden by a later poll.
            apply.Invoke(client, [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离状态", Recording: active, Automatic: enabled)]);
            window = (AutoRecordingWindow)typeof(AutoRecordingWindow).GetConstructors(flags).Single()
                .Invoke([pipe, client, (Action)(() => { })]);
            string Text(string name) => ((TextBlock)window.FindName(name)).Text;
            string trialBefore = Text("PreviewStatus");
            int commands = 0;
            using var commandServer = Server();
            async Task Reply()
            {
                var request = await Read(commandServer);
                Assert(request.Capability == "recording.automatic" && request.Automatic?.Action == "disable", "unexpected mutation in isolated receipt test");
                commands++;
                if (scenario == "dropped") { commandServer.Disconnect(); return; }
                var response = new HostResponse(Protocol.Version, scenario == "mismatch" ? Guid.NewGuid() : request.RequestId,
                    scenario == "rejected" ? "Rejected" : "Succeeded", scenario == "rejected" ? "FixtureRejected" : null,
                    scenario == "rejected" ? "隔离拒绝：环境忙碌" : "隔离命令结果",
                    Recording: active, Automatic: scenario == "missing" ? null : scenario == "rejected" ? enabled : recovered);
                await Protocol.WriteAsync(commandServer, response, timeout.Token);
            }
            var reply = Reply();
            var action = (Task)typeof(AutoRecordingWindow).GetMethod("AutomaticActionAsync", flags)!.Invoke(window, ["disable", null])!;
            PumpUntil(() => action.IsCompleted && reply.IsCompleted, "automatic operation did not settle");
            action.GetAwaiter().GetResult(); reply.GetAwaiter().GetResult();
            Assert(commands == 1, "automatic mutation was replayed");
            Assert(Text("PreviewStatus") == trialBefore, "automatic receipt altered independent trial state");
            if (scenario is "dropped" or "missing" or "mismatch")
            {
                Assert(!Available() && Text("RealStatus").Contains("连接中断"), scenario + ": uncertain automatic receipt leaves a confirmed live status");
                Assert(Text("AutomaticFeedbackText").Contains("未确认"), scenario + ": missing uncertainty feedback");
                Assert((AutomaticRecordingState)clientType.GetProperty("Automatic")!.GetValue(client)! == enabled,
                    "uncertain receipt discarded the last automatic snapshot");
                Assert(((RecordingState)clientType.GetProperty("State")!.GetValue(client)!).Control == active.Control,
                    "uncertain receipt lost the reconciliation session");
            }
            else if (scenario == "rejected")
            {
                Assert(Available() && Text("RealStatus").Contains(enabled.Message), "authoritative rejection became a connection failure");
                Assert(Text("AutomaticFeedbackText").Contains("环境忙碌"), "rejection reason vanished");
            }
            else
            {
                Assert(Available() && Text("RealStatus").Contains(recovered.Message), "confirmed automatic receipt did not update actual state");
                Assert(((TextBlock)window.FindName("AutomaticFeedbackText")).Visibility == Visibility.Collapsed, "confirmed success retains operation error");
            }

            // Resume normal client polling. A stale pre-command query must not restore old state.
            commandServer.Dispose();
            using var freshServer = Server();
            async Task ServeRecovery()
            {
                try
                {
                    while (!timeout.IsCancellationRequested)
                    {
                        var request = await Read(freshServer);
                        Assert(request.Capability == "recording.status" || request.Automatic?.Action == "lease", "recovery replayed an automatic mutation");
                        await Protocol.WriteAsync(freshServer, new HostResponse(Protocol.Version, request.RequestId,
                            "Succeeded", null, "隔离恢复", Recording: new("Idle", "隔离恢复：空闲"), Automatic: recovered), timeout.Token);
                        freshServer.Disconnect();
                    }
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            var recovery = ServeRecovery();
            int beforeOldReply = observations.Count;
            var obsolete = Protocol.WriteAsync(oldStatus, new HostResponse(Protocol.Version, read.Result.RequestId,
                "Succeeded", null, "隔离旧查询", Recording: active, Automatic: enabled), timeout.Token);
            PumpUntil(() => obsolete.IsCompleted, "old status response did not finish");
            obsolete.GetAwaiter().GetResult();
            PumpUntil(() => Available() && Text("RealStatus").Contains(recovered.Message), "fresh poll did not recover automatic state");
            Assert(observations.Skip(beforeOldReply).All(state => state.Message != enabled.Message), "pre-command status briefly restored an obsolete automatic state");
            Assert(commands == 1 && Button(window, "ToggleReal").Content.ToString() == "开启自动录课", "recovery replayed mutation or retained wrong master toggle");
            timeout.Cancel();
            PumpUntil(() => recovery.IsCompleted, "recovery test server did not stop");
            recovery.GetAwaiter().GetResult();
            Checks.Add(scenario + ": real automatic command receipt and window feedback; fresh polling restores state without replaying the mutation or changing trial state");
        }
        finally
        {
            window?.Shutdown();
            clientType.GetMethod("Detach")!.Invoke(client, null);
            var poll = (Task)clientType.GetField("_poll", flags)!.GetValue(client)!;
            PumpUntil(() => poll.IsCompleted, "automatic test client did not stop");
            ((IAsyncDisposable)client).DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (string suffix in new[] { ".recording-plans.json", ".recording-preview.json" })
            {
                string path = preferences.Replace(".startup.json", suffix, StringComparison.Ordinal);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
        }
    }
}
