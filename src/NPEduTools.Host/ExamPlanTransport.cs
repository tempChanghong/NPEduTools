using System.Text.Json;
using System.Text.Json.Nodes;
using NPEduTools.Contracts;
using NPEduTools.Integrations.Npep;

namespace NPEduTools.Host;

public sealed class ExamPlanTransport(ExamAwareService examAware, RecordingService recording,
    ClassroomModeService classroom, IRemoteExamPlatform platform) : INpepExamPlans
{
    private string? Block(ExamAwareStatus status)
    {
        if (!platform.DesktopAvailable) return "DESKTOP_UNAVAILABLE";
        if (classroom.Snapshot is not { Mode: "Exam", Phase: "Idle", Recovery: null, Runtime: null }) return "EXAM_MODE_REQUIRED";
        if (recording.State.Active) return "RECORDING_BUSY";
        if (status.BridgeState != "Connected") return "BRIDGE_DISCONNECTED";
        if (!status.CanPresent) return "BRIDGE_UPGRADE_REQUIRED";
        if (status.Player is not { Known: true }) return "PLAYER_UNKNOWN";
        if (status.Player.Sessions.Length != 0) return "PLAYER_BUSY";
        return null;
    }
    public JsonObject Observe()
    {
        var s = examAware.Snapshot(); var block = Block(s);
        // Serialize with explicit nulls, independently of the optional fields on local IPC.
        var player = s.Player is null ? null : new JsonObject { ["known"] = s.Player.Known,
            ["sessions"] = JsonSerializer.SerializeToNode(s.Player.Sessions, Protocol.Json),
            ["lastSession"] = JsonSerializer.SerializeToNode(s.Player.LastSession, Protocol.Json) };
        return new() { ["revision"] = s.Revision, ["available"] = block is null, ["blockReason"] = block,
            ["player"] = player, ["preparedId"] = s.PreparedPlan?.PreparationId.ToString("D") };
    }
    public async Task<JsonObject> ExecuteAsync(JsonObject op, bool start, Guid commandId, Action authorize, CancellationToken token)
    {
        JsonObject Result(string state, string? reason = null, ExamAwarePlanSummary? summary = null, string? session = null) =>
            new() { ["state"] = state, ["reasonCode"] = reason, ["summary"] = JsonSerializer.SerializeToNode(summary, Protocol.Json), ["sessionId"] = session };
        try
        {
            using var idle = await recording.ReserveIdleForRuntimeAsync(token);
            var input = start ? new ExamAwarePlanInput("start", PreparationId: Guid.Parse(((JsonObject)op["summary"]!).Text("preparationId")))
                : new ExamAwarePlanInput("prepare", op.Text("dataBase64"));
            var request = new HostRequest(Protocol.Version, commandId, "examaware.plan", ExpectedRevision: op.Number("revision"), ExamPlan: input);
            var reply = await examAware.RemotePlanAsync(request, op.Text("sha256"), () =>
            {
                authorize();
                if (Block(examAware.Snapshot()) is { } reason) throw new RemoteExamException(reason);
            }, token);
            if (reply.Outcome != "Accepted") return Result("FAILED", "LOCAL_REQUEST_REJECTED");
            var s = reply.ExamAware!;
            if (s.PlanOperation?.RequestId != commandId) return Result("UNKNOWN", "UNKNOWN_RESULT");
            return s.PlanOperation.State switch
            {
                "Prepared" when s.PreparedPlan is not null => Result("PREPARED", summary: s.PreparedPlan),
                "Started" when s.PlanOperation.SessionId is not null => Result("STARTED", session: s.PlanOperation.SessionId),
                "Unconfirmed" or "Sending" => Result("UNKNOWN", "UNKNOWN_RESULT"),
                "Busy" => Result("FAILED", "PLAYER_BUSY"), "Invalid" => Result("FAILED", "INVALID_PLAN"),
                "Expired" => Result("FAILED", "PLAN_EXPIRED"), "Denied" => Result("FAILED", "PLAYER_PERMISSION_DENIED"),
                "LimitReached" => Result("FAILED", "BRIDGE_COMMAND_LIMIT"), _ => Result("FAILED", "LOCAL_REQUEST_REJECTED")
            };
        }
        catch (RemoteExamException e) { return Result("FAILED", e.Code); }
    }
}
