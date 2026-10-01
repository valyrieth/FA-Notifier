using System.Text.Json.Serialization;

namespace FaNotify.Discord;

internal sealed record DiscordPayload(string? Content, DiscordEmbed[] Embeds);

internal sealed record DiscordEmbed(
    string Title,
    string Description,
    string Url,
    int Color,
    DiscordFooter Footer,
    DateTimeOffset Timestamp,
    DiscordAuthor? Author,
    DiscordImage? Image,
    DiscordImage? Thumbnail);

internal sealed record DiscordFooter(string Text);

internal sealed record DiscordAuthor(string Name, string? Url, [property: JsonPropertyName("icon_url")] string? IconUrl);

internal sealed record DiscordImage(string Url);
