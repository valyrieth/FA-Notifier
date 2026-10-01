namespace FaNotify.Configuration;

internal sealed record AppSettings(
    string? DiscordWebhookUrl,
    int PollIntervalMinutes = 30,
    int FailureAlertThreshold = 3,
    string[]? NotifyOn = null,
    string? NotificationPrefix = null,
    string? UserAgent = null,
    bool? UseFlareSolverr = null,
    string? LogLevel = "Information");
