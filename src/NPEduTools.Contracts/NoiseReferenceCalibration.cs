namespace NPEduTools.Contracts;

// Local reference workflow. Formal sampling validates the exact input signature before applying estimates.
public sealed record NoiseReferenceCommand(string Action, Guid InstanceId, long Revision, string? Secret = null,
    Guid? DraftId = null, string? Instrument = null, string? TimeWeighting = null,
    double? StabilityDb = null, double? CheckToleranceDb = null, string? Role = null, Guid? SampleId = null,
    double? ReferenceDbA = null, double? ReferenceMinimumDbA = null, double? ReferenceMaximumDbA = null,
    bool SameWindowConfirmed = false);
public sealed record NoiseReferenceSample(Guid Id, string Role, double Seconds, double Coverage,
    double? DigitalDbfs, double? MinimumDbfs, double? MaximumDbfs, double ClippedPercent, string Quality,
    DateTimeOffset WindowStartedAt, DateTimeOffset WindowEndedAt, double? ReferenceDbA = null,
    double? ReferenceMinimumDbA = null, double? ReferenceMaximumDbA = null, bool SameWindowConfirmed = false);
public sealed record NoiseReferenceDraft(Guid Id, string DeviceId, string Instrument, string TimeWeighting,
    double StabilityDb, double CheckToleranceDb, string? Signature, IReadOnlyList<NoiseReferenceSample> Samples);
public sealed record NoiseReferenceEvaluation(bool Ready, double? OffsetDb, double? CheckErrorDb, string Message);
public sealed record NoiseReferenceProfile(int Version, string Algorithm, string Method, string Statistic,
    string DeviceId, string Signature, string Instrument, string TimeWeighting, double OffsetDb,
    double StabilityDb, double CheckToleranceDb, DateTimeOffset ConfirmedAt, IReadOnlyList<NoiseReferenceSample> Samples);
public sealed record NoiseReferenceState(Guid InstanceId, long Revision, string State, string? DeviceId,
    string? DeviceName, double ProgressSeconds, double MeasuredSeconds, NoiseReferenceDraft? Draft,
    NoiseReferenceEvaluation Evaluation, NoiseReferenceProfile? Profile, bool FreshSignatureMatched, string? Error,
    string? CaptureMessage = null, string? InvalidationReason = null);

public static class NoiseReferenceContract
{
    public static bool Limit(double? value) => value is { } v && double.IsFinite(v) && v is >= 0.1 and <= 5;
    public static bool Reading(double? value) => value is { } v && double.IsFinite(v) && v is >= 30 and <= 130;
    public static bool InstrumentValid(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl);
    public static bool ReferenceRange(double? value, double? minimum, double? maximum) => Reading(value) &&
        (minimum is null && maximum is null || Reading(minimum) && Reading(maximum) && minimum <= value && value <= maximum);
    public static bool Valid(NoiseReferenceCommand c) => c.InstanceId != Guid.Empty && c.Revision >= 0 &&
        NoiseManagementContract.SecretValid(c.Secret) &&
        (c.Action is "begin" or "clear" ? c.DraftId is null : c.DraftId is { } draft && draft != Guid.Empty) &&
        (c.Action == "begin" ? InstrumentValid(c.Instrument) && c.TimeWeighting is "Fast" or "Slow" &&
            Limit(c.StabilityDb) && Limit(c.CheckToleranceDb) : c.Instrument is null && c.TimeWeighting is null &&
            c.StabilityDb is null && c.CheckToleranceDb is null) &&
        (c.Action == "start" ? c.Role is "Primary" or "Check" : c.Role is null) &&
        (c.Action is "reference" or "remove" ? c.SampleId is { } id && id != Guid.Empty : c.SampleId is null) &&
        (c.Action == "reference" ? c.SameWindowConfirmed && ReferenceRange(c.ReferenceDbA, c.ReferenceMinimumDbA, c.ReferenceMaximumDbA)
            : !c.SameWindowConfirmed && c.ReferenceDbA is null && c.ReferenceMinimumDbA is null && c.ReferenceMaximumDbA is null) &&
        c.Action is "begin" or "start" or "cancel" or "reference" or "remove" or "confirm" or "clear";
}
