using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

var config = AppConfig.Load();
var cookieContainer = CookieFile.Load(config.CookieFile);
using var handler = new HttpClientHandler { CookieContainer = cookieContainer };
using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
using var solverClient = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(config.UserAgent);

var state = StateFile.Load(config.StateFile);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

if (config.UseFlareSolverr)
{
    await FlareSolverr.WaitUntilReadyAsync(solverClient, cancellation.Token);
}

Console.WriteLine($"FA Notify started. Checking every {config.IntervalMinutes} minutes.");
while (!cancellation.IsCancellationRequested)
{
    try
    {
        var snapshot = await FaNotifications.FetchAsync(
            httpClient,
            solverClient,
            cookieContainer,
            config.UseFlareSolverr,
            state.Counts,
            !state.ItemsInitialized,
            cancellation.Token);
        var newItems = snapshot.Items
            .Where(item => config.NotifyOn.Contains(item.Type))
            .Where(item => !state.SeenItems.ContainsKey(item.Id))
            .ToList();

        foreach (var type in snapshot.CountFallbacks)
        {
            var count = snapshot.Counts.GetValueOrDefault(type);
            var fallbackId = $"count:{type}:{count}";
            if (config.NotifyOn.Contains(type) && !state.SeenItems.ContainsKey(fallbackId))
            {
                newItems.Add(new NotificationItem(
                    fallbackId,
                    type,
                    $"{char.ToUpperInvariant(type[0]) + type[1..]} notification count changed",
                    $"There are {count} unread notifications. FA's item details were not recognized on this page.",
                    snapshot.CategoryUrls.GetValueOrDefault(type, new Uri("https://www.furaffinity.net/" )).ToString(),
                    null,
                    null,
                    null));
            }
        }

        if (newItems.Count > 0)
        {
            await DiscordWebhook.SendAsync(httpClient, config.WebhookUrl, config.NotificationPrefix, newItems, cancellation.Token);
            foreach (var item in newItems)
            {
                state.SeenItems[item.Id] = DateTimeOffset.UtcNow;
            }

            Console.WriteLine($"Sent {newItems.Count} new notification item(s).");
        }
        else
        {
            Console.WriteLine("No new notifications.");
        }

        state.Counts = snapshot.Counts;
        state.ItemsInitialized = true;
        state.TrimSeenItems();
        StateFile.Save(config.StateFile, state);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Check failed: {exception.Message}");
    }

    try
    {
        await Task.Delay(TimeSpan.FromMinutes(config.IntervalMinutes), cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        break;
    }
}

internal sealed record AppConfig(
    string CookieFile,
    string StateFile,
    string WebhookUrl,
    string UserAgent,
    string NotificationPrefix,
    int IntervalMinutes,
    bool UseFlareSolverr,
    HashSet<string> NotifyOn)
{
    private static readonly string[] NotificationTypes = ["submissions", "watches", "comments", "favorites", "journals", "notes"];

    public static AppConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("FA_NOTIFY_CONFIG") ?? "/app/settings.json";
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Settings file contains invalid JSON.");

        if (!Uri.TryCreate(settings.DiscordWebhookUrl, UriKind.Absolute, out var webhookUri) || webhookUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Set discordWebhookUrl in settings.json to your HTTPS Discord webhook URL.");
        }

        if (settings.PollIntervalMinutes < 1)
        {
            throw new InvalidOperationException("pollIntervalMinutes must be a positive whole number.");
        }

        var solverSetting = Environment.GetEnvironmentVariable("FA_USE_FLARESOLVERR") ?? "false";
        var notifyOn = (settings.NotifyOn ?? NotificationTypes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidTypes = notifyOn.Except(NotificationTypes, StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalidTypes.Length > 0)
        {
            throw new InvalidOperationException($"Unknown notification type(s): {string.Join(", ", invalidTypes)}.");
        }

        return new AppConfig(
            "/app/cookies.txt",
            "/data/notifications.json",
            webhookUri.ToString(),
            settings.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:157.0) Gecko/20100101 Firefox/157.0",
            settings.NotificationPrefix ?? string.Empty,
            settings.PollIntervalMinutes,
            settings.UseFlareSolverr ?? false,
            notifyOn);
    }

    private sealed record Settings(
        string? DiscordWebhookUrl,
        int PollIntervalMinutes = 30,
        string[]? NotifyOn = null,
        string? NotificationPrefix = null,
        string? UserAgent = null,
        bool? UseFlareSolverr = null);
}

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

internal sealed record NotificationSnapshot(
    Dictionary<string, int> Counts,
    Dictionary<string, Uri> CategoryUrls,
    List<NotificationItem> Items,
    HashSet<string> CountFallbacks);

internal sealed class NotificationState
{
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DateTimeOffset> SeenItems { get; set; } = new(StringComparer.Ordinal);
    public bool ItemsInitialized { get; set; }

