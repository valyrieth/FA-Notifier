using System.Globalization;
using System.Net;

namespace FaNotify.FurAffinity;

internal sealed class CookieSession(CookieContainer container, string refreshedPath, string sourceHash)
{
    private string savedFingerprint = Fingerprint(container);

    public CookieContainer Container => container;

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
        var fingerprint = Fingerprint(container);
        if (fingerprint == savedFingerprint)
        {
            return false;
        }

        CookieFile.Save(refreshedPath, container, sourceHash);
        savedFingerprint = fingerprint;
        return true;
    }

    internal static IEnumerable<Cookie> ActiveCookies(CookieContainer container) =>
        container.GetAllCookies().Where(cookie => !cookie.Expired);

    internal static long ExpiryUnixSeconds(Cookie cookie) =>
        cookie.Expires == DateTime.MinValue ? 0 : new DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();

    private static string Fingerprint(CookieContainer container) => string.Join(
        '\n',
        ActiveCookies(container)
            .OrderBy(cookie => cookie.Domain, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Path, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Name, StringComparer.Ordinal)
            .Select(cookie => string.Create(CultureInfo.InvariantCulture, $"{cookie.Domain}|{cookie.Path}|{cookie.Name}|{cookie.Value}|{ExpiryUnixSeconds(cookie)}")));
}
