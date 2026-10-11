namespace NPEduTools.Contracts;

// Local, opt-in IPC. No photographs, verification records, audio, score or ranking.
public sealed record StudyReportPoint(double Seconds, double SampledSeconds, double? Mean, double? Maximum);
public sealed record StudyReportChannel(string Unit, double SampledSeconds, double? EnergyMean, double? Maximum,
    double ThresholdSampledSeconds, double MediumSeconds, double HighSeconds, IReadOnlyList<StudyReportPoint> Trend,
    double MediumThresholdSampledSeconds = 0);
public sealed record StudySessionReport(Guid SessionId, bool Complete, double ElapsedSeconds, double ClippedSeconds,
    long Frames, int DisciplineReminders, StudyReportChannel Raw, StudyReportChannel? Estimated, string? EstimateReason);
public sealed record StudyReportData(SchoolNoiseWindow Window, double ExpectedSeconds, double SampledSeconds, double Coverage,
    double ClippedPercent, long Frames, int DisciplineReminders, StudyReportChannel Raw, StudyReportChannel? Estimated,
    bool Complete, IReadOnlyList<string> Notes, string Mode = "Daily", string ClockSource = "ClassIsland");
public sealed record StudyReportState(Guid InstanceId, string? Version, bool Available, string State,
    double RemainingSeconds, StudyReportData? Data);
