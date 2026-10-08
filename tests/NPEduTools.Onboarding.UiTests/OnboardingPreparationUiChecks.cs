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
    private static OnboardingWindow PreparationWindow(string pipe, OnboardingState state, Action<OnboardingState> save)
    {
        var actionsType = typeof(OnboardingWindow).Assembly.GetType("NPEduTools.App.OnboardingActions")!;
        Action nothing = () => { };
        var actions = Activator.CreateInstance(actionsType, Enumerable.Repeat<object>(nothing, 9).ToArray())!;
        var session = new NpepConnectionSession((_, _) => Task.FromException<HostResponse>(new IOException("isolated fixture")));
        var constructor = typeof(OnboardingWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (OnboardingWindow)constructor.Invoke([pipe, state, save, session, actions, (Action<bool>)(_ => { }), null]);
    }
    private static void RunOnboardingPreparationChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (int stage in Enumerable.Range(0, 3))
                foreach (bool lostReply in new[] { false, true })
                    try { RunClockNavigationCheck(stage, lostReply); }
                    catch (Exception error) { failures.Add(new InvalidOperationException($"clock stage {stage}, lost reply {lostReply}: {error.Message}", error)); }
            if (failures.Count != 0) throw new AggregateException(failures);
            RunPreparationNavigationCheck("noise", false); RunPreparationNavigationCheck("noise", true); RunPreparationNavigationCheck("classroom", false);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void RunClockNavigationCheck(int stage, bool lostReply)
    {
        string pipe = "NPEduTools.Test.onboarding-clock." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        string[] capabilities = ["classisland.config.get", "classisland.status", "classisland.school-clock"];
        HostResponse Reply(HostRequest request, string marker) => new(Protocol.Version, request.RequestId, "Succeeded", null, marker,
            Launch: request.Capability == capabilities[0] ? new(new(1, marker), null, null) : null,
            SchoolClock: request.Capability == capabilities[2] ? SchoolClockFrame.Unavailable(marker) : null);
        void Send(NamedPipeServerStream server, HostRequest request, string marker)
        {
            var write = Protocol.WriteAsync(server, Reply(request, marker), timeout.Token);
            PumpUntil(() => write.IsCompleted, "clock response did not finish"); write.GetAwaiter().GetResult();
        }
        var saves = new List<OnboardingState>();
        string freshPath = Path.GetTempFileName(); // Existence check only; never executed.
        using var initialServer = Server();
        var window = PreparationWindow(pipe, new(Features: OnboardingFeatures.Automatic, Step: "classisland"), saves.Add);
        window.Width = 900; window.Height = 680;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    // No timer fallback: navigation must refresh immediately after the stale read finishes.
                    ((DispatcherTimer)typeof(OnboardingWindow).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                    using var statusServer = stage >= 1 ? Server() : null;
                    using var clockServer = stage >= 2 ? Server() : null;
                    var oldRead = Read(initialServer);
                    PumpUntil(() => oldRead.IsCompleted, "initial clock configuration read did not arrive");
                    var oldRequest = oldRead.GetAwaiter().GetResult();
                    NamedPipeServerStream oldServer = initialServer;
                    for (int index = 0; index < stage; index++)
                    {
                        Assert(oldRequest.Capability == capabilities[index], "unexpected initial clock capability");
                        var nextServer = index == 0 ? statusServer! : clockServer!;
                        var nextRead = Read(nextServer);
                        Send(oldServer, oldRequest, "old-configuration");
                        PumpUntil(() => nextRead.IsCompleted, "next clock read did not arrive");
                        oldServer = nextServer; oldRequest = nextRead.GetAwaiter().GetResult();
                    }
                    Assert(oldRequest.Capability == capabilities[stage], "wrong clock read held for navigation");
                    Click(owner, "BackButton"); Click(owner, "SkipButton");
                    Assert(((TextBlock)owner.FindName("StepTitle")).Text == "连接学校时间", "navigation did not return to clock preparation");
                    string Texts() => string.Join("|", new[] { "PathText", "ConnectionText", "BridgeText", "ClockText" }
                        .Select(name => ((TextBlock)owner.FindName(name)).Text));
                    string before = Texts();
                    using var freshServer = Server();
                    var freshRead = Read(freshServer);
                    if (lostReply) oldServer.Disconnect();
                    else Send(oldServer, oldRequest, "obsolete-clock-result");
                    PumpUntil(() => freshRead.IsCompleted || Texts() != before, "clock revisit did not finish or refresh");
                    Snapshot(owner, $"onboarding-clock-{stage}-{(lostReply ? "old-error" : "old-response")}.png");
                    Assert(Texts() == before, "a previous visit's clock result or error modified the revisited page");
                    Assert(freshRead.IsCompleted, "clock revisit did not start a fresh configuration read");
                    var freshRequest = freshRead.GetAwaiter().GetResult();
                    Assert(freshRequest.Capability == capabilities[0], "revisited clock check continued the previous visit's request sequence");
                    // Free completed servers before supplying the new visit's remaining read-only replies.
                    initialServer.Dispose(); statusServer?.Dispose(); clockServer?.Dispose();
                    using var freshStatusServer = Server();
                    var freshStatusRead = Read(freshStatusServer);
                    Send(freshServer, freshRequest, freshPath);
                    PumpUntil(() => freshStatusRead.IsCompleted, "fresh connection read did not arrive");
                    var freshStatusRequest = freshStatusRead.GetAwaiter().GetResult();
                    Assert(freshStatusRequest.Capability == capabilities[1], "unexpected fresh connection capability");
                    using var freshClockServer = Server();
                    var freshClockRead = Read(freshClockServer);
                    Send(freshStatusServer, freshStatusRequest, "fresh-status");
                    PumpUntil(() => freshClockRead.IsCompleted, "fresh school clock read did not arrive");
                    var freshClockRequest = freshClockRead.GetAwaiter().GetResult();
                    Assert(freshClockRequest.Capability == capabilities[2], "unexpected fresh school clock capability");
                    Send(freshClockServer, freshClockRequest, "fresh-clock");
                    PumpUntil(() => !(bool)typeof(OnboardingWindow).GetField("_clockBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!,
                        "fresh clock check did not finish");
                    Assert(((TextBlock)owner.FindName("ClockText")).Text.StartsWith("学校时间：暂不可用"), "unavailable fresh clock did not populate the revisited page");
                    Assert(((TextBlock)owner.FindName("PathText")).Text.Contains(freshPath), "fresh configuration did not replace the old path");
                    Assert(!Check(owner, "ConfirmClock").IsEnabled && Check(owner, "ConfirmClock").IsChecked != true,
                        "unavailable synthetic clock enabled or confirmed school-time approval");
                    Assert(saves.Count == 2 && saves[^1].Step == "classisland" && !saves[^1].Completed &&
                        !(saves[^1].Reviewed ?? []).Contains("classisland"), "clock reads changed walkthrough completion");
                    Checks.Add($"clock stage {stage}: previous visit's {(lostReply ? "error" : "response")} ignored; fresh three-read check without timer fallback or implicit approval");
                }
                finally { window.Shutdown(); }
            });
        }
        finally { window.Shutdown(); File.Delete(freshPath); }
    }
    private static void RunPreparationNavigationCheck(string step, bool lostReply)
    {
        string pipe = "NPEduTools.Test.onboarding-preparation." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        NamedPipeServerStream Server() => new(pipe, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        async Task<HostRequest> Read(NamedPipeServerStream server)
        {
            await server.WaitForConnectionAsync(timeout.Token);
            return await Protocol.ReadAsync<HostRequest>(server, timeout.Token);
        }
        using var oldServer = Server();
        var saves = new List<OnboardingState>();
        var window = PreparationWindow(pipe, new(Features: step == "noise" ? OnboardingFeatures.Noise : OnboardingFeatures.Classroom, Step: step), saves.Add);
        window.Width = 900; window.Height = 680;
        try
        {
            Exercise(window, owner =>
            {
                try
                {
                    string Text() => ((TextBlock)owner.FindName(step == "noise" ? "NoiseText" : "ClassroomText")).Text;
                    var oldRead = Read(oldServer);
                    PumpUntil(() => oldRead.IsCompleted, "initial preparation read did not arrive");
                    var oldRequest = oldRead.GetAwaiter().GetResult();
                    string capability = step == "noise" ? "noise.status" : "classisland.config.get";
                    Assert(oldRequest.Capability == capability, "unexpected preparation capability");
                    Click(owner, "BackButton");
                    Assert(((TextBlock)owner.FindName("StepTitle")).Text == "使用偏好", "back navigation did not leave preparation");
                    Click(owner, "SkipButton");
                    Assert(((TextBlock)owner.FindName("StepTitle")).Text == (step == "noise" ? "准备噪音监测" : "准备考试环境"), "return navigation did not reach the original step");
                    using var currentServer = Server();
                    var currentRead = Read(currentServer);
                    var noise = new NoiseState(Guid.NewGuid(), 1, "Stopped", "隔离旧监测状态", null, null, null, null, "NoData", null, []);
                    if (lostReply) oldServer.Disconnect();
                    else
                    {
                        var oldReply = Protocol.WriteAsync(oldServer, new HostResponse(Protocol.Version, oldRequest.RequestId, "Succeeded", null, "隔离旧检查",
                            Noise: step == "noise" ? noise : null, Launch: step == "classroom" ? new(new(1, null), null, null) : null), timeout.Token);
                        PumpUntil(() => oldReply.IsCompleted, "old preparation reply did not finish"); oldReply.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => currentRead.IsCompleted || Text().Contains("隔离旧监测状态") || Text().Contains("后台状态暂不可用"), "preparation did not complete or refresh after returning to its page");
                    Snapshot(owner, $"onboarding-{step}-{(lostReply ? "old-error" : "old-read")}.png");
                    Assert(!Text().Contains("隔离旧监测状态") && !Text().Contains("后台状态暂不可用"),
                        "a preparation read from a previous visit was presented as the current visit's result after navigating away and back");
                    Assert(currentRead.IsCompleted, "returning to the same step did not trigger a fresh configuration read");
                    oldServer.Dispose();
                    var currentRequest = currentRead.GetAwaiter().GetResult();
                    Assert(currentRequest.Capability == capability && Protocol.Validate(currentRequest) is null, "fresh preparation sent an unexpected request");
                    using var examServer = step == "classroom" ? Server() : null;
                    var examRead = examServer is null ? null : Read(examServer);
                    var currentReply = Protocol.WriteAsync(currentServer, new HostResponse(Protocol.Version, currentRequest.RequestId, "Succeeded", null, "隔离新检查",
                        Noise: step == "noise" ? noise with { Message = "隔离新监测状态" } : null,
                        Launch: step == "classroom" ? new(new(2, null), null, null) : null), timeout.Token);
                    PumpUntil(() => currentReply.IsCompleted, "fresh preparation reply did not finish"); currentReply.GetAwaiter().GetResult();
                    if (examRead is not null)
                    {
                        PumpUntil(() => examRead.IsCompleted, "fresh exam preparation read did not arrive");
                        var examRequest = examRead.GetAwaiter().GetResult();
                        Assert(examRequest.Capability == "examaware.status", "fresh classroom read did not request ExamAware status");
                        var examReply = Protocol.WriteAsync(examServer!, new HostResponse(Protocol.Version, examRequest.RequestId, "Succeeded", null, "隔离考试配置",
                            ExamAware: ReadyExamAware() with { ExecutablePath = null, BridgeState = "Disconnected" }), timeout.Token);
                        PumpUntil(() => examReply.IsCompleted, "fresh exam response did not finish"); examReply.GetAwaiter().GetResult();
                    }
                    PumpUntil(() => step == "noise" ? Text().Contains("隔离新监测状态") : Text().Contains("考试看板桥接：尚未确认连接"), "fresh result did not populate the current preparation page");
                    Snapshot(owner, $"onboarding-{step}-{(lostReply ? "error-recovered" : "fresh-read")}.png");
                    Assert(saves.Count == 2 && saves[^1].Step == step && saves[^1].Skipped!.Contains("preferences") &&
                        !(saves[^1].Reviewed ?? []).Contains(step) && !saves[^1].Completed, "read-only preparation changed the completion or navigation record");
                    Checks.Add($"{step}: leave and return while reading; old {(lostReply ? "connection error" : "response")} ignored; fresh read populates the page without completing the step");
                }
                finally { window.Shutdown(); }
            });
        }
        finally { window.Shutdown(); }
    }
}
