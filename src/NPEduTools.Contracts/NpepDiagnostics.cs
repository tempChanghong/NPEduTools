namespace NPEduTools.Contracts;

public sealed record NpepConnectionEvent(Guid Id, DateTimeOffset At, string Kind, string Endpoint, string? Code,
    int? HttpStatus, Guid? RequestId, long? ElapsedMs, bool? Retrying);
public sealed record NpepConnectionHistory(string Persistence, IReadOnlyList<NpepConnectionEvent> Events);

public static class NpepDiagnosticValues
{
    public const int MaxEvents = 48;
    public static string? Error(string? code) => code is null ? null : Errors.Contains(code, StringComparer.Ordinal) ? code : "UNKNOWN";
    private static readonly string[] Errors = ("AUTH_INVALID BINDING_CHANGED CREDENTIAL_EXPIRED CREDENTIAL_STORE_UNAVAILABLE INSTANCE_MISMATCH INVALID_REQUEST INVALID_RESPONSE JOURNAL_UNAVAILABLE NETWORK_UNAVAILABLE PAIRING_ALREADY_EXISTS PAIRING_CODE_UNAVAILABLE PAIRING_EXPIRED PREAUTHORIZATION_CHANGED REVISION_CONFLICT SCREEN_PAIRING_DISABLED SESSION_SUPERSEDED STORE_UNAVAILABLE TEMPORARILY_UNAVAILABLE TLS_VALIDATION_FAILED UNSUPPORTED SAMPLE_TOO_OLD RATE_LIMITED SEQUENCE_CONFLICT STALE_SEQUENCE SEQUENCE_EXHAUSTED SESSION_EPOCH_EXHAUSTED DEVICE_SUSPENDED INVALID_SERVER_ORIGIN LOCAL_CONFIRMATION_REQUIRED CREDENTIAL_ID_CONFLICT PAYLOAD_TOO_LARGE REDIRECT_REJECTED NOT_FOUND INVALID_ENDPOINT SCHOOL_ADMIN_REQUIRED CAPABILITY_DENIED APPROVER_NO_LONGER_AUTHORIZED PAIRING_STATE_CONFLICT IDEMPOTENCY_CONFLICT BINDING_OCCUPIED PROTOCOL_UNSUPPORTED SCREEN_TOKEN_INVALID MANAGEMENT_NOT_CONFIGURED UNKNOWN").Split(' ');
    public static NpepConnectionEvent Safe(NpepConnectionEvent item) => item with {
        At = item.At.ToUniversalTime(), Kind = Choice(item.Kind, "Failure TransportFailure RequestCancelled SlowRequest QueueWait Recovered"),
        Endpoint = Choice(item.Endpoint, "Status Command Info Pairing Registration Session StudySettings StudyHomework StudySeating StudyPhotos StudyExecution StudyPresentation StudyResume SchoolManagement Notifications Noise RuntimeControl ExamPlans Unknown"),
        Code = Error(item.Code), HttpStatus = item.HttpStatus is >= 100 and <= 599 ? item.HttpStatus : null,
        RequestId = item.RequestId == Guid.Empty ? null : item.RequestId,
        ElapsedMs = item.ElapsedMs is >= 0 and <= 600000 ? item.ElapsedMs : null };
    private static string Choice(string? value, string choices) => value is not null && choices.Split(' ').Contains(value, StringComparer.Ordinal) ? value : "Unknown";
}
