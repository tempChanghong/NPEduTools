using System.Collections.Concurrent;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.IPC;
using ClassIsland.Shared.IPC.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;
using dotnetCampus.Ipc.CompilerServices.GeneratedProxies;
using dotnetCampus.Ipc.IpcRouteds.DirectRouteds;
using dotnetCampus.Ipc.Pipes;
using NPEduTools.ClassIsland.Bridge.Contracts;

// Test-only server. Never occupy the real ClassIsland endpoint.
if (args.Length != 2 || !args[0].StartsWith("NPEduTools.Test.", StringComparison.Ordinal) ||
    args[1] is not ("bridge" or "healthy" or "hang" or "drop" or "error" or "empty" or "schedule" or "schedule-clock" or "schedule-switch" or "schedule-undefined")) return 2;
using var provider = new IpcProvider(args[0]);
var routed = new JsonIpcDirectRoutedProvider(provider);
var clients = new ConcurrentBag<string>();
if (args[1].StartsWith("schedule", StringComparison.Ordinal))
{
    var fixture = new ScheduleFixture(args[1]); fixture.Initialize();
    provider.CreateIpcJoint<IPublicLessonsService>(new ScheduledLessons(fixture));
    provider.CreateIpcJoint<IPublicProfileService>(new ScheduledProfile(fixture));
}
else provider.CreateIpcJoint<IPublicLessonsService>(new FakeLessons(args[1]));
if (args[1] == "bridge")
{
    var bridge = new FakeBridge(Environment.GetEnvironmentVariable("NPEEDUTOOLS_TEST_BRIDGE_CONTROL"));
    provider.CreateIpcJoint<IRecordingBridgeP0>(bridge);
    provider.CreateIpcJoint<IRecordingBridgeCalendar>(bridge);
}
provider.PeerConnected += (_, e) => clients.Add(e.Peer.PeerName);
provider.StartServer();
routed.StartServer();
Console.WriteLine("READY");
// Safety net for interrupted test runners; parents normally dispose this process immediately.
using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(args[1] == "bridge" ? 180 : 60));
try
{
    while (!lifetime.IsCancellationRequested)
    {
        await Task.Delay(75, lifetime.Token);
        foreach (string name in clients)
        {
            try
            {
                var peer = await routed.GetAndConnectClientAsync(name).WaitAsync(TimeSpan.FromMilliseconds(300));
                await peer.NotifyAsync(IpcRoutedNotifyIds.OnClassNotifyId).WaitAsync(TimeSpan.FromMilliseconds(300));
            }
            catch (Exception) { /* Test peers may have exited. */ }
        }
    }
}
catch (OperationCanceledException) { }
return 0;

internal sealed class FakeLessons(string mode) : IPublicLessonsService
{
    public bool IsTimerRunning => mode != "empty";
    public ClassPlan? CurrentClassPlan { get; set; }
    public int CurrentSelectedIndex { get; set; } = mode == "empty" ? -1 : 2;
    public Subject NextClassSubject { get; set; } = new();
    public TimeLayoutItem NextBreakingTimeLayoutItem { get; set; } = new();
    public TimeLayoutItem NextClassTimeLayoutItem { get; set; } = new();
    public TimeSpan OnClassLeftTime { get; set; }
    public TimeSpan OnBreakingTimeLeftTime { get; set; }
    public TimeState CurrentState
    {
        get
        {
            if (mode == "hang") Thread.Sleep(Timeout.Infinite);
            if (mode == "drop") Environment.Exit(0);
            if (mode == "error") throw new InvalidOperationException("Simulated service failure");
            return mode == "empty" ? TimeState.None : TimeState.OnClass;
        }
        set => throw new NotSupportedException();
    }
    public TimeLayoutItem CurrentTimeLayoutItem { get; set; } = new();
    public Subject? CurrentSubject { get; set; } = mode == "empty" ? null : new() { Name = "数学" };
    public bool IsClassPlanEnabled { get; set; } = mode != "empty";
    public bool IsClassPlanLoaded { get; set; } = mode != "empty";
    public bool IsLessonConfirmed { get; set; } = mode != "empty";
    public ClassPlan? GetClassPlanByDate(DateTime date) => null;
}
