namespace NPEduTools.Contracts;

public sealed record ClassroomModeCommand(string Target);
public sealed record ClassroomStartupSnapshot(string ClassIslandPath, long ClassIslandRevision,
    bool ClassIslandEnabled, string ExamAwarePath, long ExamAwareRevision, bool ExamAwareEnabled);
public sealed record ClassroomModeRecovery(string PreviousMode, bool PreviousPause, ClassroomStartupSnapshot Startup);
public sealed record ClassroomModeState(long Revision = 0, string Mode = "Unconfigured", string Phase = "Idle",
    bool AutomaticPaused = false, string Message = "尚未设置课堂模式。", ClassroomStartupSnapshot? Actual = null,
    DateTimeOffset? CheckedAt = null, bool? MatchesMode = null, ClassroomModeRecovery? Recovery = null,
    Guid[]? RecentRequests = null)
{
    public bool Busy => Phase is "Checking" or "Switching";
}
