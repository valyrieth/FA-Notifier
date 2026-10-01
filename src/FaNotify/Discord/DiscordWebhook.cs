using System.Net.Http.Json;
using FaNotify.Http;
using FaNotify.Logging;
using FaNotify.Notifications;
using FaNotify.Serialization;
using Microsoft.Extensions.Logging;

namespace FaNotify.Discord;

internal static class DiscordWebhook
{
    public static async Task SendAsync(
        HttpClient client,
        ILogger logger,
        string webhookUrl,
        string prefix,
        IReadOnlyList<NotificationItem> notifications,
        Action<IReadOnlyList<NotificationItem>>? onBatchDelivered,
        CancellationToken cancellationToken)
    {
        foreach (var (index, batch) in notifications.Chunk(10).Index())
        {
            logger.SendingBatch(index + 1, batch.Length);
            var content = index == 0 && !string.IsNullOrWhiteSpace(prefix) ? prefix : null;
            var payload = new DiscordPayload(content, batch.Select(ToEmbed).ToArray());

            using var response = await HttpRetry.SendAsync(
                token => client.PostAsJsonAsync(webhookUrl, payload, AppJsonContext.Default.DiscordPayload, token),
                logger,
                "Discord webhook request",
                cancellationToken);
            response.EnsureSuccessStatusCode();
            foreach (var notification in batch)
            {
                logger.NotificationAccepted(notification.Type, notification.Id, notification.Title);
            }

            onBatchDelivered?.Invoke(batch);
        }
    }

    private static DiscordEmbed ToEmbed(NotificationItem notification) => new(
        Limit($"{Capitalize(notification.Type)}: {notification.Title}", 256),
        notification.Description,
        notification.Url,
        GetColor(notification.Type),
        new DiscordFooter("FurAffinity Notify"),
        DateTimeOffset.UtcNow,
        string.IsNullOrWhiteSpace(notification.ActorName)
            ? null
            : new DiscordAuthor(notification.ActorName, notification.ActorUrl, notification.ActorIconUrl),
        notification.ImageUrl is null ? null : new DiscordImage(notification.ImageUrl),
        notification.ImageUrl is null && notification.ActorIconUrl is not null ? new DiscordImage(notification.ActorIconUrl) : null);

    private static int GetColor(string type) => type switch
    {
        "submissions" => 0x2E8B57,
        "watches" => 0x5865F2,
        "comments" => 0xE67E22,
        "favorites" => 0xE84393,
        "journals" => 0x3498DB,
        _ => 0x7F8C8D
    };

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
