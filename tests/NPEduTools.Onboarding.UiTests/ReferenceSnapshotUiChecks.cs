using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NPEduTools.App;
using NPEduTools.Contracts;
using NPEduTools.Core;

internal static partial class Program
{
    private static void RunReferenceSnapshotChecks()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Application.Current.Dispatcher));
        try
        {
            var failures = new List<Exception>();
            foreach (bool usb in new[] { false, true })
            foreach (string scenario in new[] { "null-evaluation", "null-samples", "null-sample", "missing-offset", "missing-check-error" })
                try { ReferenceSnapshotCheck(usb, scenario); }
                catch (Exception error) { failures.Add(new InvalidOperationException(usb + "/" + scenario + ": " + error.Message, error)); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void ReferenceSnapshotCheck(bool usb, string scenario)
    {
        string pipe = "NPEduTools.Test.reference-snapshot." + Guid.NewGuid().ToString("N");
        string? path = usb ? "synthetic-meter" : null;
        var at = DateTimeOffset.UtcNow;
        var sample = new NoiseReferenceSample(Guid.NewGuid(), "Primary", 10, 1, -40, -40.5, -39.5, 0, "Good",
            at.AddSeconds(-30), at.AddSeconds(-20), 50, 49.5, 50.5, true,
            usb ? new(10, 9.9, 1, .1, .05, .05, 0) : null);
        var check = sample with { Id = Guid.NewGuid(), Role = "Check", DigitalDbfs = -30, MinimumDbfs = -30.5,
            MaximumDbfs = -29.5, ReferenceDbA = 60, ReferenceMinimumDbA = 59.5, ReferenceMaximumDbA = 60.5,
            WindowStartedAt = at.AddSeconds(-10), WindowEndedAt = at };
        var draft = new NoiseReferenceDraft(Guid.NewGuid(), "synthetic-mic", "fixture meter", "Slow", 2, 2,
            "fixture signature", [sample, check], path);
        var profile = new NoiseReferenceProfile(1, ReferenceNoiseMeter.AlgorithmVersion,
            usb ? "UsbReferenceBeta" : "ManualReference", NoiseReferenceCalibration.Statistic, draft.DeviceId,
            draft.Signature!, draft.Instrument, draft.TimeWeighting, 90, 2, 2, at, draft.Samples, path);
        var valid = new NoiseReferenceState(Guid.NewGuid(), 1, "Ready", draft.DeviceId, "Synthetic", 0, 0,
            draft, NoiseReferenceCalibration.Evaluate(draft), profile, true, null);
        Assert(valid.Evaluation.Ready, "valid fixture does not meet the existing reference contract");
        using var stop = new CancellationTokenSource();
        int phase = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            Assert(request.Capability is "noise.management.status" or "noise.reference.status", "snapshot recovery sampled or saved");
            var state = Volatile.Read(ref phase) != 1 ? valid : scenario switch
            {
                "null-evaluation" => valid with { Evaluation = null! },
                "null-samples" => valid with { Draft = draft with { Samples = null! } },
                "null-sample" => valid with { Draft = draft with { Samples = [null!] } },
                "missing-offset" => valid with { Evaluation = valid.Evaluation with { OffsetDb = null } },
                _ => valid with { Evaluation = valid.Evaluation with { CheckErrorDb = null } }
            };
            if (Volatile.Read(ref phase) == 1) state = state with { Revision = 2 };
            return Task.FromResult(new HostResponse(1, request.RequestId, "Succeeded", null, "synthetic reference snapshot",
                NoiseReference: request.Capability == "noise.reference.status" ? state : null));
        }, stop.Token));
        var window = new NoiseReferenceWindow(pipe, path);
        try
        {
            Task Read() => (Task)typeof(NoiseReferenceWindow).GetMethod("ReadAsync", WorkspaceFlags)!.Invoke(window, null)!;
            void Complete(Task read)
            { PumpUntil(() => read.IsCompleted, "reference snapshot read stalled"); read.GetAwaiter().GetResult(); }
            Complete(Read());
            Assert(Button(window, "StartSample").IsEnabled && Button(window, "ConfirmProfile").IsEnabled,
                "healthy reference snapshot is not actionable");
            Assert(((TextBlock)window.FindName("ReferenceHealth")).Text.Contains("本次输入已核对"), "fixture did not seed a matched profile");
            Volatile.Write(ref phase, 1);
            var failed = Read();
            PumpUntil(() => failed.IsCompleted, "invalid reference snapshot read stalled");
            Assert(!failed.IsFaulted, "reference snapshot escaped the status task: " + failed.Exception?.GetBaseException().GetType().Name);
            failed.GetAwaiter().GetResult();
            Assert(!Button(window, "StartSample").IsEnabled && !Button(window, "ConfirmProfile").IsEnabled &&
                Button(window, "RefreshStatus").IsEnabled, "invalid reference snapshot remains actionable or blocks refresh");
            Assert(((TextBlock)window.FindName("ProfileStatus")).Text.Contains("待确认") &&
                ((TextBlock)window.FindName("SamplingStatus")).Text.Contains("待确认") &&
                ((TextBlock)window.FindName("MeterStatus")).Text.Contains("待确认"), "invalid snapshot retained fresh profile/sample/meter claims");
            var cached = (NoiseReferenceState)typeof(NoiseReferenceWindow).GetField("_state", WorkspaceFlags)!.GetValue(window)!;
            Assert(cached.Revision == 1 && cached.Draft?.Id == draft.Id, "malformed snapshot replaced the last valid draft context");
            // Same revision as before the invalid reply: recovery cannot require a new draft or sample.
            Volatile.Write(ref phase, 2);
            Complete(Read());
            Assert(Button(window, "StartSample").IsEnabled && Button(window, "ConfirmProfile").IsEnabled &&
                ((ListBox)window.FindName("SampleList")).Items.Count == 2, "valid snapshot did not recover the original samples");
            Assert(((TextBlock)window.FindName("ReferenceHealth")).Text.Contains("本次输入已核对"), "valid recovery did not restore profile status");
            Checks.Add((usb ? "USB" : "Manual reference") + "/" + scenario +
                ": invalid structured reply never escapes or replaces valid context; actions and fresh claims withdrawn; same-revision read-only recovery preserves samples, no sensors/save");
        }
        finally { window.Close(); stop.Cancel(); try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { } }
    }
}
