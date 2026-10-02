using System.Globalization;
using System.Net;

namespace FaNotify.FurAffinity;

internal sealed class CookieSession(CookieContainer container, string refreshedPath, string sourceHash, string? clearanceUserAgent = null)
{
    private string savedFingerprint = Fingerprint(container, clearanceUserAgent);

    public CookieContainer Container => container;

    // Cloudflare ties its clearance cookie to the browser that solved the challenge, so requests must reuse that User-Agent.
    public string? ClearanceUserAgent { get; private set; } = clearanceUserAgent;

    public void ImportSolverResult(IEnumerable<Cookie> solverCookies, string? userAgent)
    {
        foreach (var cookie in solverCookies.Where(cookie => IsFurAffinityDomain(cookie.Domain)))
        {
            container.Add(cookie);
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            ClearanceUserAgent = userAgent;
        }
    }

    // The a/b cookies are the login; fall back to the earliest-expiring cookie if they are absent.
    public DateTimeOffset? GetSessionExpiry()
    {
        var expiring = ActiveCookies(container).Where(cookie => cookie.Expires != DateTime.MinValue).ToArray();
        var login = expiring.Where(cookie => cookie.Name is "a" or "b").ToArray();
        var tracked = login.Length > 0 ? login : expiring;
        return tracked.Length == 0 ? null : tracked.Min(cookie => new DateTimeOffset(cookie.Expires));
    }

    public bool SaveIfChanged()
    {
        var fingerprint = Fingerprint(container, ClearanceUserAgent);
        if (fingerprint == savedFingerprint)
        {
            return false;
        }

        CookieFile.Save(refreshedPath, container, sourceHash, ClearanceUserAgent);
        savedFingerprint = fingerprint;
        return true;
    }

    internal static IEnumerable<Cookie> ActiveCookies(CookieContainer container) =>
        container.GetAllCookies().Where(cookie => !cookie.Expired);

    internal static long ExpiryUnixSeconds(Cookie cookie) =>
        cookie.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();

    private static bool IsFurAffinityDomain(string domain)
    {
        var host = domain.TrimStart('.');
        return host.Equals("furaffinity.net", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".furaffinity.net", StringComparison.OrdinalIgnoreCase);
    }

    private static string Fingerprint(CookieContainer container, string? userAgent) => string.Join(
        '\n',
        ActiveCookies(container)
            .OrderBy(cookie => cookie.Domain, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Path, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Name, StringComparer.Ordinal)
            .Select(cookie => string.Create(CultureInfo.InvariantCulture, $"{cookie.Domain}|{cookie.Path}|{cookie.Name}|{cookie.Value}|{ExpiryUnixSeconds(cookie)}"))
            .Append(userAgent ?? string.Empty));
}
