namespace FaNotify.Notifications;

internal sealed record NotificationSnapshot(
    Dictionary<string, int> Counts,
    Dictionary<string, Uri> CategoryUrls,
    List<NotificationItem> Items,
    HashSet<string> CountFallbacks);
