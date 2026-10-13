namespace NPEduTools.Contracts;

// Local reference workflow. Formal sampling validates the exact input signature before applying estimates.
public sealed record NoiseReferenceCommand(string Action, Guid InstanceId, long Revision, string? Secret = null,
    Guid? DraftId = null, string? Instrument = null, string? TimeWeighting = null,
    double? StabilityDb = null, double? CheckToleranceDb = null, string? Role = null, Guid? SampleId = null,
    double? ReferenceDbA = null, double? ReferenceMinimumDbA = null, double? ReferenceMaximumDbA = null,
    bool SameWindowConfirmed = false, string? MeterDevicePath = null, string? Workflow = null, bool WarningAcknowledged = false);
public sealed record NoiseReferenceSample(Guid Id, string Role, double Seconds, double Coverage,
    double? DigitalDbfs, double? MinimumDbfs, double? MaximumDbfs, double ClippedPercent, string Quality,
    DateTimeOffset WindowStartedAt, DateTimeOffset WindowEndedAt, double? ReferenceDbA = null,
    double? ReferenceMinimumDbA = null, double? ReferenceMaximumDbA = null, bool SameWindowConfirmed = false,
    NoiseReferenceMeterEvidence? MeterEvidence = null);
// Host request/receipt brackets, not an instrument clock or a claim of hardware synchronisation.
public sealed record NoiseReferenceMeterEvidence(int Readings, double SpanSeconds, double MaximumGapSeconds,
    double MaximumRoundTripSeconds, double StartSkewSeconds, double EndSkewSeconds, int Range,
    string Timing = "host-bracketed-beta-v1");
public sealed record NoiseReferenceMeterState(SoundMeterSample? Reading, double? AgeSeconds, string Message);
public sealed record NoiseReferenceDraft(Guid Id, string DeviceId, string Instrument, string TimeWeighting,
    double StabilityDb, double CheckToleranceDb, string? Signature, IReadOnlyList<NoiseReferenceSample> Samples,
    string? MeterDevicePath = null, string Workflow = "Stable");
public sealed record NoiseReferenceAssessment(bool WarningRequired, string Scope, double ReferenceMinimumDbA, double ReferenceMaximumDbA);
public sealed record NoiseReferenceEvaluation(bool Ready, double? OffsetDb, double? CheckErrorDb, string Message,
    NoiseReferenceAssessment? Assessment = null);
public sealed record NoiseReferenceProfile(int Version, string Algorithm, string Method, string Statistic,
    string DeviceId, string Signature, string Instrument, string TimeWeighting, double OffsetDb,
    double StabilityDb, double CheckToleranceDb, DateTimeOffset ConfirmedAt, IReadOnlyList<NoiseReferenceSample> Samples,
    string? MeterDevicePath = null, string Workflow = "Stable", bool WarningAcknowledged = false);
public sealed record NoiseReferenceState(Guid InstanceId, long Revision, string State, string? DeviceId,
    string? DeviceName, double ProgressSeconds, double MeasuredSeconds, NoiseReferenceDraft? Draft,
    NoiseReferenceEvaluation Evaluation, NoiseReferenceProfile? Profile, bool FreshSignatureMatched, string? Error,
    string? CaptureMessage = null, string? InvalidationReason = null, NoiseReferenceMeterState? Meter = null);

