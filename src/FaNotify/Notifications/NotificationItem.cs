namespace FaNotify.Notifications;

internal sealed record NotificationItem(
    string Id,
    string Type,
    string Title,
    string Description,
    string Url,
    string? ActorName,
    string? ActorUrl,
    string? ImageUrl,
    string? ActorIconUrl = null);
