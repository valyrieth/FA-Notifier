using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaNotify.Logging;
using FaNotify.Serialization;
using Microsoft.Extensions.Logging;

namespace FaNotify.Solver;

internal static class FlareSolverr
{
    public static async Task WaitUntilReadyAsync(HttpClient client, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                using var response = await client.GetAsync("/", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    logger.SolverReady();
                    return;
                }
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            logger.SolverWaiting();
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException("FlareSolverr did not become ready. Start it with the Docker Compose solver profile.");
    }

    public static async Task<SolverPage> FetchPageAsync(
        HttpClient client,
        Uri target,
        CookieContainer cookieContainer,
        CancellationToken cancellationToken)
    {
        var cookies = cookieContainer.GetCookies(target)
            .Cast<Cookie>()
            .Select(cookie => new SolverCookie(cookie.Name, cookie.Value, cookie.Domain, cookie.Path, cookie.Secure, cookie.HttpOnly))
            .ToArray();

        var request = new SolverRequest("request.get", target.AbsoluteUri, 60000, true, cookies);

        using var response = await client.PostAsJsonAsync(string.Empty, request, AppJsonContext.Default.SolverRequest, cancellationToken);
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
        var userAgent = solution.TryGetProperty("userAgent", out var agent) ? agent.GetString() : null;

        return new SolverPage(statusCode, html, finalUri, ReadCookies(solution), userAgent);
    }

    private static List<Cookie> ReadCookies(JsonElement solution)
    {
        List<Cookie> cookies = [];
        if (!solution.TryGetProperty("cookies", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return cookies;
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = ReadString(item, "name");
            var domain = ReadString(item, "domain");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(domain))
            {
                continue;
            }

            try
            {
                var cookie = new Cookie(name, ReadString(item, "value") ?? string.Empty, ReadString(item, "path") ?? "/", domain)
                {
                    Secure = item.TryGetProperty("secure", out var secure) && secure.ValueKind == JsonValueKind.True,
                    HttpOnly = item.TryGetProperty("httpOnly", out var httpOnly) && httpOnly.ValueKind == JsonValueKind.True
                };

                // FlareSolverr reports expiry in seconds under "expiry" or "expires"; session cookies are absent or <= 0.
                var seconds = ReadSeconds(item, "expiry") ?? ReadSeconds(item, "expires");
                if (seconds is > 0)
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeSeconds((long)seconds.Value).UtcDateTime;
                }

                cookies.Add(cookie);
            }
            catch (Exception exception) when (exception is CookieException or ArgumentException)
            {
                // A cookie .NET rejects (for example an invalid name) is simply not reused.
            }
        }

        return cookies;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? ReadSeconds(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
