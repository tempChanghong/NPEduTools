using NPEduTools.Contracts;
namespace NPEduTools.Host;
public static class StudyProjection
{
    public static bool Active(ExamAwareStatus status) => status.BridgeState == "Connected" && status.Player is { Known: true } player &&
        player.Sessions.Any(s => s.State is "opening" or "ready" or "closing");
}
