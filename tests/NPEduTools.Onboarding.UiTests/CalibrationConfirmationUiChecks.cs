using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using NPEduTools.App;
using NPEduTools.Contracts;

internal static partial class Program
{
    private static void CheckCalibrationBeginConfirmation(bool reference, bool usb, string scenario)
    {
        string pipe = "NPEduTools.Test.calibration-confirmation." + Guid.NewGuid().ToString("N");
        string? meterPath = usb ? "synthetic-meter" : null;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var instance = Guid.NewGuid(); var draft = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        var sample = new NoiseCalibrationSample(Guid.NewGuid(), "RelativeQuiet", "Acceptable", 15, 1, -55, -50, -45, 0, "Good");
        var referenceSample = new NoiseReferenceSample(Guid.NewGuid(), "Primary", 10, 1, -50, -51, -49, 0, "Good", at, at.AddSeconds(10));
        var quickState = new NoiseCalibrationState(instance, 10, draft, "synthetic-mic", "fixture-context", "Ready", 0, [sample],
            new(false, null, null, "synthetic suggestion"), null, false, null);
        var referenceState = new NoiseReferenceState(instance, 10, "Ready", "synthetic-mic", "Synthetic input", 0, 0,
            new(draft, "synthetic-mic", "Synthetic reference", "Slow", 2, 2, null, [referenceSample], meterPath),
            new(false, null, null, "synthetic evaluation"), null, false, null);
        int commands = 0, accepted = 0;
        var server = Task.Run(() => WorkspaceProxy(pipe, request =>
        {
            commands++;
            bool matches = Protocol.Validate(request) is null && (reference
                ? request.NoiseReference is { Action: "begin" } r && r.InstanceId == referenceState.InstanceId && r.Revision == referenceState.Revision
                : request.NoiseCalibration is { Action: "begin" } q && q.InstanceId == quickState.InstanceId && q.Revision == quickState.Revision);
            if (matches)
            {
                accepted++;
                quickState = quickState with { Revision = quickState.Revision + 1, DraftId = Guid.NewGuid(), Samples = [] };
                referenceState = referenceState with { Revision = referenceState.Revision + 1,
                    Draft = referenceState.Draft! with { Id = Guid.NewGuid(), Samples = [] } };
            }
            return Task.FromResult(new HostResponse(Protocol.Version, request.RequestId, matches ? "Succeeded" : "Rejected",
                matches ? null : reference ? "REFERENCE_CHANGED" : "CALIBRATION_CHANGED",
                matches ? "synthetic draft created" : "草稿已变化，请重新确认。",
                NoiseCalibration: reference ? null : quickState, NoiseReference: reference ? referenceState : null));
        }, stop.Token));
        Window window = reference ? new NoiseReferenceWindow(pipe, meterPath) : new NoiseCalibrationWindow(pipe);
        var type = window.GetType();
        void Render() => type.GetMethod("Render", flags | BindingFlags.DeclaredOnly)!.Invoke(window, [reference ? (object)referenceState : quickState]);
        try
        {
            Render(); type.GetField("_connected", flags)!.SetValue(window, true);
            ((PasswordBox)window.FindName("ManagementSecret")).Password = "synthetic-admin";
            int confirmations = 0;
            bool Confirm()
            {
                confirmations++;
                if (scenario != "unchanged")
                {
                    Guid nextInstance = scenario == "instance" ? Guid.NewGuid() : instance;
                    long nextRevision = scenario == "instance" ? 10 : 11;
                    quickState = quickState with { InstanceId = nextInstance, Revision = nextRevision, DraftId = Guid.NewGuid() };
                    referenceState = referenceState with { InstanceId = nextInstance, Revision = nextRevision,
                        Draft = referenceState.Draft! with { Id = Guid.NewGuid() } };
                    // A modal dialog dispatches polls; model the updated Host state being rendered before consent returns.
                    Render();
                }
                return scenario != "cancelled";
            }
            var action = (Task)type.GetMethod("BeginDraftAsync", flags)!.Invoke(window, [(Func<bool>)Confirm])!;
            PumpUntil(() => action.IsCompleted, "calibration draft confirmation did not settle");
            action.GetAwaiter().GetResult();
            Assert(confirmations == 1, "replacement confirmation was skipped or repeated");
            Assert(commands == (scenario == "cancelled" ? 0 : 1) && accepted == (scenario == "unchanged" ? 1 : 0),
                "confirmation replaced a newer calibration draft, followed a restarted Host, or replayed the operation");
            Assert((reference ? referenceState.Draft!.Samples.Count : quickState.Samples.Count) == (scenario == "unchanged" ? 0 : 1),
                "a stale or cancelled confirmation discarded the replacement draft's samples");
            if (scenario is "revision" or "instance")
                Assert(((TextBlock)window.FindName("OperationMessage")).Text.Contains("重新确认"), "conflicting draft confirmation was hidden");
            Checks.Add($"{(usb ? "USB reference" : reference ? "Manual reference" : "Quick")} draft {scenario}: confirmation preserves its original Host/revision, cancellation sends nothing, valid replacement still works; private pipe, no devices");
        }
        finally
        {
            window.Close(); stop.Cancel();
            try { server.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
