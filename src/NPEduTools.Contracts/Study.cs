namespace NPEduTools.Contracts;

// Policy transport only; never contains credentials, audio or camera bytes.
public sealed record StudyCalendarException(string Date, string Kind, int? Weekday);
public sealed record StudySchedule(string TimeZone, string EffectiveFrom, string? EffectiveTo,
    IReadOnlyList<SchoolNoiseRule> Rules, IReadOnlyList<StudyCalendarException> Exceptions);
public sealed record StudyPresentation(string NoiseDisplay, int TemporaryHideSeconds);
public sealed record StudyHomework(bool Enabled, string SourceMode, IReadOnlyList<string> SourceClassIds, int RotationSeconds);
public sealed record StudyEstimatedThresholds(double? HighDbA, double? MediumDbA);
public sealed record StudyNoise(bool Enabled, double? RedLineDbfs, double? MediumLineDbfs, int DisciplineCooldownSeconds, int HighContinuousSeconds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] StudyEstimatedThresholds? Estimated = null)
{
    [System.Text.Json.Serialization.JsonIgnore] public double? High => Estimated is { } e ? e.HighDbA : RedLineDbfs;
    [System.Text.Json.Serialization.JsonIgnore] public double? Medium => Estimated is { } e ? e.MediumDbA : MediumLineDbfs;
}
public sealed record StudyPhotos(bool Enabled, int ContinuousSeconds, int CooldownSeconds, int RetentionDays, bool CaptureOnExit, bool ExtendedRetentionAcknowledged);
public sealed record StudySettings(StudySchedule Schedule, StudyPresentation Presentation, StudyHomework Homework, StudyNoise Noise, StudyPhotos Photos);
public sealed record StudyHostState(string? Version, string PolicyState, string PresentationState, string CaptureState,
    string Phase, string Reason, bool ClockReady, DateTimeOffset? SchoolNow, SchoolNoiseWindow? Window,
    SchoolNoiseWindow? Next, StudySettings? Settings, StudyCaptureState? Execution = null, StudyDisplayState? Display = null,
    StudyHomeworkState? Homework = null, StudyThresholdState? Threshold = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] StudySeatingState? Seating = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ManualStudyState? Manual = null);
// Local statistics only. An offer is not a camera action or evidence of audio playback.
public sealed record StudyThresholdState(Guid SessionId, string Band, Guid? DisciplineId = null, string? ReminderError = null);
// Local Named Pipe only. Available means eligible to show, never a claim that WPF is visible.
public sealed record StudyDisplayState(Guid InstanceId, bool Selected, bool Available, double HiddenRemainingSeconds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? StageAudioError = null);
public sealed record StudyHideCommand(Guid InstanceId, string Version, SchoolNoiseWindow Window);
public sealed record StudyReminderCommand(Guid InstanceId, string Version, SchoolNoiseWindow Window, string Phase,
    Guid? DisciplineId = null, Guid? SessionId = null);
// Local UI observation, not permission to show or evidence of physical screen pixels.
public sealed record StudyPresentationPulse(Guid InstanceId, Guid AppInstanceId, long Sequence, string Version,
    SchoolNoiseWindow Window, string Phase, string State, int? RenderAgeMs);
// schoolNow is an offset-free school wall stamp, never deserialized as a Windows-local timestamp.
public sealed record StudyExecutionWindow(string Start, string End);
public sealed record StudyCaptureState(string Owner, string State, Guid? SessionId, string Reason,
    bool ClockReady, string? SchoolNow, StudyExecutionWindow? Window, bool SignalFresh);
public sealed record StudyHomeworkSource(string Id, string Name);
public sealed record StudyHomeworkItem(string Id, long Revision, string Subject, string Title, string Content, IReadOnlyList<StudyHomeworkSource> Sources);
public sealed record StudyHomeworkSnapshot(string Version, string Date, IReadOnlyList<StudyHomeworkItem> Items);
public sealed record StudyHomeworkState(string State, string? SnapshotId, int Chunks, string? Date, string? Version);
public sealed record StudyHomeworkRead(string SnapshotId, int Index);
public sealed record StudyHomeworkChunk(string SnapshotId, int Index, int Count, string Data);
