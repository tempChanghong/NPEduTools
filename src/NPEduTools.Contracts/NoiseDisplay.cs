namespace NPEduTools.Contracts;

// Display availability is advisory. It never grants microphone control or changes a schedule.
public sealed record NoiseDisplayCommand(string Action, Guid InstanceId, Guid SessionId);
public sealed record NoiseDisplayState(string Phase, string Message, bool Fallback,
    Guid? InstanceId = null, Guid? SessionId = null, SchoolNoiseWindow? Window = null,
    int ReturnMinutes = 10, double ReturnRemainingSeconds = 0, string? WebsiteState = null,
    string? ErrorCode = null);
public static class NoiseDisplayContract
{
    public static bool Valid(NoiseDisplayCommand c) => c.Action == "return" && c.InstanceId != Guid.Empty && c.SessionId != Guid.Empty;
}