    public void TrimSeenItems()
    {
        if (SeenItems.Count > 5000)
        {
            SeenItems = SeenItems
                .OrderByDescending(item => item.Value)
                .Take(5000)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        }
    }
}

internal static class CookieFile
{
    public static CookieContainer Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("FA cookie file not found. Mount your exported cookies.txt file into the container.", path);
        }

        var container = new CookieContainer();
        var loaded = 0;
        foreach (var originalLine in File.ReadLines(path))
        {
            var line = originalLine.TrimEnd('\r', '\n');
            var httpOnly = line.StartsWith("#HttpOnly_", StringComparison.Ordinal);
            if (httpOnly)
            {
                line = line[10..];
            }
            else if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length < 7)
            {
                throw new InvalidDataException("The cookie file is not in Netscape cookies.txt format.");
            }

            var cookie = new Cookie(fields[5], fields[6], fields[2], fields[0])
            {
                Secure = fields[3].Equals("TRUE", StringComparison.OrdinalIgnoreCase),
                HttpOnly = httpOnly
            };

            if (long.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiry) && expiry > 0)
            {
                cookie.Expires = DateTimeOffset.FromUnixTimeSeconds(expiry).UtcDateTime;
                if (cookie.Expired)
                {
                    continue;
                }
            }

            container.Add(cookie);
            loaded++;
        }

        if (loaded == 0)
        {
            throw new InvalidDataException("No unexpired cookies were found in the Netscape cookie file.");
        }

        return container;
    }
}

internal static class FaNotifications
{
    private static readonly string[] NotificationTypes = ["submissions", "watches", "comments", "favorites", "journals", "notes"];
    private static readonly Uri HomePage = new("https://www.furaffinity.net/");

    public static async Task<NotificationSnapshot> FetchAsync(
        HttpClient client,
        HttpClient solverClient,
        CookieContainer cookieContainer,
        bool useFlareSolverr,
        IReadOnlyDictionary<string, int> previousCounts,
        bool initialSync,
        CancellationToken cancellationToken)
    {
        var homePage = await DownloadPageAsync(client, solverClient, cookieContainer, useFlareSolverr, HomePage, cancellationToken);
        var document = ParseDocument(homePage);

        var notifications = NotificationTypes.ToDictionary(type => type, _ => 0);
        var categoryUrls = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase);
        var links = document.DocumentNode.SelectNodes(
            "//a[contains(concat(' ', normalize-space(@class), ' '), ' notification-container ') and contains(concat(' ', normalize-space(@class), ' '), ' inline ')]");

        if (links is not null)
        {
            foreach (var link in links)
            {
                var href = link.GetAttributeValue("href", string.Empty);
                var type = GetTypeFromHref(href);
                if (type is null)
                {
                    continue;
                }

                var match = Regex.Match(link.InnerText, @"\d[\d,]*");
                if (match.Success && int.TryParse(match.Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count))
                {
                    notifications[type] = count;
                    if (TryGetFaUri(homePage.Uri, href, out var categoryUrl))
                    {
                        categoryUrls[type] = categoryUrl;
                    }
                }
            }
        }

