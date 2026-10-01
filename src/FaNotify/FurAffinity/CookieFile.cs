using System.Globalization;
using System.Net;

namespace FaNotify.FurAffinity;

internal static class CookieFile
{
    public static LoadedCookies Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("FA cookie file not found. Mount your exported cookies.txt file into the container.", path);
        }

        var container = new CookieContainer();
        var loaded = 0;
        DateTimeOffset? sessionExpiry = null;
        DateTimeOffset? earliestExpiry = null;
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
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiry);
                cookie.Expires = expiresAt.UtcDateTime;
                if (cookie.Expired)
                {
                    continue;
                }

                earliestExpiry = earliestExpiry is { } earliest && earliest < expiresAt ? earliest : expiresAt;
                if (cookie.Name is "a" or "b")
                {
                    sessionExpiry = sessionExpiry is { } session && session < expiresAt ? session : expiresAt;
                }
            }

            container.Add(cookie);
            loaded++;
        }

        if (loaded == 0)
        {
            throw new InvalidDataException("No unexpired cookies were found in the Netscape cookie file.");
        }

        return new LoadedCookies(container, sessionExpiry ?? earliestExpiry);
    }
}
