using System.Net.Http.Json;
using FaNotify.Logging;
using FaNotify.Notifications;
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
        CancellationToken cancellationToken)
    {
        foreach (var (index, batch) in notifications.Chunk(10).Index())
        {
            logger.SendingBatch(index + 1, batch.Length);
            var embeds = batch.Select(notification =>
            {
                var embed = new Dictionary<string, object>
                {
                    ["title"] = Limit($"{Capitalize(notification.Type)}: {notification.Title}", 256),
                    ["description"] = notification.Description,
                    ["url"] = notification.Url,
                    ["color"] = notification.Type switch
                    {
                        "submissions" => 0x2E8B57,
                        "watches" => 0x5865F2,
                        "comments" => 0xE67E22,
                        "favorites" => 0xE84393,
                        "journals" => 0x3498DB,
                        _ => 0x7F8C8D
                    },
                    ["footer"] = new { text = "FurAffinity Notify" },
                    ["timestamp"] = DateTimeOffset.UtcNow
                };

                if (!string.IsNullOrWhiteSpace(notification.ActorName))
                {
                    var author = new Dictionary<string, string> { ["name"] = notification.ActorName };
                    if (notification.ActorUrl is not null)
                    {
                        author["url"] = notification.ActorUrl;
                    }

                    if (notification.ActorIconUrl is not null)
                    {
                        author["icon_url"] = notification.ActorIconUrl;
                    }

                    embed["author"] = author;
                }

                if (notification.ImageUrl is not null)
                {
                    embed["image"] = new { url = notification.ImageUrl };
                }
                else if (notification.ActorIconUrl is not null)
                {
                    embed["thumbnail"] = new { url = notification.ActorIconUrl };
                }

                return embed;
            }).ToArray();

            var payload = new Dictionary<string, object> { ["embeds"] = embeds };
            if (index == 0 && !string.IsNullOrWhiteSpace(prefix))
            {
                payload["content"] = prefix;
            }

            using var response = await client.PostAsJsonAsync(webhookUrl, payload, cancellationToken);
            response.EnsureSuccessStatusCode();
            foreach (var notification in batch)
            {
                logger.NotificationAccepted(notification.Type, notification.Id, notification.Title);
            }
        }
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
