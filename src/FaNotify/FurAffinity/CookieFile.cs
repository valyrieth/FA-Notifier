using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace FaNotify.FurAffinity;

internal static class CookieFile
{
    private const string SourceHashPrefix = "# source-sha256: ";

    public static CookieSession Load(string sourcePath, string refreshedPath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("FA cookie file not found. Mount your exported cookies.txt file into the container.", sourcePath);
        }

        var sourceBytes = File.ReadAllBytes(sourcePath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        var container = TryLoadRefreshed(refreshedPath, sourceHash) ?? Parse(Encoding.UTF8.GetString(sourceBytes).Split('\n'));
        return new CookieSession(container, refreshedPath, sourceHash);
    }

    public static void Save(string path, CookieContainer container, string sourceHash)
    {
        var lines = new List<string> { "# Netscape HTTP Cookie File", SourceHashPrefix + sourceHash };
        lines.AddRange(CookieSession.ActiveCookies(container).Select(Format));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{path}.tmp";
        File.WriteAllLines(temporaryPath, lines);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    // The renewed cookies only apply to the export they came from; replacing cookies.txt discards them.
    private static CookieContainer? TryLoadRefreshed(string path, string sourceHash)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var lines = File.ReadAllLines(path);
            return lines.Contains(SourceHashPrefix + sourceHash) ? Parse(lines) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    private static CookieContainer Parse(IEnumerable<string> allLines)
    {
        var container = new CookieContainer();
        var loaded = 0;
        foreach (var originalLine in allLines)
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

    private static string Format(Cookie cookie) => string.Join(
        '\t',
        (cookie.HttpOnly ? "#HttpOnly_" : string.Empty) + cookie.Domain,
        cookie.Domain.StartsWith('.') ? "TRUE" : "FALSE",
        cookie.Path,
        cookie.Secure ? "TRUE" : "FALSE",
        CookieSession.ExpiryUnixSeconds(cookie).ToString(CultureInfo.InvariantCulture),
        cookie.Name,
        cookie.Value);
}
