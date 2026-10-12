using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunClassIslandClockChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher));
        string pipe = "NPEduTools.Test.adjusted-clock." + Guid.NewGuid().ToString("N");
        using var lifetime = new CancellationTokenSource();
        int stale = 0;
        var adjusted = new DateTimeOffset(2026, 10, 12, 19, 0, 42, TimeSpan.Zero);
        Guid instance = Guid.NewGuid(), connection = Guid.NewGuid(); long sequence = 0;
        var serving = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(lifetime.Token);
                var request = await Protocol.ReadAsync<HostRequest>(server, lifetime.Token);
                var response = new HostResponse(1, request.RequestId, "Succeeded", null, "fixture");
                if (request.Capability == "classisland.school-clock") response = response with {
                    SchoolClock = new(connection, instance, ++sequence, 1, adjusted,
                        Volatile.Read(ref stale) == 1 ? 4000 : 0, "Advancing", "fixture adjusted ClassIsland time") };
                await Protocol.WriteAsync(server, response, lifetime.Token);
            }
        });
        var window = IsolatedMainWindow(pipe);
        try
        {
            Exercise(window, owner =>
            {
                void Refresh() => typeof(MainWindow).GetMethod("RefreshClassIslandBridgeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                Refresh();
                PumpUntil(() => ((TextBlock)owner.FindName("ClassIslandClockText")).Text == "2026-10-12 19:00:42", "settings did not use adjusted school time through private pipe");
                Assert(((TextBlock)owner.FindName("ClassIslandClockDetail")).Text.Contains("ClassIsland 调整后的学校时间"), "time source missing");
                Checks.Add("Software connection shows the bridge's adjusted school timestamp independently of mode; no Windows fallback");
                Interlocked.Exchange(ref stale, 1); Refresh();
                PumpUntil(() => ((TextBlock)owner.FindName("ClassIslandClockText")).Text == "--:--:--", "stale bridge time remained displayed as current");
                Checks.Add("Stale bridge time is withdrawn; real ClassIsland/Windows clock settings were not changed");
                owner.Close();
            });
        }
        finally
        {
            window.Close(); lifetime.Cancel(); try { serving.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }
}
