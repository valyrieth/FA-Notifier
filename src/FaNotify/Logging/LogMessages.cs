using Microsoft.Extensions.Logging;

namespace FaNotify.Logging;

internal static partial class LogMessages
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Writing this run's log to {LogPath}.")]
    public static partial void WritingLogFile(this ILogger logger, string logPath);

    [LoggerMessage(Level = LogLevel.Error, Message = "Configuration loading failed: {Message}")]
    public static partial void ConfigurationFailed(this ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "FlareSolverr is ready.")]
    public static partial void SolverReady(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Waiting for FlareSolverr to start...")]
    public static partial void SolverWaiting(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "FA Notify started. Normal check interval: {Minutes} minutes.")]
    public static partial void Started(this ILogger logger, int minutes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting Fur Affinity check. Initial item sync: {InitialSync}.")]
    public static partial void CheckStarting(this ILogger logger, bool initialSync);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Snapshot received: registered users={Users:N0}; category counts=[{Counts}]; parsed items={Items}; count fallbacks={Fallbacks}.")]
    public static partial void SnapshotReceived(this ILogger logger, int users, string counts, int items, int fallbacks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fur Affinity reports {Users:N0} registered users online, below {Threshold:N0}. Polling every minute until the count reaches that number.")]
    public static partial void PollingStartedFast(this ILogger logger, int users, int threshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fur Affinity reports {Users:N0} registered users online. Polling every {Minutes} minutes.")]
    public static partial void PollingStartedNormal(this ILogger logger, int users, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fur Affinity reports {Users:N0} registered users online, below {Threshold:N0}. Switching to one-minute polling.")]
    public static partial void PollingSwitchedToFast(this ILogger logger, int users, int threshold);

    [LoggerMessage(Level = LogLevel.Information, Message = "Fur Affinity reports {Users:N0} registered users online, at or above {Threshold:N0}. Resuming the normal {Minutes}-minute polling interval.")]
    public static partial void PollingResumedNormal(this ILogger logger, int users, int threshold, int minutes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping disabled notification type={Type}, id={Id}.")]
    public static partial void SkippingDisabledType(this ILogger logger, string type, string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping previously delivered notification type={Type}, id={Id}, title=\"{Title}\".")]
    public static partial void SkippingDelivered(this ILogger logger, string type, string id, string title);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping count-only fallback for disabled notification type={Type}.")]
    public static partial void SkippingDisabledFallback(this ILogger logger, string type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping previously delivered count-only fallback type={Type}, count={Count}.")]
    public static partial void SkippingDeliveredFallback(this ILogger logger, string type, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Creating count-only fallback for type={Type}, count={Count}.")]
    public static partial void CreatingFallback(this ILogger logger, string type, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} new notification item(s).")]
    public static partial void SentItems(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "No new notifications.")]
    public static partial void NoNewItems(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Notification state saved: {Count} delivered item ID(s) retained.")]
    public static partial void StateSaved(this ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Check failed: {Message}")]
    public static partial void CheckFailed(this ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Check is still failing; the failure alert was already sent for this outage.")]
    public static partial void FailureAlertAlreadySent(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Next check in {Minutes} minute(s).")]
    public static partial void NextCheck(this ILogger logger, int minutes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Alert} alert to Discord.")]
    public static partial void StatusAlertSent(this ILogger logger, string alert);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not send {Alert} alert to Discord: {Message}")]
    public static partial void StatusAlertFailed(this ILogger logger, string alert, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No new {Type} count increase: current={Current}, previous={Previous}.")]
    public static partial void NoCountIncrease(this ILogger logger, string type, int current, int previous);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No category URL found for {Type}; using a count-only fallback.")]
    public static partial void NoCategoryUrl(this ILogger logger, string type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parsed no {Type} notification items from {Rows} row(s); using a count-only fallback.")]
    public static partial void ParsedNoItems(this ILogger logger, string type, int rows);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Parsed {Count} {Type} notification item(s).")]
    public static partial void ParsedItems(this ILogger logger, int count, string type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fetching Fur Affinity page {Host}{Path} via {Transport}.")]
    public static partial void FetchingPage(this ILogger logger, string host, string path, string transport);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received HTTP {Status} from {Host}{Path} ({Length} characters).")]
    public static partial void ReceivedPage(this ILogger logger, int status, string host, string path, int length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Sending Discord batch {Batch} with {Count} notification(s).")]
    public static partial void SendingBatch(this ILogger logger, int batch, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Discord accepted notification: type={Type}, id={Id}, title=\"{Title}\".")]
    public static partial void NotificationAccepted(this ILogger logger, string type, string id, string title);
}
