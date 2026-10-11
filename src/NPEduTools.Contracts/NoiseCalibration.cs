namespace NPEduTools.Contracts;

// Local pipe only. Scene labels describe an administrator's judgement, not sound recognition.
public sealed record NoiseCalibrationCommand(string Action, Guid InstanceId, long Revision,
    string? Secret = null, Guid? DraftId = null, string? Scene = null, string? Label = null,
    Guid? SampleId = null, double? MediumDbfs = null, double? HighDbfs = null);
public sealed record NoiseCalibrationSample(Guid Id, string Scene, string Label, double Seconds,
    double Coverage, double? P10Dbfs, double? MedianDbfs, double? P90Dbfs, double ClippedPercent, string Quality);
public sealed record NoiseCalibrationSuggestion(bool Ready, double? MediumDbfs, double? HighDbfs, string Message);
public sealed record NoiseCalibrationProfile(int Version, string DeviceId, string Signature, string Context,
    double MediumDbfs, double HighDbfs, DateTimeOffset ConfirmedAt, IReadOnlyList<NoiseCalibrationSample> Samples);
public sealed record NoiseCalibrationState(Guid InstanceId, long Revision, Guid? DraftId, string? DeviceId,
    string? Context, string State, double ProgressSeconds, IReadOnlyList<NoiseCalibrationSample> Samples,
    NoiseCalibrationSuggestion Suggestion, NoiseCalibrationProfile? Profile, bool Applied, string? Error, string? DeviceName = null);

public static class NoiseCalibrationContract
{
    public static readonly string[] Scenes = ["RelativeQuiet", "NormalStudy", "LowConversation", "SustainedConversation", "AdministratorVoice"];
    public static readonly string[] Labels = ["Acceptable", "Visual", "Discipline"];
    public static bool Thresholds(double? medium, double? high) => medium is { } m && high is { } h &&
        double.IsFinite(m) && double.IsFinite(h) && m is >= -120 and <= 0 && h is >= -120 and <= 0 && h - m >= 1;
    public static bool Valid(NoiseCalibrationCommand c) => c.InstanceId != Guid.Empty && c.Revision >= 0 &&
        NoiseManagementContract.SecretValid(c.Secret) &&
        (c.Action == "begin" ? c.DraftId is null : c.DraftId is { } id && id != Guid.Empty) &&
        (c.Action == "start" ? Scenes.Contains(c.Scene) && Labels.Contains(c.Label) : c.Scene is null) &&
        (c.Action == "label" ? Labels.Contains(c.Label) : c.Action == "start" || c.Label is null) &&
        (c.Action is "remove" or "label" ? c.SampleId is { } sample && sample != Guid.Empty : c.SampleId is null) &&
        (c.Action == "confirm" ? Thresholds(c.MediumDbfs, c.HighDbfs) : c.MediumDbfs is null && c.HighDbfs is null) &&
        c.Action is "begin" or "start" or "cancel" or "remove" or "label" or "confirm" or "clear";
}
