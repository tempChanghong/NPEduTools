namespace NPEduTools.Contracts;

public sealed record NpepNotice(string PublicationId, long Revision, string Title, string Content, string Priority,
    DateTimeOffset PublishAt, DateTimeOffset? ExpiresAt, string Source, bool PopupEnabled);
public sealed record NpepNoticeSummary(string PublicationId, long Revision, string Title, string Priority, bool PopupEnabled, bool Dismissed)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLabel => (Priority switch { "MINOR" => "次要", "NORMAL" => "普通", "IMPORTANT" => "重要", _ => "紧急" }) +
        (Dismissed ? " · 已手动关闭" : PopupEnabled ? " · 弹窗通知" : " · 仅在列表显示");
}
public sealed record NpepNotificationCommand(string Action, string? Scope = null, string? PublicationId = null, long Revision = 0, int Offset = 0);
public sealed record NpepInboxState(string? Scope, string Connection, string Message, int Total,
    IReadOnlyList<NpepNoticeSummary> Items, int? NextOffset = null, NpepNotice? Current = null, bool CanPresent = false);

public static class NpepNotificationContract
{
    public static bool Valid(NpepNotificationCommand c) => c.Action is "poll" or "get" or "displayed" or "dismissed" &&
        c.Offset is >= 0 and <= 500 && (c.Action == "poll"
            ? c.PublicationId is null && c.Scope is null && c.Revision == 0
            : c.Scope is { Length: 64 } && c.Scope.All(Uri.IsHexDigit) && c.PublicationId is { Length: > 0 and <= 191 } &&
                !c.PublicationId.Any(char.IsControl) && c.Revision is > 0 and <= 9007199254740991 && c.Offset == 0);
}