public static class NoiseReferenceContract
{
    public const int UsbPollMilliseconds = 1000; // SoundLab's default ~995 ms; no unverified high-rate polling.
    public const double UsbMaximumGapSeconds = 2;
    public const double UsbMaximumRoundTripSeconds = .5;
    public const int UsbReadTimeoutMilliseconds = 750;
    public const double UsbMaximumBoundarySkewSeconds = 1.25;
    public const double UsbMinimumSpanSeconds = 8;
    public static bool Limit(double? value) => value is { } v && double.IsFinite(v) && v is >= 0.1 and <= 5;
    public static bool WorkflowValid(string? workflow, string? meter) => workflow is null or "Stable" || workflow == "Field" && MeterPathValid(meter);
    public static bool StabilityLimit(double? value, string workflow) => workflow == "Stable" ? Limit(value) :
        workflow == "Field" && value is { } v && double.IsFinite(v) && v is >= .1 and <= 20;
    public static bool CheckLimit(double? value, string workflow) => workflow == "Stable" ? Limit(value) :
        workflow == "Field" && value is { } v && double.IsFinite(v) && v is >= .1 and <= 3;
    public static bool Reading(double? value) => value is { } v && double.IsFinite(v) && v is >= 30 and <= 130;
    public static bool InstrumentValid(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && !name.Any(char.IsControl);
    public static bool MeterPathValid(string? path) => path is { Length: > 0 and <= 4096 } &&
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl);
    public static bool MeterEvidenceValid(NoiseReferenceMeterEvidence? e, string workflow = "Stable") => e is not null &&
        workflow is "Stable" or "Field" && e.Timing == "host-bracketed-beta-v1" && e.Readings >= (workflow == "Field" ? 28 : 8) && e.Readings <= 128 &&
        double.IsFinite(e.SpanSeconds) && e.SpanSeconds >= (workflow == "Field" ? 28 : UsbMinimumSpanSeconds) && e.SpanSeconds <= (workflow == "Field" ? 30.01 : 10.01) &&
        double.IsFinite(e.MaximumGapSeconds) && e.MaximumGapSeconds is > 0 and <= UsbMaximumGapSeconds &&
        double.IsFinite(e.MaximumRoundTripSeconds) && e.MaximumRoundTripSeconds is >= 0 and <= UsbMaximumRoundTripSeconds &&
        double.IsFinite(e.StartSkewSeconds) && e.StartSkewSeconds is >= 0 and <= UsbMaximumBoundarySkewSeconds &&
        double.IsFinite(e.EndSkewSeconds) && e.EndSkewSeconds is >= 0 and <= UsbMaximumBoundarySkewSeconds && e.Range is >= 0 and <= 4;
    public static bool ReferenceRange(double? value, double? minimum, double? maximum) => Reading(value) &&
        (minimum is null && maximum is null || Reading(minimum) && Reading(maximum) && minimum <= value && value <= maximum);
    public static bool Valid(NoiseReferenceCommand c) => c.InstanceId != Guid.Empty && c.Revision >= 0 &&
        NoiseManagementContract.SecretValid(c.Secret) &&
        (c.Action == "begin" ? c.MeterDevicePath is null || MeterPathValid(c.MeterDevicePath) : c.MeterDevicePath is null) &&
        (c.Action is "begin" or "clear" ? c.DraftId is null : c.DraftId is { } draft && draft != Guid.Empty) &&
        (c.Action == "begin" ? InstrumentValid(c.Instrument) && c.TimeWeighting is "Fast" or "Slow" &&
            WorkflowValid(c.Workflow, c.MeterDevicePath) && StabilityLimit(c.StabilityDb,c.Workflow??"Stable") && CheckLimit(c.CheckToleranceDb,c.Workflow??"Stable") : c.Instrument is null && c.TimeWeighting is null &&
            c.StabilityDb is null && c.CheckToleranceDb is null && c.Workflow is null) &&
        (!c.WarningAcknowledged || c.Action == "confirm") &&
        (c.Action == "start" ? c.Role is "Primary" or "Check" : c.Role is null) &&
        (c.Action is "reference" or "remove" ? c.SampleId is { } id && id != Guid.Empty : c.SampleId is null) &&
        (c.Action == "reference" ? c.SameWindowConfirmed && ReferenceRange(c.ReferenceDbA, c.ReferenceMinimumDbA, c.ReferenceMaximumDbA)
            : !c.SameWindowConfirmed && c.ReferenceDbA is null && c.ReferenceMinimumDbA is null && c.ReferenceMaximumDbA is null) &&
        c.Action is "begin" or "start" or "cancel" or "reference" or "remove" or "confirm" or "clear";
}
