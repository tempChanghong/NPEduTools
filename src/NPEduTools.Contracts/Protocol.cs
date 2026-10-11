using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduTools.Contracts;

public sealed record HostRequest(
    int Version,
    Guid RequestId,
    string Capability,
    int TimeoutMs = 3000,
    int ObserveMs = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutablePath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OperationId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? SchoolDate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecorderCommand? Recording = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AutomaticRecordingCommand? Automatic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AutoStartEnabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassroomModeCommand? ClassroomMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepCommand? Npep = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepNotificationCommand? Notification = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemoteExamCommand? RemoteExam = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExamAwarePlanInput? ExamPlan = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseCommand? Noise = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SecRandomCommand? SecRandom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseManagementCommand? NoiseManagement = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? NoiseAuthorization = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseDisplayCommand? NoiseDisplay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyHideCommand? StudyHide = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyReminderCommand? StudyReminder = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyHomeworkRead? StudyHomework = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyPresentationPulse? StudyPresentation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudySeatingRead? StudySeating = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudySeatingPulse? SeatingPresentation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyCameraCommand? StudyCamera = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseCalibrationCommand? NoiseCalibration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseReferenceCommand? NoiseReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SoundMeterCommand? SoundMeter = null);

public sealed record LessonStatusDto(
    DateTimeOffset SampleStartedAt,
    DateTimeOffset SampleCompletedAt,
    string State,
    string? Subject,
    bool IsTimerRunning,
    bool IsClassPlanLoaded,
    bool IsClassPlanEnabled,
    int CurrentSelectedIndex,
    IReadOnlyDictionary<string, long> ObservedEvents);

public sealed record HostResponse(
    int Version,
    Guid RequestId,
    string Outcome,
    string? ErrorCode,
    string Message,
    LessonStatusDto? Status = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LaunchData? Launch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TouchAssistState? TouchAssist = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DaySchedule? Schedule = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SchoolClockFrame? SchoolClock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DayForecast? Forecast = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecordingState? Recording = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AutomaticRecordingState? Automatic = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExamAwareStatus? ExamAware = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExamAwarePairing? ExamAwarePairing = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClassroomModeState? ClassroomMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepState? Npep = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepInboxState? Inbox = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RemoteExamStatus? RemoteExam = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseState? Noise = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<NoiseDevice>? NoiseDevices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SecRandomState? SecRandom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseProtectionState? NoiseProtection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseDisplayState? NoiseDisplay = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyHostState? Study = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyHomeworkChunk? HomeworkChunk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudySeatingChunk? SeatingChunk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudyDeviceState? StudyDevices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseCalibrationState? NoiseCalibration = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoiseReferenceState? NoiseReference = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SoundMeterState? SoundMeter = null);

public sealed record TouchAssistState(bool Running, bool Paused, bool AllowUnmarkedMouse, string State, string? Error = null);

