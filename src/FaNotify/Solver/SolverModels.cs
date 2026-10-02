using System.Net;

namespace FaNotify.Solver;

internal sealed record SolverPage(HttpStatusCode StatusCode, string Html, Uri FinalUri, IReadOnlyList<Cookie> Cookies, string? UserAgent);

internal sealed record SolverRequest(string Cmd, string Url, int MaxTimeout, bool DisableMedia, SolverCookie[] Cookies);

internal sealed record SolverCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly);
