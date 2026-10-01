using NPEduTools.Contracts;

namespace NPEduTools.Core;

public sealed record NoiseScheduleInput(EffectiveNoiseSchedule Policy, SchoolClockReading Clock,
    bool Eligible, bool LeaseValid, bool MicrophoneConfigured, bool ExamBlocked, string Owner,
    bool CaptureBusy, string MicrophoneKey, IReadOnlyList<NoiseWindowBlock> Blocks);

/// <summary>Decisions only. A later Host adapter owns effects, acknowledgements and persistence.</summary>
public static class NoiseScheduleEvaluator
{
    public static NoiseScheduleDecision Evaluate(NoiseScheduleInput input)
    {
        NoiseScheduleDecision Block(string reason, SchoolNoiseWindow? window = null) =>
            new(input.Owner == "Schedule" ? "Stop" : "Wait", reason, window);
        if (input.Owner is not ("None" or "Manual" or "Schedule")) throw new ArgumentException("INVALID_OWNER");
        // Schedule controls may never stop or adopt a manually owned capture.
        if (input.Owner == "Manual") return new("Wait", "MANUAL_ACTIVE", null);
        if (!input.Eligible) return Block("NOT_ELIGIBLE");
        if (!input.LeaseValid) return Block("POLICY_EXPIRED");
        if (input.Policy.Source is "None" or "Disabled") return Block("DISABLED");
        if (input.ExamBlocked) return Block("EXAM_PAUSED");
        if (!input.Clock.Fresh || !input.Clock.CanStart || input.Clock.Now is null || input.Clock.DateNeedsReview)
            return Block("SCHOOL_CLOCK_UNAVAILABLE");
        var window = SchoolNoiseSchedule.Windows(input.Policy, input.Clock.Now.Value).Current;
        if (window is null) return Block("OUTSIDE_WINDOW");
        if (input.Blocks.Any(b => b.Kind == "Skipped" && SchoolNoiseSchedule.Overlaps(b.Window, window)))
            return Block("WINDOW_SKIPPED", window);
        if (input.Blocks.Any(b => b.Kind == "Failed" && b.MicrophoneKey == input.MicrophoneKey && SchoolNoiseSchedule.Overlaps(b.Window, window)))
            return Block("WINDOW_FAILED", window);
        if (!input.MicrophoneConfigured) return Block("MICROPHONE_NOT_CONFIGURED", window);
        if (input.CaptureBusy) return new("Wait", "CAPTURE_BUSY", window);
        return new(input.Owner == "Schedule" ? "Keep" : "Start", "WINDOW_ACTIVE", window);
    }
}

/// <summary>Host-scoped monotonic lease; a restored file cannot renew it after a Host restart.</summary>
public sealed record NoiseScheduleLease(Guid HostId, string Scope, long Revision, double ConfirmedAtMs, double LifetimeMs)
{
    public const double MaxLifetimeMs = 24 * 60 * 60 * 1000;
    public bool Valid(Guid hostId, string scope, long revision, double elapsedMs) => HostId != Guid.Empty && HostId == hostId &&
        !string.IsNullOrWhiteSpace(Scope) && Scope == scope && Revision >= 0 && Revision <= 9007199254740991 && Revision == revision &&
        double.IsFinite(ConfirmedAtMs) && ConfirmedAtMs >= 0 && double.IsFinite(LifetimeMs) && LifetimeMs > 0 && LifetimeMs <= MaxLifetimeMs &&
        double.IsFinite(elapsedMs) && elapsedMs >= ConfirmedAtMs && elapsedMs - ConfirmedAtMs < LifetimeMs;
}
