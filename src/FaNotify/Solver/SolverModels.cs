namespace FaNotify.Solver;

internal sealed record SolverRequest(string Cmd, string Url, int MaxTimeout, bool DisableMedia, SolverCookie[] Cookies);

internal sealed record SolverCookie(string Name, string Value, string Domain, string Path, bool Secure, bool HttpOnly);
