using NPEduTools.Contracts;

namespace NPEduTools.Host;

internal sealed record EdgeWebsiteScheduleRule(string Host, string Period)
{
    internal static bool ValidPeriod(string? period) => period is "ALWAYS" or "LESSON" or "BREAK" or "AFTER_SCHOOL" or
        "LESSON|BREAK" or "LESSON|AFTER_SCHOOL" or "BREAK|AFTER_SCHOOL" or "LESSON|BREAK|AFTER_SCHOOL";
}

internal sealed record EdgeWebsiteTiming(long Revision, EdgeWebsiteScheduleRule[] Rules, string UnavailableBehavior)
{
    internal bool Valid => Revision is >= 0 and <= 9007199254740991 && UnavailableBehavior is "BLOCK" or "ALLOW" &&
        Rules is {Length: <= 24} && Rules.All(rule => rule is not null && EdgeWebsitePolicy.ValidHost(rule.Host) &&
            EdgeWebsiteScheduleRule.ValidPeriod(rule.Period)) && Rules.Select(rule => rule.Host).Distinct(StringComparer.Ordinal).Count() == Rules.Length;

    internal bool HasSchedule => Rules.Any(rule => rule.Period != "ALWAYS");
    internal string[] ForPhase(string phase) => Rules.Where(rule => rule.Period == "ALWAYS" || rule.Period.Split('|').Contains(phase, StringComparer.Ordinal) ||
        phase == "UNAVAILABLE" && UnavailableBehavior == "BLOCK").Select(rule => rule.Host).ToArray();
}

internal readonly record struct WebsitePhaseObservation(string? Phase, bool ExplicitlyUnavailable = false);

internal static class WebsiteSchedulePhase
{
    internal static WebsitePhaseObservation Read(WatchSnapshot frame, double receiptAgeMs, DateTimeOffset hostNow)
    {
        if (frame.Outcome != "Succeeded" || frame.ConnectionId == Guid.Empty || frame.Status is not { } sample) return default;
        double duration = (sample.SampleCompletedAt - sample.SampleStartedAt).TotalMilliseconds;
        double deliveryAge = (hostNow - sample.SampleCompletedAt).TotalMilliseconds;
        if (!double.IsFinite(receiptAgeMs) || receiptAgeMs < 0 || duration < 0 || deliveryAge < 0 ||
            Math.Max(receiptAgeMs, deliveryAge) + duration >= 3000)
            return default;
        if (!sample.IsTimerRunning || !sample.IsClassPlanEnabled || !sample.IsClassPlanLoaded) return new(null, true);
        return new(sample.State switch
        {
            "OnClass" => "LESSON",
            "Breaking" => "BREAK",
            "AfterSchool" => "AFTER_SCHOOL",
            // ClassIsland also uses None in gaps outside a configured time slot.
            "None" or "PrepareOnClass" => "NO_ACTIVE_PERIOD",
            _ => null
        });
    }
}

internal sealed class WebsitePhaseContinuity
{
    private const int HoldMilliseconds = 12_000;
    private string? _lastVerified;
    private long? _unavailableSince;

    internal (string Phase, bool Held) Resolve(WebsitePhaseObservation state, long elapsed)
    {
        if (state.Phase is { } fresh)
        {
            _lastVerified = fresh;
            _unavailableSince = null;
            return (fresh, false);
        }
        if (state.ExplicitlyUnavailable) Reset();
        _unavailableSince ??= elapsed;
        if (_lastVerified is { } recent && elapsed - _unavailableSince < HoldMilliseconds) return (recent, true);
        _lastVerified = null;
        return ("UNAVAILABLE", false);
    }

    internal void Reset()
    {
        _lastVerified = null;
        _unavailableSince = null;
    }
}
