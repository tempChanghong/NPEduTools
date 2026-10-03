namespace NPEduTools.Contracts;

public sealed record SecRandomCommand(string Action, Guid? AcknowledgeOperation = null);
public sealed record SecRandomDrawWinner(string Id, string Name, string Gender);
public sealed record SecRandomOperation(Guid RequestId, string Action, string State, string Message, DateTimeOffset UpdatedAt,
    string? ErrorCode = null, SecRandomDrawWinner? Winner = null);
public sealed record SecRandomState(string? ExecutablePath, long Revision, string Connection, DateTimeOffset? CheckedAt,
    SecRandomOperation? Operation, string? Error = null);

public static class SecRandomContract
{
    public static bool Valid(SecRandomCommand command) => (command.Action is "check" or "open" or "show-float" or "hide-float" or "quick-draw" or "acknowledge") &&
        (command.Action == "acknowledge" ? command.AcknowledgeOperation is { } id && id != Guid.Empty : command.AcknowledgeOperation is null);
}
