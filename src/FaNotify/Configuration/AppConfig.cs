using System.Globalization;
using System.Text.Json;
using FaNotify.Notifications;
using Microsoft.Extensions.Logging;

namespace FaNotify.Configuration;

internal sealed record AppConfig(
    string CookieFile,
    string StateFile,
    string HealthFile,
    string WebhookUrl,
    string UserAgent,
    string NotificationPrefix,
    int IntervalMinutes,
    int FailureAlertThreshold,
    LogLevel LogLevel,
    bool UseFlareSolverr,
    HashSet<string> NotifyOn)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static AppConfig Load()
    {
        var path = Environment.GetEnvironmentVariable("FA_NOTIFY_CONFIG") ?? "/app/settings.json";
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Settings file contains invalid JSON.");

        if (!Uri.TryCreate(settings.DiscordWebhookUrl, UriKind.Absolute, out var webhookUri) || webhookUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Set discordWebhookUrl in settings.json to your HTTPS Discord webhook URL.");
        }

        if (settings.PollIntervalMinutes < 1)
        {
            throw new InvalidOperationException("pollIntervalMinutes must be a positive whole number.");
        }

        if (settings.FailureAlertThreshold < 1)
        {
            throw new InvalidOperationException("failureAlertThreshold must be a positive whole number.");
        }

        var logLevelText = settings.LogLevel?.Trim() ?? "Information";
        if (!Enum.TryParse<LogLevel>(logLevelText, ignoreCase: true, out var logLevel)
            || !Enum.IsDefined(logLevel)
            || int.TryParse(logLevelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            throw new InvalidOperationException("logLevel must be one of: Trace, Debug, Information, Warning, Error, Critical, None.");
        }

        var solverSetting = Environment.GetEnvironmentVariable("FA_USE_FLARESOLVERR") ?? "false";
        var notifyOn = (settings.NotifyOn ?? NotificationTypes.All)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalidTypes = notifyOn.Except(NotificationTypes.All, StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalidTypes.Length > 0)
        {
            throw new InvalidOperationException($"Unknown notification type(s): {string.Join(", ", invalidTypes)}.");
        }

        return new AppConfig(
            "/app/cookies.txt",
            "/data/notifications.json",
            "/data/healthy-until",
            webhookUri.ToString(),
            settings.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:157.0) Gecko/20100101 Firefox/157.0",
            settings.NotificationPrefix ?? string.Empty,
            settings.PollIntervalMinutes,
            settings.FailureAlertThreshold,
            logLevel,
            settings.UseFlareSolverr ?? false,
            notifyOn);
    }

    private sealed record Settings(
        string? DiscordWebhookUrl,
        int PollIntervalMinutes = 30,
        int FailureAlertThreshold = 3,
        string[]? NotifyOn = null,
        string? NotificationPrefix = null,
        string? UserAgent = null,
        bool? UseFlareSolverr = null,
        string? LogLevel = "Information");
}
