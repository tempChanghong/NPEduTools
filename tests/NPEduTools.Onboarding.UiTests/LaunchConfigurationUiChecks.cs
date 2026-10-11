using System.IO.Pipes;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static Task LaunchConfigurationTask(MainWindow window, string method) =>
        (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
    private static void LaunchConfigurationSnapshot(MainWindow window, LaunchData data) =>
        typeof(MainWindow).GetMethod("ShowLaunchData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new HostResponse(Protocol.Version, Guid.NewGuid(), "Succeeded", null, "隔离读取", Launch: data)]);
    private static void RunLaunchConfigurationChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            foreach (string scenario in new[] { "save-lost", "reload-save", "reload-error-save", "reload-reload", "poll-reload", "poll-error" })
                RunLaunchConfigurationCheck(scenario);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunLaunchConfigurationCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.launch-config." + Guid.NewGuid().ToString("N");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 4,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server, string capability)
        {
            await server.WaitForConnectionAsync(deadline.Token);
            var request = await Protocol.ReadAsync<HostRequest>(server, deadline.Token);
            Assert(request.Capability == capability && Protocol.Validate(request) is null, "unexpected configuration request");
            return request;
        }
        var oldData = new LaunchData(new(1, @"C:\Old\ClassIsland.exe"), null);
        var now = DateTimeOffset.UtcNow;
        var freshData = new LaunchData(new(2, @"C:\New\ClassIsland.exe"),
            new(Guid.NewGuid(), @"C:\New\ClassIsland.exe", now, now, "Succeeded", null, "隔离新连接记录"));
        void Reply(NamedPipeServerStream server, HostRequest request, LaunchData data, string message)
        {
            var write = Protocol.WriteAsync(server, new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null, message, Launch: data), deadline.Token);
            PumpUntil(() => write.IsCompleted, "configuration response did not finish"); write.GetAwaiter().GetResult();
        }
        var window = IsolatedMainWindow(pipe);
        var lifetime = (CancellationTokenSource)typeof(MainWindow).GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        Task? poll = null;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    LaunchConfigurationSnapshot(window, oldData);
                    using var oldServer = Server();
                    bool polling = scenario is "poll-reload" or "poll-error";
                    Task<HostRequest>? oldRead = null; Task? oldTask = null;
                    if (scenario != "save-lost")
                    {
                        oldRead = Read(oldServer, polling ? "classisland.execution.get" : "classisland.config.get");
                        oldTask = polling ? poll = (Task)typeof(MainWindow).GetMethod("ManagementLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [lifetime.Token])!
                            : LaunchConfigurationTask(window, "ReloadConfigurationAsync");
                        PumpUntil(() => oldRead.IsCompleted, "older configuration query did not arrive"); oldRead.GetAwaiter().GetResult();
                    }
                    else oldServer.Dispose();
                    bool saving = scenario is "reload-save" or "reload-error-save" or "save-lost";
                    using var freshServer = Server();
                    if (saving)
                    {
                        ((TextBox)window.FindName("ExecutablePathBox")).Text = freshData.Settings.ExecutablePath;
                        async Task ServeSave()
                        {
                            var protection = await Read(freshServer, "noise.management.status");
                            await Protocol.WriteAsync(freshServer, new HostResponse(Protocol.Version, protection.RequestId, "Succeeded", null,
                                "隔离无定时保护", NoiseProtection: new(false, false, Guid.NewGuid(), null)), deadline.Token);
                            freshServer.Disconnect();
                            var save = await Read(freshServer, "classisland.config.set");
                            Assert(save.ExecutablePath == freshData.Settings.ExecutablePath && save.ExpectedRevision == 1 && save.NoiseAuthorization is null,
                                "save does not use the selected path and original revision");
                            if (scenario == "save-lost") freshServer.Disconnect();
                            else await Protocol.WriteAsync(freshServer, new HostResponse(Protocol.Version, save.RequestId, "Succeeded", null,
                                "隔离保存成功", Launch: freshData), deadline.Token);
                        }
                        var serveSave = ServeSave(); var saveTask = LaunchConfigurationTask(window, "SavePathAsync");
                        PumpUntil(() => saveTask.IsCompleted && serveSave.IsCompleted, "configuration save did not finish");
                        saveTask.GetAwaiter().GetResult(); serveSave.GetAwaiter().GetResult();
                    }
                    else
                    {
                        var freshRead = Read(freshServer, "classisland.config.get");
                        var reload = LaunchConfigurationTask(window, "ReloadConfigurationAsync");
                        PumpUntil(() => freshRead.IsCompleted, "newer configuration query did not arrive");
                        Reply(freshServer, freshRead.GetAwaiter().GetResult(), freshData, "隔离新读取");
                        PumpUntil(() => reload.IsCompleted, "newer configuration query did not complete"); reload.GetAwaiter().GetResult();
                    }
                    if (scenario != "save-lost")
                    {
                        string message = ((TextBlock)window.FindName("ConfigurationMessage")).Text;
                        string history = ((TextBlock)window.FindName("LaunchResultText")).Text;
                        bool startAvailable = Button(window, "StartButton").IsEnabled;
                        freshServer.Dispose();
                        using var nextServer = Server();
                        var nextRead = polling ? Read(nextServer, "classisland.execution.get") : null;
                        if (scenario is "reload-error-save" or "poll-error") oldServer.Disconnect();
                        else Reply(oldServer, oldRead!.GetAwaiter().GetResult(), oldData, "隔离旧读取");
                        PumpUntil(() => polling ? nextRead!.IsCompleted : oldTask!.IsCompleted, "older query did not finish");
                        if (!polling) oldTask!.GetAwaiter().GetResult();
                        Assert(((TextBox)window.FindName("ExecutablePathBox")).Text == freshData.Settings.ExecutablePath &&
                            (string?)typeof(MainWindow).GetField("_savedPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == freshData.Settings.ExecutablePath &&
                            (long)typeof(MainWindow).GetField("_configurationRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == 2,
                            "older configuration query overwrote the newly saved or reread path and revision");
                        Assert(((TextBlock)window.FindName("ConfigurationMessage")).Text == message && ((TextBlock)window.FindName("LaunchResultText")).Text == history && Button(window, "StartButton").IsEnabled == startAvailable,
                            "older configuration response or error overwrote the newer feedback or history");
                        if (polling)
                        {
                            Reply(nextServer, nextRead!.GetAwaiter().GetResult(), freshData with { Execution = freshData.Execution! with { Message = "隔离最新记录" } }, "隔离最新查询");
                            PumpUntil(() => ((TextBlock)window.FindName("LaunchResultText")).Text == "隔离最新记录", "fresh management query was ignored");
                        }
                    }
                    else
                    {
                        Assert(((TextBlock)window.FindName("ConfigurationMessage")).Text.Contains("未能确认"), "lost save was reported as successful");
                        freshServer.Dispose();
                        using var recoveryServer = Server();
                        var read = Read(recoveryServer, "classisland.execution.get");
                        poll = (Task)typeof(MainWindow).GetMethod("ManagementLoopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [lifetime.Token])!;
                        PumpUntil(() => read.IsCompleted, "lost save did not query the actual state");
                        Reply(recoveryServer, read.GetAwaiter().GetResult(), freshData, "隔离保存后的状态");
                        PumpUntil(() => ((TextBlock)window.FindName("LaunchResultText")).Text == freshData.Execution!.Message, "recovery state did not arrive");
                        Assert((string?)typeof(MainWindow).GetField("_savedPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) == freshData.Settings.ExecutablePath &&
                            (long)typeof(MainWindow).GetField("_configurationRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! == 2,
                            "fresh management status did not reconcile an uncertain save");
                    }
                    Checks.Add($"ClassIsland {scenario}: older requests cannot replace current configuration; recovery uses reads without replay");
                }
                finally { window.Close(); }
            });
        }
        finally
        {
            window.Close();
            if (poll is not null) { PumpUntil(() => poll.IsCompleted, "management poll did not stop with its window"); poll.GetAwaiter().GetResult(); }
        }
    }
}
