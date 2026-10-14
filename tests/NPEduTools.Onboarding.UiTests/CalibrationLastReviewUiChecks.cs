using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void RunCalibrationLastReviewChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (var check in new Action[] { () => ReferenceStatusFailureWithdrawsClaims(false),
                () => ReferenceStatusFailureWithdrawsClaims(true), NoiseDelayedOwnershipWithdrawsOldSignal })
                try { check(); } catch (Exception e) { failures.Add(e); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void ReferenceStatusFailureWithdrawsClaims(bool protocolFailure)
    {
        string pipe = "NPEduTools.Test.reference-stale-claims." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); int phase = 0;
        var profile = new NoiseReferenceProfile(1, "fixture", "UsbReferenceBeta", "fixture", "mic", "fixture", "DLY-2202", "Slow", 90,
            1, 1, DateTimeOffset.UtcNow, [], "synthetic-meter");
        var active = new NoiseReferenceState(Guid.NewGuid(), 1, "Sampling", "mic", "Synthetic", 10, 8, null,
            new(false, null, null, "fixture"), profile, true, null,
            Meter: new(new(60, "A", "Slow", false, false, 2, 20, 8, 8), .1, "isolated fresh reading"));
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            Assert(request.Capability is "noise.management.status" or "noise.reference.status", "reference recovery sent a mutation");
            if (request.Capability == "noise.management.status") return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "fixture"));
            int current = Volatile.Read(ref phase);
            return Task.FromResult(current == 1
                ? new HostResponse(protocolFailure ? 2 : 1, request.RequestId, "Rejected", "REFERENCE_UNAVAILABLE", "isolated failure")
                : new HostResponse(1, request.RequestId, "Succeeded", null, "fixture", NoiseReference: current == 0 ? active :
                    active with { State = "Ready", Revision = 2, Meter = null }));
        }, stop.Token));
        var window = new NoiseReferenceWindow(pipe, "synthetic-meter");
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        void Read()
        {
            var task = (Task)typeof(NoiseReferenceWindow).GetMethod("ReadAsync", flags)!.Invoke(window, null)!;
            PumpUntil(() => task.IsCompleted, "reference status did not finish"); task.GetAwaiter().GetResult();
        }
        try
        {
            Read(); Assert(Text("ProfileStatus").Contains("已联机校准 (Beta)") && Text("MeterStatus").Contains("60.0"), "reference fixture did not seed live facts");
            Volatile.Write(ref phase, 1); Read();
            Assert(Text("ProfileStatus").Contains("待确认") && Text("SamplingStatus").Contains("待确认") && Text("MeterStatus").Contains("待确认"),
                "failed reference query retained calibration, sampling or USB claims");
            Assert(!Button(window, "StartSample").IsEnabled && !Button(window, "ConfirmProfile").IsEnabled && !Button(window, "CancelSample").IsEnabled,
                "unconfirmed reference state still allows mutation");
            Volatile.Write(ref phase, 2); Read();
            Assert(Text("ProfileStatus").Contains("已联机校准 (Beta)") && !Text("MeterStatus").Contains("60.0") && Button(window, "ClearProfile").IsEnabled,
                "fresh reference state did not recover read-only");
            Checks.Add($"Reference stale claims: protocolFailure={protocolFailure}; actual WPF/private pipe withdraws profile/sampling/USB labels and recovers without any capture/save");
        }
        finally { window.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
    private static void NoiseDelayedOwnershipWithdrawsOldSignal()
    {
        string pipe = "NPEduTools.Test.noise-delayed-ownership." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource(); int manualReads = 0;
        var noise = new NoiseState(Guid.NewGuid(), 7, "Active", "isolated live signal", "mic", "Synthetic", DateTimeOffset.UtcNow, -57,
            "Good", new(30, 29.6, .99, -60, -30, 0, 100), [new(29, -57, "Good")], SessionId: Guid.NewGuid(), SelectedDeviceId: "mic");
        var server = Task.Run(() => WorkspaceProxy(pipe, async request =>
        {
            Assert(request.Noise is null && request.ManualStudy is null, "slow ownership recovery submitted a capture mutation");
            string capability = NoiseReadCapability(request);
            if (capability == "manual.study.status")
            {
                if (Interlocked.Increment(ref manualReads) == 1) await Task.Delay(3700, stop.Token);
                return new(1, request.RequestId, "Succeeded", null, "fixture", ManualStudy: ManualFixture(noise));
            }
            Assert(capability is "noise.devices" or "noise.status", "unexpected signal polling request");
            return new(1, request.RequestId, "Succeeded", null, "fixture", Noise: noise,
                NoiseDevices: capability == "noise.devices" ? [new("mic", "Synthetic")] : null,
                NoiseCalibrationStatus: new(noise.InstanceId, noise.Revision, "Ready", "Quick"));
        }, stop.Token, supportsCalibrationStatus: true));
        var window = new NoiseWindow(pipe);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        try
        {
            Exercise(window, w =>
            {
                PumpUntil(() => typeof(NoiseWindow).GetField("_manual", flags)!.GetValue(window) is not null ||
                    ((TextBlock)w.FindName("StateText")).Text.Contains("读取较慢"), "delayed ownership did not finish", 8);
                typeof(NoiseWindow).GetField("_busy", flags)!.SetValue(window, true);
                Assert(((TextBlock)w.FindName("LevelText")).Text == "—" && ((TextBlock)w.FindName("StateText")).Text.Contains("未知") &&
                    !((TextBlock)w.FindName("SummaryText")).Text.Contains("29.6") && ((Canvas)w.FindName("TrendCanvas")).Children.Count == 0,
                    "slow ownership query promoted old signal or statistics to live facts");
                Assert(((TextBlock)w.FindName("CalibrationStatus")).Text.Contains("待确认"), "old calibration remained applied");
                typeof(NoiseWindow).GetField("_busy", flags)!.SetValue(window, false);
                PumpUntil(() => ((TextBlock)w.FindName("LevelText")).Text == "-57.0" && ((TextBlock)w.FindName("CalibrationStatus")).Text == "已快速校准",
                    "fresh read-only polling did not restore signal/calibration");
                Checks.Add("Delayed ownership: actual NoiseWindow/private pipe ages the earlier signal across awaits, clears all old live facts, then recovers without capture replay");
                window.Shutdown();
            });
        }
        finally { window.Shutdown(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