        var items = new List<NotificationItem>();
        var countFallbacks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in NotificationTypes)
        {
            if (notifications[type] <= 0
                || (!initialSync && notifications[type] <= previousCounts.GetValueOrDefault(type)))
            {
                continue;
            }

            if (!categoryUrls.TryGetValue(type, out var categoryUrl))
            {
                countFallbacks.Add(type);
                continue;
            }

            var detailPage = await DownloadPageAsync(client, solverClient, cookieContainer, useFlareSolverr, categoryUrl, cancellationToken);
            var detailDocument = ParseDocument(detailPage);
            var section = detailDocument.DocumentNode.SelectSingleNode($"//*[@id='messages-{type}']") ?? detailDocument.DocumentNode;
            var rows = section.SelectNodes(".//ul[contains(concat(' ', normalize-space(@class), ' '), ' message-stream ')]/li");
            var pageItems = rows?.Select(row => ParseItem(row, type, detailPage.Uri))
                .Where(item => item is not null)
                .Cast<NotificationItem>()
                .ToArray() ?? [];

            if (pageItems.Length == 0)
            {
                countFallbacks.Add(type);
            }
            else
            {
                items.AddRange(pageItems);
            }
        }

        return new NotificationSnapshot(notifications, categoryUrls, items, countFallbacks);
    }

    private static async Task<(HttpStatusCode StatusCode, string Html, Uri Uri)> DownloadPageAsync(
        HttpClient client,
        HttpClient solverClient,
        CookieContainer cookieContainer,
        bool useFlareSolverr,
        Uri target,
        CancellationToken cancellationToken)
    {
        if (useFlareSolverr)
        {
            var page = await FlareSolverr.FetchPageAsync(solverClient, target, cookieContainer, cancellationToken);
            return (page.StatusCode, page.Html, page.FinalUri ?? target);
        }

        using var response = await client.GetAsync(target, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken), response.RequestMessage?.RequestUri ?? target);
    }

    private static HtmlDocument ParseDocument((HttpStatusCode StatusCode, string Html, Uri Uri) page)
    {
        if (IsLoginPage(page.Html) || page.Uri?.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new InvalidOperationException("FurAffinity redirected to login. Refresh cookies.txt and restart the container.");
        }

        if (page.StatusCode == HttpStatusCode.Forbidden)
        {
            var challengedByCloudflare = page.Html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
                || page.Html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase)
                || page.Html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);
            var reason = challengedByCloudflare
                ? "FurAffinity's Cloudflare security check blocked the request. Check the User-Agent and network/IP; cookies alone cannot pass this challenge."
                : "FurAffinity denied the request. The session may be invalid, or the request may be blocked by FA's security rules.";
            throw new HttpRequestException($"FurAffinity returned HTTP 403 at {page.Uri?.Host}{page.Uri?.AbsolutePath}. {reason}");
        }

        if ((int)page.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException($"FurAffinity returned HTTP {(int)page.StatusCode} at {page.Uri?.Host}{page.Uri?.AbsolutePath}.");
        }

        var document = new HtmlDocument();
        document.LoadHtml(page.Html);
        return document;
    }

    private static NotificationItem? ParseItem(HtmlNode row, string type, Uri pageUri)
    {
        var anchors = row.SelectNodes(".//a[@href]");
        if (anchors is null)
        {
            return null;
        }

        var actorAnchor = anchors.FirstOrDefault(anchor => IsUserHref(anchor.GetAttributeValue("href", string.Empty)));
        var primaryAnchor = anchors.FirstOrDefault(anchor => IsPrimaryHref(type, anchor.GetAttributeValue("href", string.Empty)));
        if (primaryAnchor is null && type == "notes")
        {
            primaryAnchor = anchors.FirstOrDefault(anchor => !IsUserHref(anchor.GetAttributeValue("href", string.Empty)));
        }

        if (primaryAnchor is null || !TryGetFaUri(pageUri, primaryAnchor.GetAttributeValue("href", string.Empty), out var itemUri))
        {
            return null;
        }

        if (type == "watches")
        {
            actorAnchor = primaryAnchor;
        }

        var actorName = actorAnchor is null ? null : NormalizeText(actorAnchor.InnerText);
        var actorUrl = actorAnchor is not null && TryGetFaUri(pageUri, actorAnchor.GetAttributeValue("href", string.Empty), out var parsedActorUrl)
            ? parsedActorUrl.ToString()
            : null;
        var images = row.SelectNodes(".//img[@src or @data-src]")?.ToArray() ?? [];
        var icon = images.FirstOrDefault(image => IsAvatarImage(image.GetAttributeValue("src", image.GetAttributeValue("data-src", string.Empty)), actorName));
        var artwork = images.FirstOrDefault(image => !IsAvatarImage(image.GetAttributeValue("src", image.GetAttributeValue("data-src", string.Empty)), actorName));
        var titleNode = row.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' journal_subject ')]");
        var title = NormalizeText(titleNode?.InnerText)
            ?? FirstNonEmpty(
                primaryAnchor.GetAttributeValue("title", string.Empty),
                primaryAnchor.GetAttributeValue("aria-label", string.Empty),
                images.Select(image => image.GetAttributeValue("alt", string.Empty)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                primaryAnchor.InnerText,
                actorName,
                type);
        var description = GetDescription(row);
        if (type == "watches" && !string.IsNullOrWhiteSpace(actorName))
        {
            description = $"{actorName} is now watching you.";
        }

        var identity = $"{type}\n{itemUri.AbsoluteUri}\n{actorName}\n{description}";
        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
        return new NotificationItem(
            id,
            type,
            Limit(title, 240),
            Limit(description, 3500),
            itemUri.ToString(),
            actorName,
            actorUrl,
            GetImageUrl(artwork, pageUri),
            GetImageUrl(icon, pageUri));
    }

    private static bool IsPrimaryHref(string type, string href)
    {
        return type switch
        {
            "submissions" or "comments" or "favorites" => href.Contains("/view/", StringComparison.OrdinalIgnoreCase),
            "watches" => IsUserHref(href),
            "journals" => href.Contains("/journal/", StringComparison.OrdinalIgnoreCase),
            "notes" => href.Contains("pms", StringComparison.OrdinalIgnoreCase) || href.Contains("message", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsUserHref(string href) => href.Contains("/user/", StringComparison.OrdinalIgnoreCase);

    private static string GetDescription(HtmlNode row)
    {
        var clone = row.CloneNode(true);
        foreach (var node in clone.SelectNodes(".//input | .//button | .//script | .//style | .//*[contains(concat(' ', normalize-space(@class), ' '), ' popup_date ')]")?.ToArray() ?? [])
        {
            node.Remove();
        }

        return NormalizeText(clone.InnerText) ?? string.Empty;
    }

    private static bool IsAvatarImage(string source, string? actorName)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return source.Contains("user_icons", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(actorName) && source.Contains(actorName, StringComparison.OrdinalIgnoreCase));
        }

        return uri.Host.StartsWith("a.", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("user_icons", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetImageUrl(HtmlNode? image, Uri pageUri)
    {
        if (image is null)
        {
            return null;
        }

        var source = image.GetAttributeValue("src", image.GetAttributeValue("data-src", string.Empty));
        return TryGetFaUri(pageUri, source, out var uri) ? uri.ToString() : null;
    }

    private static string? GetTypeFromHref(string href) => NotificationTypes.FirstOrDefault(type =>
        href.Contains(type == "notes" ? "pms" : type, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetFaUri(Uri baseUri, string href, out Uri uri)
    {
        if (Uri.TryCreate(baseUri, href, out uri!)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("furaffinity.net", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".furaffinity.net", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        uri = null!;
        return false;
    }

    private static string? NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Regex.Replace(HtmlEntity.DeEntitize(text), @"\s+", " ").Trim();
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "FurAffinity notification";

    private static string Limit(string text, int maxLength) => text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    private static bool IsLoginPage(string html)
    {
        return html.Contains("<form", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(html, "<input[^>]+type=[\\\"']password[\\\"']", RegexOptions.IgnoreCase);
    }
}

internal static class FlareSolverr
{
    private static readonly Uri ServiceUrl = new("http://flaresolverr:8191/v1");

    public static async Task WaitUntilReadyAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var serviceRoot = new Uri(ServiceUrl, "/");
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(serviceRoot, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine("FlareSolverr is ready.");
                    return;
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            Console.WriteLine("Waiting for FlareSolverr to start...");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("FlareSolverr did not become ready. Start it with the Docker Compose solver profile.");
    }

    public static async Task<(HttpStatusCode StatusCode, string Html, Uri? FinalUri)> FetchPageAsync(
        HttpClient client,
        Uri target,
        CookieContainer cookieContainer,
        CancellationToken cancellationToken)
    {
        var cookies = cookieContainer.GetCookies(target)
            .Cast<Cookie>()
            .Select(cookie => new
            {
                name = cookie.Name,
                value = cookie.Value,
                domain = cookie.Domain,
                path = cookie.Path,
                secure = cookie.Secure,
                httpOnly = cookie.HttpOnly
            })
            .ToArray();

        var request = new
        {
            cmd = "request.get",
            url = target.AbsoluteUri,
            maxTimeout = 60000,
            disableMedia = true,
            cookies
        };

        using var response = await client.PostAsJsonAsync(ServiceUrl, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
        {
            var message = root.TryGetProperty("message", out var error) ? error.GetString() : null;
            throw new InvalidOperationException($"FlareSolverr could not fetch FurAffinity: {message ?? "unknown solver error"}");
        }

        var solution = root.GetProperty("solution");
        var html = solution.GetProperty("response").GetString()
            ?? throw new InvalidDataException("FlareSolverr returned no page content.");
        var finalUriText = solution.TryGetProperty("url", out var url) ? url.GetString() : null;
        var finalUri = Uri.TryCreate(finalUriText, UriKind.Absolute, out var parsedUri) ? parsedUri : target;
        var statusCode = solution.TryGetProperty("status", out var pageStatus)
            ? (HttpStatusCode)pageStatus.GetInt32()
            : HttpStatusCode.OK;

        return (statusCode, html, finalUri);
    }
}

internal static class DiscordWebhook
{
    public static async Task SendAsync(
        HttpClient client,
        string webhookUrl,
        string prefix,
        IReadOnlyList<NotificationItem> notifications,
        CancellationToken cancellationToken)
    {
        foreach (var (batch, index) in notifications.Chunk(10).Select((items, index) => (items, index)))
        {
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
        }
    }

    private static string Capitalize(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Limit(string value, int maxLength) => value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}

internal static class StateFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static NotificationState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NotificationState();
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("counts", out _))
        {
            return JsonSerializer.Deserialize<NotificationState>(json, JsonOptions)
                ?? throw new InvalidDataException("Notification state file contains invalid JSON.");
        }

        var legacyCounts = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOptions)
            ?? throw new InvalidDataException("Notification state file contains invalid JSON.");
        return new NotificationState { Counts = legacyCounts };
    }

    public static void Save(string path, NotificationState state)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}