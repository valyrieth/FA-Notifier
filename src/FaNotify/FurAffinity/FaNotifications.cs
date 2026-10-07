using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FaNotify.Http;
using FaNotify.Logging;
using FaNotify.Notifications;
using FaNotify.Solver;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;

namespace FaNotify.FurAffinity;

internal static partial class FaNotifications
{
    private static readonly Uri HomePage = new("https://www.furaffinity.net/");

    [GeneratedRegex(@"\d[\d,]*")]
    private static partial Regex CountPattern();

    [GeneratedRegex(@"(?<count>\d[\d,]*)\s+registered\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegisteredUsersPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex("""<input[^>]+type=["']password["']""", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInputPattern();

    [GeneratedRegex(@"/view/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ViewIdPattern();

    [GeneratedRegex(@"/journal/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex JournalIdPattern();

    [GeneratedRegex(@"cid:(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CommentIdPattern();

    [GeneratedRegex(@"/pms/\d+/(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NoteIdPattern();

    [GeneratedRegex(@"/user/(?<name>[^/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UserNamePattern();

    [GeneratedRegex(@"\br-(?<rating>general|mature|adult)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RatingPattern();

    public static async Task<NotificationSnapshot> FetchAsync(
        HttpClient client,
        HttpClient solverClient,
        CookieSession cookies,
        bool useFlareSolverr,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var homePage = await DownloadPageAsync(client, solverClient, cookies, useFlareSolverr, logger, HomePage, cancellationToken);
        var document = ParseDocument(homePage);
        var registeredUsers = ParseRegisteredUserCount(document);

        var notifications = NotificationTypes.All.ToDictionary(type => type, _ => 0);
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

                var match = CountPattern().Match(link.InnerText);
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

        // Comments, favorites, journals... all live on /msg/others/, so each page is downloaded once.
        var detailPages = new Dictionary<string, (HtmlDocument Document, Uri Uri)>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in NotificationTypes.All)
        {
            var currentCount = notifications[type];
            if (currentCount <= 0)
            {
                continue;
            }

            if (!categoryUrls.TryGetValue(type, out var categoryUrl))
            {
                logger.NoCategoryUrl(type);
                countFallbacks.Add(type);
                continue;
            }

            var pageKey = categoryUrl.GetLeftPart(UriPartial.Query);
            if (!detailPages.TryGetValue(pageKey, out var detail))
            {
                var detailPage = await DownloadPageAsync(client, solverClient, cookies, useFlareSolverr, logger, categoryUrl, cancellationToken);
                detail = (ParseDocument(detailPage), detailPage.Uri);
                detailPages[pageKey] = detail;
            }

            // Only ever read the type's own section; a missing section must never fall back to the whole page.
            var sectionId = type switch
            {
                "comments" => "messages-comments-submission",
                "notes" => "notes-list",
                _ => $"messages-{type}"
            };
            var section = detail.Document.DocumentNode.SelectSingleNode($"//*[@id='{sectionId}']");
            var rows = section is null ? null : GetRows(section, type);
            var pageItems = rows?.Select(row => ParseItem(row, type, detail.Uri))
                .OfType<NotificationItem>()
                .ToArray() ?? [];

            if (pageItems.Length == 0)
            {
                logger.ParsedNoItems(type, rows?.Count ?? 0);
                countFallbacks.Add(type);
            }
            else
            {
                logger.ParsedItems(pageItems.Length, type);
                items.AddRange(pageItems);
            }
        }

        return new NotificationSnapshot(registeredUsers, notifications, categoryUrls, items, countFallbacks);
    }

    private static int ParseRegisteredUserCount(HtmlDocument document)
    {
        var match = RegisteredUsersPattern().Match(document.DocumentNode.InnerText);
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count))
        {
            throw new InvalidDataException("Could not read Fur Affinity's registered-user count; notification state was not updated.");
        }

        return count;
    }

    // FlareSolverr starts a real browser, so it is only used when Cloudflare actually challenges a normal request.
    private static async Task<(HttpStatusCode StatusCode, string Html, Uri Uri)> DownloadPageAsync(
        HttpClient client,
        HttpClient solverClient,
        CookieSession cookies,
        bool useFlareSolverr,
        ILogger logger,
        Uri target,
        CancellationToken cancellationToken)
    {
        logger.FetchingPage(target.Host, target.AbsolutePath);
        using var response = await HttpRetry.SendAsync(
            async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target);
                if (cookies.ClearanceUserAgent is { } userAgent)
                {
                    request.Headers.UserAgent.Clear();
                    request.Headers.UserAgent.ParseAdd(userAgent);
                }

                return await client.SendAsync(request, token);
            },
            logger,
            "Fur Affinity request",
            cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        var responseUri = response.RequestMessage?.RequestUri ?? target;
        logger.ReceivedPage((int)response.StatusCode, responseUri.Host, responseUri.AbsolutePath, html.Length);
        if (!useFlareSolverr || !IsCloudflareChallenge(response.StatusCode, html))
        {
            return (response.StatusCode, html, responseUri);
        }

        logger.SolverFallback(target.Host, target.AbsolutePath, (int)response.StatusCode);
        var solved = await FlareSolverr.FetchPageAsync(solverClient, target, cookies.Container, cancellationToken);
        cookies.ImportSolverResult(solved.Cookies, solved.UserAgent);
        logger.ReceivedPage((int)solved.StatusCode, solved.FinalUri.Host, solved.FinalUri.AbsolutePath, solved.Html.Length);
        return (solved.StatusCode, solved.Html, solved.FinalUri);
    }

    private static bool IsCloudflareChallenge(HttpStatusCode status, string html) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable
        && (html.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-chl-", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase));

    private static HtmlDocument ParseDocument((HttpStatusCode StatusCode, string Html, Uri Uri) page)
    {
        if (IsLoginPage(page.Html) || page.Uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionExpiredException("FurAffinity redirected to login. Refresh cookies.txt and restart the container.");
        }

        if (page.StatusCode == HttpStatusCode.Forbidden)
        {
            var challengedByCloudflare = IsCloudflareChallenge(page.StatusCode, page.Html);
            var reason = challengedByCloudflare
                ? "FurAffinity's Cloudflare security check blocked the request. Check the User-Agent and network/IP; cookies alone cannot pass this challenge."
                : "FurAffinity denied the request. The session may be invalid, or the request may be blocked by FA's security rules.";
            throw new HttpRequestException($"FurAffinity returned HTTP 403 at {page.Uri.Host}{page.Uri.AbsolutePath}. {reason}");
        }

        if ((int)page.StatusCode is < 200 or >= 300)
        {
            throw new HttpRequestException($"FurAffinity returned HTTP {(int)page.StatusCode} at {page.Uri.Host}{page.Uri.AbsolutePath}.");
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
        var primaryAnchor = anchors.FirstOrDefault(anchor =>
                IsPrimaryHref(type, anchor.GetAttributeValue("href", string.Empty))
                && NormalizeText(anchor.InnerText) is not null)
            ?? anchors.FirstOrDefault(anchor => IsPrimaryHref(type, anchor.GetAttributeValue("href", string.Empty)));
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

        // Watch rows link the avatar image, so the name sits next to it as plain text.
        actorName ??= NormalizeText(row.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' info ')]/span")?.InnerText)
            ?? GetUserNameFromHref(actorAnchor);
        var actorUrl = actorAnchor is not null && TryGetFaUri(pageUri, actorAnchor.GetAttributeValue("href", string.Empty), out var parsedActorUrl)
            ? parsedActorUrl.ToString()
            : null;
        var images = row.SelectNodes(".//img[@src or @data-src]")?.ToArray() ?? [];
        var icon = images.FirstOrDefault(image => IsAvatarImage(GetImageSource(image), actorName));
        var artwork = images.FirstOrDefault(image =>
        {
            var source = GetImageSource(image);
            return !IsAvatarImage(source, actorName) && !source.Contains("/themes/", StringComparison.OrdinalIgnoreCase);
        });
        var titleNode = row.SelectSingleNode(".//*[contains(concat(' ', normalize-space(@class), ' '), ' journal_subject ')]");
        var title = NormalizeText(titleNode?.InnerText)
            ?? FirstNonEmpty(
                primaryAnchor.GetAttributeValue("title", string.Empty),
                primaryAnchor.GetAttributeValue("aria-label", string.Empty),
                images.Select(image => image.GetAttributeValue("alt", string.Empty)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                primaryAnchor.InnerText,
                actorName,
                type);
        var rawDescription = GetDescription(row);
        var description = type switch
        {
            "watches" when actorName is not null => $"{actorName} is now watching you.",
            "favorites" when actorName is not null => $"{actorName} favorited {title}",
            "notes" when actorName is not null => $"New note from {actorName}.",
            "submissions" when GetRating(row) is { } rating => $"Rating: {rating}",
            _ => rawDescription
        };

        // The ID hashes the row's own text so rewording the message above does not change it.
        var id = CreateId(type, itemUri, actorName, rawDescription);
        return new NotificationItem(
            id,
            type,
            Limit(title, 240),
            Limit(description, 3500),
            itemUri.ToString(),
            actorName,
            actorUrl,
            GetImageUrl(artwork, pageUri),
            GetImageUrl(icon, pageUri) ?? GetAvatarUrl(actorAnchor),
            GetOccurredAt(row));
    }

    private static string? GetUserNameFromHref(HtmlNode? anchor)
    {
        var match = UserNamePattern().Match(anchor?.GetAttributeValue("href", string.Empty) ?? string.Empty);
        return match.Success ? Uri.UnescapeDataString(match.Groups["name"].Value) : null;
    }

    // Comment, favorite and note rows have no avatar markup, but FA serves the current one at /<username>.gif.
    private static string? GetAvatarUrl(HtmlNode? actorAnchor) =>
        GetUserNameFromHref(actorAnchor) is { } name ? $"https://a.furaffinity.net/{Uri.EscapeDataString(name.ToLowerInvariant())}.gif" : null;

    private static string? GetRating(HtmlNode row)
    {
        var match = RatingPattern().Match(row.GetAttributeValue("class", string.Empty));
        return match.Success ? char.ToUpperInvariant(match.Groups["rating"].Value[0]) + match.Groups["rating"].Value[1..].ToLowerInvariant() : null;
    }

    private static DateTimeOffset? GetOccurredAt(HtmlNode row)
    {
        var seconds = row.SelectSingleNode(".//*[@data-time]")?.GetAttributeValue("data-time", string.Empty);
        return long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds)
            : null;
    }

    private static string CreateId(string type, Uri itemUri, string? actorName, string description)
    {
        var stableId = type switch
        {
            "submissions" when MatchId(ViewIdPattern(), itemUri) is { } viewId => $"submissions:{viewId}",
            "journals" when MatchId(JournalIdPattern(), itemUri) is { } journalId => $"journals:{journalId}",
            "comments" when CommentIdPattern().Match(itemUri.Fragment) is { Success: true } comment => $"comments:{comment.Groups["id"].Value}",
            "notes" when NoteIdPattern().Match(itemUri.AbsolutePath) is { Success: true } note => $"notes:{note.Groups["id"].Value}",
            "favorites" when MatchId(ViewIdPattern(), itemUri) is { } favoritedViewId && !string.IsNullOrWhiteSpace(actorName)
                => $"favorites:{favoritedViewId}:{actorName.ToLowerInvariant()}",
            _ => null
        };

        if (stableId is not null)
        {
            return stableId;
        }

        var identity = $"{type}\n{itemUri.AbsoluteUri}\n{actorName}\n{description}";
        return $"{type}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12].ToLowerInvariant()}";
    }

    private static string? MatchId(Regex pattern, Uri uri)
    {
        var match = pattern.Match(uri.AbsolutePath);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static HtmlNodeCollection? GetRows(HtmlNode section, string type) => type switch
    {
        "submissions" => section.SelectNodes(".//figure[starts-with(@id, 'sid-') and .//a[contains(@href, '/view/')]]"),
        "notes" => section.SelectNodes(
            ".//*[contains(concat(' ', normalize-space(@class), ' '), ' c-noteListItem ')][.//a[contains(concat(' ', normalize-space(@class), ' '), ' notelink ') and contains(concat(' ', normalize-space(@class), ' '), ' note-unread ')]]"),
        _ => section.SelectNodes(".//ul[contains(concat(' ', normalize-space(@class), ' '), ' message-stream ')]/li")
    };

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

        return TryGetFaUri(pageUri, GetImageSource(image), out var uri) ? uri.ToString() : null;
    }

    private static string GetImageSource(HtmlNode image) =>
        image.GetAttributeValue("src", image.GetAttributeValue("data-src", string.Empty));

    private static string? GetTypeFromHref(string href) => NotificationTypes.All.FirstOrDefault(type =>
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

        return WhitespacePattern().Replace(HtmlEntity.DeEntitize(text), " ").Trim();
    }

    private static string FirstNonEmpty(params string?[] values) => values.Select(NormalizeText).FirstOrDefault(value => value is not null) ?? "FurAffinity notification";

    private static string Limit(string text, int maxLength) => text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    private static bool IsLoginPage(string html)
    {
        return html.Contains("<form", StringComparison.OrdinalIgnoreCase)
            && PasswordInputPattern().IsMatch(html);
    }
}
