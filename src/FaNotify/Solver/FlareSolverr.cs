using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FaNotify.Logging;
using Microsoft.Extensions.Logging;

namespace FaNotify.Solver;

internal static class FlareSolverr
{
    private static readonly Uri ServiceUrl = new("http://flaresolverr:8191/v1");

    public static async Task WaitUntilReadyAsync(HttpClient client, ILogger logger, CancellationToken cancellationToken)
    {
        var serviceRoot = new Uri(ServiceUrl, "/");
        for (var attempt = 0; attempt < 24; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(serviceRoot, cancellationToken);
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
