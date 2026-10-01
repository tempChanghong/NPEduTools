using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NPEduTools.Contracts;

public sealed record ExamAwarePlanInput(string Action, string? DataBase64 = null, Guid? PreparationId = null);
public sealed record ExamAwarePlanSummary(Guid PreparationId, string Sha256, string ExamName, string Message, ExamAwarePlanExam[] Exams);
public sealed record ExamAwarePlanExam(string Name, string Start, string End, double AlertTime);
public sealed record ExamAwarePlayerSession(string Id, string State, string ExamName);
public sealed record ExamAwarePlayerStatus(bool Known, ExamAwarePlayerSession[] Sessions, ExamAwarePlayerSession? LastSession = null);
public sealed record ExamAwarePlanState(Guid RequestId, string State, string Message, string? SessionId = null);
public sealed record ExamAwarePlanAck(Guid RequestId, string State, ExamAwarePlanSummary? Summary = null, string? SessionId = null);
public sealed record ExamAwarePlanWireCommand(Guid RequestId, string Action, long IssuedAt, long ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DataBase64 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? PreparationId = null);

public static class ExamAwarePlanContract
{
    public const int MaxBytes = 24 * 1024;
    public static bool Valid(ExamAwarePlanInput input)
    {
        if (input.Action == "start") return input.DataBase64 is null && input.PreparationId is { } id && id != Guid.Empty;
        if (input.Action != "prepare" || input.PreparationId is not null || input.DataBase64 is not { Length: > 0 and <= 32768 } text) return false;
        try
        {
            var bytes = Convert.FromBase64String(text);
            if (bytes.Length > MaxBytes || Convert.ToBase64String(bytes) != text) return false;
            string json = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            // Both IPC layers retain their 64 KiB limit. Escaping '+' in base64 can expand
            // the nested signed payload, so raw file size alone is not a sufficient bound.
            string payload = JsonSerializer.Serialize(new ExamAwarePlanWireCommand(Guid.Empty, "plan.prepare",
                long.MaxValue, long.MaxValue, text), Protocol.Json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                JsonSerializer.SerializeToUtf8Bytes(new ExamAwareFrame(2, "command", long.MaxValue, payload, new string('0', 64)), Protocol.Json).Length <= Protocol.MaxFrameBytes;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException) { return false; }
    }
    public static bool Valid(ExamAwarePlayerStatus? status) => status is null || status.Sessions is { Length: <= 8 } sessions &&
        (status.Known || sessions.Length == 0 && status.LastSession is null) && sessions.All(s => ValidSession(s) &&
            s.State is "preparing" or "opening" or "ready" or "closing") && (status.LastSession is null || ValidSession(status.LastSession));
    private static bool ValidSession(ExamAwarePlayerSession? s) => s is not null && s.Id is { Length: > 0 and <= 128 } &&
        s.ExamName is { Length: <= 160 } && s.State is "preparing" or "opening" or "ready" or "closing" or "closed" or "failed";
    public static bool Valid(ExamAwarePlanAck ack) => ack.RequestId != Guid.Empty && ack.State switch
    {
        "Prepared" => ack.SessionId is null && ack.Summary is { PreparationId: var id, Sha256: { Length: 64 } hash,
            ExamName.Length: > 0 and <= 160, Message.Length: <= 2000, Exams: { Length: > 0 and <= 32 } exams } &&
            id != Guid.Empty && hash.All(Uri.IsHexDigit) && exams.All(x => x is not null && x.Name is { Length: > 0 and <= 160 } &&
                x.Start is { Length: <= 64 } && x.End is { Length: <= 64 } && double.IsFinite(x.AlertTime)) &&
            ack.Summary.ExamName.Length + ack.Summary.Message.Length + exams.Sum(x => x.Name.Length + x.Start.Length + x.End.Length) <= 6000,
        "Started" => ack.Summary is null && ack.SessionId is { Length: > 0 and <= 128 },
        "Invalid" or "Busy" or "Expired" or "Denied" or "Unconfirmed" or "Unsupported" or "LimitReached" => ack.Summary is null && ack.SessionId is null,
        _ => false
    };
}
