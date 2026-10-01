namespace FaNotify.Notifications;

internal sealed record NotificationSnapshot(
    int RegisteredUsers,
    Dictionary<string, int> Counts,
    Dictionary<string, Uri> CategoryUrls,
    List<NotificationItem> Items,
    HashSet<string> CountFallbacks);
