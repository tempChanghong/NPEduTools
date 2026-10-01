namespace NPEduTools.App;

public enum SchoolNotificationPriority { Minor, Normal, Important, Urgent }

// Presentation data only. Authorization and delivery receipts belong to the Host.
public sealed record SchoolNotification(string Title, string Body, string Source,
    DateTimeOffset PublishedAt, SchoolNotificationPriority Priority, bool IsPreview = false)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Priority) || string.IsNullOrWhiteSpace(Title) || Title.Length > 160 ||
            string.IsNullOrWhiteSpace(Body) || Body.Length > 8000 ||
            string.IsNullOrWhiteSpace(Source) || Source.Length > 120)
            throw new ArgumentException("通知内容不完整或超出显示长度限制。");
    }

    public static SchoolNotification Preview(SchoolNotificationPriority priority) => new(
        priority switch
        {
            SchoolNotificationPriority.Minor => "让每一条消息，清晰可见。",
            SchoolNotificationPriority.Normal => "请留意下一节课的安排。",
            SchoolNotificationPriority.Important => "重要事项，请及时查看。",
            _ => "紧急通知，请立即关注。"
        },
        "这是通知样式预览。\n\n学校通知将在这里显示完整内容。窗口保持置顶，阅读后请手动关闭。",
        "NPEduTools · 本地预览", DateTimeOffset.Now, priority, true);
}

public sealed record SchoolNotificationStyle(string Label, string Background)
{
    public static SchoolNotificationStyle For(SchoolNotificationPriority priority) => priority switch
    {
        SchoolNotificationPriority.Minor => new("次要通知", "#087F5B"),
        SchoolNotificationPriority.Normal => new("普通通知", "#2157B8"),
        // Deep yellow preserves white text contrast, including the smaller labels.
        SchoolNotificationPriority.Important => new("重要通知", "#947000"),
        SchoolNotificationPriority.Urgent => new("紧急通知", "#C52B36"),
        _ => throw new ArgumentOutOfRangeException(nameof(priority))
    };
}
