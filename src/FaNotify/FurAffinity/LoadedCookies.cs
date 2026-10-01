using System.Net;

namespace FaNotify.FurAffinity;

internal sealed record LoadedCookies(CookieContainer Container, DateTimeOffset? SessionExpiry);
