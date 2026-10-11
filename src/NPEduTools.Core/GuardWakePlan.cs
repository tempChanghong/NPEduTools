namespace NPEduTools.Core;

/// <summary>UTC launch hints projected from a recently confirmed school clock.
/// These never constitute permission to open a sensor or to resume a manual session.</summary>
public sealed record GuardWakeWindow(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
public sealed record GuardWakePlan(Guid Generation, GuardProcess Host, DateTimeOffset IssuedUtc,
    IReadOnlyList<GuardWakeWindow> Windows, DateTimeOffset? CurrentEndUtc = null,string? ContextKey=null)
{
    public bool Valid() => (ContextKey is null || ContextKey.Length==64 && ContextKey.All(Uri.IsHexDigit)) && Generation != Guid.Empty && Host is { Id: > 0, StartedUtcTicks: > 0 } &&
        IssuedUtc.Offset == TimeSpan.Zero && IssuedUtc.Year is >= 2000 and <= 9997 && Windows is { Count: <= 16 } &&
        Windows.All(w => w is not null && w.StartUtc.Offset == TimeSpan.Zero && w.EndUtc.Offset == TimeSpan.Zero &&
            w.EndUtc > w.StartUtc && w.EndUtc - w.StartUtc <= TimeSpan.FromHours(3) &&
            w.StartUtc >= IssuedUtc.AddHours(-3) && w.EndUtc <= IssuedUtc.AddDays(400)) &&
        (CurrentEndUtc is null || CurrentEndUtc.Value.Offset == TimeSpan.Zero && CurrentEndUtc <= IssuedUtc.AddHours(3).AddMinutes(2));
    public GuardWakeWindow? Due(DateTimeOffset utc) => IssuedUtc <= utc.AddSeconds(5) ? Windows.FirstOrDefault(w => w.StartUtc.AddMinutes(-2) <= utc && utc < w.EndUtc) : null;
}
public sealed record GuardExpectedExit(Guid Generation, GuardProcess Host, DateTimeOffset? SkipUntilUtc);
