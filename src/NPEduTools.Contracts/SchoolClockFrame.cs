namespace NPEduTools.Contracts;

/// <summary>
/// School calendar values use offset zero as a comparison carrier ONLY, never as UTC instants.
/// Age is an accumulated monotonic duration, not a difference from the Windows clock.
/// </summary>
public sealed record SchoolClockFrame(Guid ConnectionId, Guid BridgeInstanceId, long Sequence, long Epoch,
    DateTimeOffset? SchoolNow, double AgeMs, string State, string Message, DaySchedule? Schedule = null)
{
    public const double MaxAgeMs = 3000;
    public static SchoolClockFrame Unavailable(string message) =>
        new(Guid.Empty, Guid.Empty, 0, 0, null, 0, "Unavailable", message);
}
