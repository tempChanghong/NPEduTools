using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Host;

internal static partial class Program
{
    private static void RunOnboardingSecRandomChecks()
    {
        string directory = Path.Combine(_output, "onboarding-secrandom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string executable = Path.Combine(directory, "SecRandom.Desktop.exe");
        File.WriteAllText(executable, "inert existence fixture; never executed");
        string settings = Path.Combine(directory, "secrandom.json");
        var pending = new SecRandomOperation(Guid.NewGuid(), "quick-draw", "Running", "isolated pending intent", DateTimeOffset.UtcNow);
        File.WriteAllText(settings, JsonSerializer.Serialize(new { Version = 1, Path = executable, Revision = 2, Receipts = new[] { pending } }, Protocol.Json));
        byte[] original = File.ReadAllBytes(settings);
        SecRandomState unavailable;
        using (var held = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Real Host startup must rewrite an old intent before allowing new operations.
            // Deny that rename on this fixture only; status remains readable with Error set.
            var failed = new SecRandomService(directory, new RuntimeOperationGate());
            try
            {
                var status = failed.Handle(new(Protocol.Version, Guid.NewGuid(), "secrandom.status"));
                Assert(status.Outcome == "Succeeded" && status.SecRandom is { Error: not null, ExecutablePath: not null },
                    "fixture did not reproduce a readable but unavailable Host configuration");
                unavailable = status.SecRandom!;
                Assert(File.ReadAllBytes(settings).SequenceEqual(original), "failed recovery overwrote the original intent");
            }
            finally { failed.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        var restored = new SecRandomService(directory, new RuntimeOperationGate());
        SecRandomState healthy;
        try { healthy = restored.Snapshot(); }
        finally { restored.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Assert(healthy.Error is null && healthy.Operation?.State == "Unknown", "released fixture did not recover without replaying the old draw");
        Checks.Add("OOBE SecRandom fixture: real Host preserves a locked pending intent, reports configuration failure, and recovers on reopen without executing a draw");

        string pipe = "NPEduTools.Test.onboarding-secrandom." + Guid.NewGuid().ToString("N");
        var requests = new ConcurrentQueue<HostRequest>();
        var current = unavailable;
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            requests.Enqueue(request);
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, "Succeeded", null,
                "isolated status", SecRandom: Volatile.Read(ref current)));
        }, stop.Token));
        var saves = new List<OnboardingState>();
        var window = PreparationWindow(pipe, new(Features: OnboardingFeatures.SecRandom, Step: "secrandom"), saves.Add);
        bool Busy() => (bool)typeof(OnboardingWindow).GetField("_preparationBusy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var previous = SynchronizationContext.Current;
        var context = new QuickEventContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            PumpUntil(() => !Busy(), "initial SecRandom preparation read did not settle");
            Click(window, "NextButton"); SettleManagementEvent(context);
            Assert(saves.Count == 0 && ((TextBlock)window.FindName("StepTitle")).Text == "配置课堂点名",
                "an existing executable bypassed the Host configuration error and completed preparation");
            Assert(((TextBlock)window.FindName("SecRandomText")).Text.Contains(unavailable.Error!) &&
                !string.IsNullOrWhiteSpace(((TextBlock)window.FindName("ErrorText")).Text),
                "unavailable configuration is reported as ready or hides the Host's repair guidance");
            Assert(Button(window, "NextButton").IsEnabled && Button(window, "SkipButton").IsEnabled,
                "failed preparation left navigation stuck");
            Checks.Add("OOBE SecRandom: existing executable plus Host error cannot complete the step; error guidance and navigation remain available");

            Click(window, "SkipButton");
            Assert(saves.Last().Step == "review" && saves.Last().Skipped!.Contains("secrandom") &&
                !(saves.Last().Reviewed ?? []).Contains("secrandom"), "explicit skip is incorrectly recorded as completed preparation");
            Checks.Add("OOBE SecRandom: configuration failure can be explicitly skipped and remains marked for later setup");

            Click(window, "BackButton"); PumpUntil(() => !Busy(), "revisited SecRandom preparation did not settle");
            Volatile.Write(ref current, healthy);
            Click(window, "NextButton"); SettleManagementEvent(context);
            Assert(saves.Last().Step == "review" && saves.Last().Reviewed!.Contains("secrandom") &&
                !(saves.Last().Skipped ?? []).Contains("secrandom"), "fresh healthy configuration cannot replace a prior skip");
            Assert(((TextBlock)window.FindName("SecRandomText")).Text.Contains("不会启动软件") &&
                requests.All(request => request.Capability == "secrandom.status" && request.SecRandom is null),
                "preparation recovery launched software, probed the upstream or replayed a draw");
            Checks.Add("OOBE SecRandom: a fresh healthy Host read clears the prior skip; preparation remains local and read-only without launching or drawing");
        }
        finally
        {
            window.Shutdown(); SynchronizationContext.SetSynchronizationContext(previous);
            stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
