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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepCommand? Npep = null);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NpepState? Npep = null);

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
        if (request.RequestId == Guid.Empty) return "InvalidRequestId";
        if (request.Capability is not ("npep.status" or "npep.command" or "host.ping" or "host.stop" or "host.cached-status" or "classroom.status" or "classroom.refresh" or "classroom.set" or "classroom.restore" or "classroom.retry" or "classisland.status" or "classisland.watch" or
            "examaware.status" or "examaware.config.set" or "examaware.start" or "examaware.settings" or "examaware.plugins" or "examaware.pairing.get" or "examaware.pairing.reset" or "examaware.quit" or "examaware.autostart.set" or
            "recording.status" or "recording.command" or "recording.automatic" or
            "classisland.day-plan" or "classisland.school-clock" or "classisland.schedule" or "classisland.config.get" or "classisland.config.set" or "classisland.start" or "classisland.verify" or "classisland.execution.get" or
            "presentation.touch.status" or "presentation.touch.enable" or "presentation.touch.disable" or
            "presentation.touch.pause" or "presentation.touch.resume" or "presentation.touch.compat.on" or "presentation.touch.compat.off")) return "UnknownCapability";
        if (request.Capability is "classisland.config.set" or "classisland.verify" or "examaware.config.set")
        {
            if (string.IsNullOrWhiteSpace(request.ExecutablePath) || request.ExecutablePath.Length > 2048 ||
                request.ExpectedRevision is null or < 0) return "InvalidConfiguration";
        }
        else if (request.Capability is "examaware.autostart.set" or "classroom.set" or "classroom.restore" or "classroom.retry")
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
}
