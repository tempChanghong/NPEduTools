using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunQuickCalibrationSnapshotChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (string scenario in new[] { "unknown-scene", "unknown-label", "null-samples", "null-sample", "null-suggestion", "invalid-suggestion" })
                try { QuickCalibrationSnapshotCheck(scenario); } catch (Exception e) { failures.Add(new InvalidOperationException(scenario + ": " + e.Message, e)); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void QuickCalibrationSnapshotCheck(string scenario)
    {
        string pipe = "NPEduTools.Test.quick-snapshot." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); int phase = 0;
        var sample = new NoiseCalibrationSample(Guid.NewGuid(), "NormalStudy", "Acceptable", 15, 1, -60, -55, -50, 0, "Good");
        var valid = new NoiseCalibrationState(Guid.NewGuid(), 1, Guid.NewGuid(), "synthetic", "scope", "Ready", 0, [sample],
            new(false, null, null, "no suggestion"), new(1, "synthetic", "fixture-signature", "scope", -50, -40, DateTimeOffset.UtcNow, [sample]), true, null, "Synthetic");
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            Assert(request.Capability is "noise.management.status" or "noise.calibration.status", "quick recovery sampled or saved");
            var state = Volatile.Read(ref phase) != 1 ? valid : scenario switch
            {
                "unknown-scene" => valid with { Samples = [sample with { Scene = "UnknownScene" }] },
                "unknown-label" => valid with { Samples = [sample with { Label = "UnknownLabel" }] },
                "null-samples" => valid with { Samples = null! },
                "null-sample" => valid with { Samples = [null!] },
                "null-suggestion" => valid with { Suggestion = null! },
                _ => valid with { Suggestion = new(true, null, -30, "invalid suggestion") }
            };
            if (Volatile.Read(ref phase) == 1) state = state with { Revision = 2 };
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic quick calibration",
                NoiseCalibration: request.Capability == "noise.calibration.status" ? state : null));
        }, stop.Token));
        var window = new NoiseCalibrationWindow(pipe);
        try
        {
            Task Read() => (Task)typeof(NoiseCalibrationWindow).GetMethod("ReadAsync", WorkspaceFlags)!.Invoke(window, null)!;
            var read = Read(); PumpUntil(() => read.IsCompleted, "initial quick read stalled"); read.GetAwaiter().GetResult();
            Assert(Button(window, "StartSample").IsEnabled, "healthy quick snapshot is unusable");
            Assert(((TextBlock)window.FindName("ProfileStatus")).Text.Contains("阈值适用"), "fixture did not seed an applied profile claim");
            Volatile.Write(ref phase, 1); read = Read(); PumpUntil(() => read.IsCompleted, "invalid quick read stalled");
            Assert(!read.IsFaulted, "semantic quick snapshot escaped the status task: " + read.Exception?.GetBaseException().GetType().Name);
            read.GetAwaiter().GetResult();
            Assert(!Button(window, "StartSample").IsEnabled && !Button(window, "ConfirmThresholds").IsEnabled && !Button(window, "UseSuggestion").IsEnabled,
                "invalid quick snapshot remains actionable");
            Assert(((TextBlock)window.FindName("ProfileStatus")).Text.Contains("待确认") && ((TextBlock)window.FindName("SamplingStatus")).Text.Contains("待确认"),
                "invalid quick response retained an applied/capturing status claim");
            Volatile.Write(ref phase, 2); read = Read(); PumpUntil(() => read.IsCompleted, "quick recovery stalled"); read.GetAwaiter().GetResult();
            Assert(Button(window, "StartSample").IsEnabled && ((ListBox)window.FindName("SampleList")).Items.Count == 1, "valid quick snapshot did not recover the original sample");
            Assert(((TextBlock)window.FindName("ProfileStatus")).Text.Contains("阈值适用"), "valid recovery did not restore the profile claim");
            Checks.Add(scenario + ": actual calibration controller/private pipe rejects unsupported sample/suggestion shapes without fault and recovers read-only; no capture/save");
        }
        finally { window.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