/// <summary>Length-prefixed UTF-8 JSON. watch returns a stream of full WatchSnapshot frames.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 64 * 1024;
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken token)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds size limit.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid frame length.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidDataException("Empty message.");
    }

    public static string? Validate(HostRequest request)
    {
        if (request.Version != Version) return "ProtocolVersionMismatch";
        if (request.Capability == "noise.meter.command" ? request.SoundMeter is null ||
            !SoundMeterContract.Valid(request.SoundMeter) : request.SoundMeter is not null) return "InvalidSoundMeterCommand";
        if (request.Capability == "noise.reference.command" ? request.NoiseReference is null ||
            !NoiseReferenceContract.Valid(request.NoiseReference) : request.NoiseReference is not null)
            return "InvalidNoiseReferenceCommand";
        if (request.Capability == "noise.calibration.command" ? request.NoiseCalibration is null ||
            !NoiseCalibrationContract.Valid(request.NoiseCalibration) : request.NoiseCalibration is not null)
            return "InvalidNoiseCalibrationCommand";
        if (request.Capability == "study.devices.select" ? request.StudyCamera is not { } camera || !StudyDeviceContract.Valid(camera) : request.StudyCamera is not null)
            return "InvalidStudyCameraCommand";
        if (request.Capability == "study.presentation" ? request.StudyPresentation is not { } pulse ||
            !ValidStudyDisplayIdentity(pulse.InstanceId, pulse.Version, pulse.Window) || pulse.AppInstanceId == Guid.Empty ||
            pulse.Sequence is <= 0 or > 9007199254740991 || pulse.Phase is not ("Preparing" or "Active" or "Ended") ||
            pulse.State is not ("Visible" or "Hidden" or "Stale") ||
            (pulse.State == "Visible" ? pulse.RenderAgeMs is not (>= 0 and < 1000) : pulse.RenderAgeMs is not null)
            : request.StudyPresentation is not null) return "InvalidStudyPresentation";
        if (request.Capability == "study.seating" ? request.StudySeating is not { Version.Length: 64, Hash.Length: 64, Revision: > 0, Index: >= 0 and < 128 } seat ||
            !seat.Hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || !seat.Version.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') : request.StudySeating is not null)
            return "InvalidStudySeatingRead";
        if (request.Capability == "study.seating.presentation" ? request.SeatingPresentation is not { InstanceId: var si, AppInstanceId: var ai, Sequence: > 0, Version.Length: 64, Revision: >= 0 } sp ||
            si == Guid.Empty || ai == Guid.Empty || !sp.Version.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') || sp.Sequence > 9007199254740991 || sp.AppliedHash is not null && (sp.AppliedHash.Length != 64 || !sp.AppliedHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) ||
            sp.RenderAgeMs is < 0 or > 999 || sp.Visible && (sp.AppliedHash is null || sp.RenderAgeMs is null) : request.SeatingPresentation is not null)
            return "InvalidStudySeatingPresentation";
        if (request.Capability == "study.homework" ? request.StudyHomework is not { SnapshotId.Length: 64, Index: >= 0 and < 128 } read ||
            !read.SnapshotId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') : request.StudyHomework is not null)
            return "InvalidStudyHomeworkRead";
        if (request.Capability.StartsWith("study.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability == "study.reminder" ? request.StudyReminder is not { Phase: "Preparing" or "Active" or "Ended" or "Discipline" } reminder ||
            !ValidStudyDisplayIdentity(reminder.InstanceId, reminder.Version, reminder.Window) ||
            (reminder.Phase == "Discipline" ? reminder.DisciplineId is null || reminder.DisciplineId == Guid.Empty ||
                reminder.SessionId is null || reminder.SessionId == Guid.Empty : reminder.DisciplineId is not null || reminder.SessionId is not null)
            : request.StudyReminder is not null)
            return "InvalidStudyReminderCommand";
        if (request.Capability == "study.hide" ? request.StudyHide is not { } hide || !ValidStudyDisplayIdentity(hide.InstanceId, hide.Version, hide.Window)
            : request.StudyHide is not null) return "InvalidStudyHideCommand";
        if (request.Capability == "noise.display.return" ? request.NoiseDisplay is null || !NoiseDisplayContract.Valid(request.NoiseDisplay) : request.NoiseDisplay is not null)
            return "InvalidNoiseDisplayCommand";
        if (request.Capability == "noise.management.command" ? request.NoiseManagement is null || !NoiseManagementContract.Valid(request.NoiseManagement) : request.NoiseManagement is not null)
            return "InvalidNoiseManagementCommand";
        if (request.NoiseAuthorization is { } grant && (grant == Guid.Empty || !NoiseInterruption(request))) return "UnexpectedParameters";
        if (request.Capability == "secrandom.command" ? request.SecRandom is null || !SecRandomContract.Valid(request.SecRandom) : request.SecRandom is not null)
            return "InvalidSecRandomCommand";
        if (request.Capability.StartsWith("secrandom.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability is "noise.command" or "study.devices.microphone" ? request.Noise is null || !NoiseContract.Valid(request.Noise) || request.Capability == "study.devices.microphone" && request.Noise.Action != "select" : request.Noise is not null) return "InvalidNoiseCommand";
        if (request.Capability.StartsWith("noise.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability == "examaware.plan" ? request.ExamPlan is null || !ExamAwarePlanContract.Valid(request.ExamPlan) : request.ExamPlan is not null) return "InvalidExamPlan";
        if (request.Capability == "remoteexam.command" ? request.RemoteExam is null || !RemoteExamContract.Valid(request.RemoteExam) : request.RemoteExam is not null) return "InvalidRemoteExamCommand";
        if (request.Capability == "npep.notifications" ? request.Notification is null || !NpepNotificationContract.Valid(request.Notification) : request.Notification is not null) return "InvalidNotificationCommand";
        if (request.RequestId == Guid.Empty) return "InvalidRequestId";
        if (request.Capability is not ("remoteexam.preflight" or "remoteexam.inspect" or "remoteexam.status" or "remoteexam.command" or "npep.status" or "npep.command" or "npep.notifications" or "host.ping" or "host.stop" or "host.cached-status" or "classroom.status" or "classroom.refresh" or "classroom.set" or "classroom.restore" or "classroom.retry" or "classisland.status" or "classisland.watch" or
            "study.devices.microphone" or "study.devices.status" or "study.devices.refresh" or "study.devices.select" or "study.status.seating" or "study.seating" or "study.seating.presentation" or "study.status" or "study.hide" or "study.reminder" or "study.homework" or "study.presentation" or "secrandom.status" or "secrandom.config.set" or "secrandom.command" or
            "examaware.status" or "examaware.config.set" or "examaware.start" or "examaware.settings" or "examaware.plugins" or "examaware.pairing.get" or "examaware.pairing.reset" or "examaware.quit" or "examaware.autostart.set" or "examaware.plan" or
            "noise.meter.command" or "noise.reference.status" or "noise.reference.command" or "noise.calibration.status" or "noise.calibration.command" or "noise.status" or "noise.devices" or "noise.command" or "noise.display.status" or "noise.display.return" or "noise.management.status" or "noise.management.command" or "noise.management.prepare-exit" or "recording.status" or "recording.command" or "recording.automatic" or
            "classisland.day-plan" or "classisland.school-clock" or "classisland.schedule" or "classisland.config.get" or "classisland.config.set" or "classisland.start" or "classisland.verify" or "classisland.execution.get" or
            "presentation.touch.status" or "presentation.touch.enable" or "presentation.touch.disable" or
            "presentation.touch.pause" or "presentation.touch.resume" or "presentation.touch.compat.on" or "presentation.touch.compat.off")) return "UnknownCapability";
        if (request.Capability is "classisland.config.set" or "classisland.verify" or "examaware.config.set" or "secrandom.config.set")
        {
            if (string.IsNullOrWhiteSpace(request.ExecutablePath) || request.ExecutablePath.Length > 2048 ||
                request.ExpectedRevision is null or < 0) return "InvalidConfiguration";
        }
        else if (request.Capability is "secrandom.command" or "examaware.plan" or "examaware.autostart.set" or "classroom.set" or "classroom.restore" or "classroom.retry")
        {
            if (request.ExecutablePath is not null || request.ExpectedRevision is null or < 0) return "InvalidConfiguration";
        }
        else if (request.Capability == "examaware.quit")
        {
            if (request.ExecutablePath is not null || request.ExpectedRevision is < 0) return "InvalidConfiguration";
        }
        else if (request.ExecutablePath is not null || request.ExpectedRevision is not null) return "UnexpectedParameters";
        if (request.Capability == "examaware.autostart.set" ? request.AutoStartEnabled is null : request.AutoStartEnabled is not null)
            return "InvalidAutoStartParameters";
        if (request.Capability is "classroom.set" or "classroom.retry" ? request.ClassroomMode?.Target is not ("Daily" or "Exam") : request.ClassroomMode is not null)
            return "InvalidClassroomMode";
        if (request.ClassroomMode is { } mode &&
            ((mode.SwitchRunning && mode.Target == "Daily" && !mode.ExamWorkSaved) ||
             (mode.ExamWorkSaved && (!mode.SwitchRunning || mode.Target != "Daily")) ||
             (request.Capability == "classroom.retry" && !mode.SwitchRunning))) return "InvalidRuntimeConfirmation";
        if (request.Capability.StartsWith("classroom.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.OperationId is not null && (request.Capability != "classisland.execution.get" || request.OperationId == Guid.Empty))
            return "UnexpectedParameters";
        if (request.TimeoutMs is < 250 or > 15000) return "InvalidTimeout";
        if (request.Capability == "npep.command" ? request.Npep is null || !NpepContract.Valid(request.Npep) : request.Npep is not null) return "InvalidNpepCommand";
        if (request.Capability.StartsWith("npep.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability == "host.cached-status" && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability.StartsWith("remoteexam.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.ObserveMs < 0 || request.ObserveMs > 5000 || request.ObserveMs >= request.TimeoutMs)
            return "InvalidObservationWindow";
        if (request.Capability == "classisland.watch" && request.ObserveMs != 0) return "InvalidObservationWindow";
        if (request.Capability == "classisland.schedule" && request.ObserveMs != 0) return "InvalidObservationWindow";
        if (request.Capability == "classisland.school-clock" && request.ObserveMs != 0) return "InvalidObservationWindow";
        if (request.Capability == "classisland.day-plan" ? request.SchoolDate is null || request.ObserveMs != 0 : request.SchoolDate is not null)
            return "InvalidSchoolDate";
        if (request.Capability == "recording.command")
        {
            if (request.Recording is not { } cmd || cmd.ClientId is null || cmd.ClientId == Guid.Empty || cmd.Action is not ("start" or "pause" or "resume" or "stop") ||
                (cmd.Action == "start" ? cmd.Options is null || RecordingContract.Validate(cmd.Options) is not null || cmd.Control is not null :
                    cmd.Options is not null || cmd.Control is not { SessionId: var id, Owner: "Manual" or "Automatic" } || id == Guid.Empty)) return "InvalidRecordingCommand";
        }
        else if (request.Recording is not null) return "UnexpectedParameters";
        if (request.Capability == "recording.automatic")
        {
            if (request.Automatic is not { } cmd || cmd.ClientId == Guid.Empty ||
                cmd.Action is not ("enable" or "disable" or "lease" or "skip-day" or "resume-day" or "skip-next") ||
                (cmd.Action == "enable" ? cmd.Options is null || RecordingContract.Validate(cmd.Options) is not null : cmd.Options is not null)) return "InvalidAutomaticCommand";
        }
        else if (request.Automatic is not null) return "UnexpectedParameters";
        if (request.Capability.StartsWith("recording.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        if (request.Capability.StartsWith("examaware.", StringComparison.Ordinal) && request.ObserveMs != 0) return "UnexpectedParameters";
        return null;
    }

    public static bool NoiseInterruption(HostRequest r) => r.Capability is "host.stop" or "noise.management.prepare-exit" ||
        r.Capability == "noise.command" && r.Noise?.Action == "stop" ||
        r.Capability == "npep.command" && r.Npep?.Action is "pause" or "unpair" ||
        r.Capability is "classroom.set" or "classroom.retry" or "classroom.restore" ||
        r.Capability is "classisland.config.set" or "examaware.quit";

    private static bool ValidStudyDisplayIdentity(Guid instance, string? version, SchoolNoiseWindow? window) =>
        instance != Guid.Empty && version is { Length: 64 } && version.All(Uri.IsHexDigit) && window is not null &&
        window.Start.Offset == TimeSpan.Zero && window.End.Offset == TimeSpan.Zero && window.Start.Year >= 2000 && window.End.Year <= 9998 &&
        window.End > window.Start && window.End - window.Start <= TimeSpan.FromHours(3);

    public static string NoisePurpose(HostRequest r) => r.Capability is "host.stop" or "noise.management.prepare-exit" ? "maintenance" :
        r.Capability == "noise.command" ? "stop" : "change-environment";
}
