using System.Globalization;
using FaNotify.Configuration;
using FaNotify.Discord;
using FaNotify.FurAffinity;
using FaNotify.Http;
using FaNotify.Logging;
using FaNotify.Notifications;
using FaNotify.Solver;
using FaNotify.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FaNotify.Hosting;

internal sealed class NotifierWorker(
    AppConfig config,
    CookieSession cookies,
    IHttpClientFactory httpClientFactory,
    ILogger<NotifierWorker> logger) : BackgroundService
{
    private const int RegisteredUserThreshold = 15000;
    private const int LowTrafficIntervalMinutes = 1;
    private static readonly TimeSpan CookieExpiryWarning = TimeSpan.FromHours(24);
    private static readonly TimeSpan HealthGracePeriod = TimeSpan.FromMinutes(5);

    private bool expiryLogged;
    private DateTimeOffset? loggedExpiry;
    private DateTimeOffset? alertedExpiry;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var faClient = httpClientFactory.CreateClient(HttpClientNames.Fa);
        var discordClient = httpClientFactory.CreateClient(HttpClientNames.Discord);
        var solverClient = httpClientFactory.CreateClient(HttpClientNames.Solver);
        var state = StateFile.Load(config.StateFile);
        var fastPolling = false;
        var startupStatusLogged = false;
        var failureAlertSent = false;
        var consecutiveFailures = 0;

        if (config.UseFlareSolverr)
        {
            await FlareSolverr.WaitUntilReadyAsync(solverClient, logger, stoppingToken);
        }

        logger.Started(config.IntervalMinutes);
        await CheckCookieExpiryAsync(discordClient, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.CheckStarting();
                var snapshot = await FaNotifications.FetchAsync(
                    faClient,
                    solverClient,
                    cookies,
                    config.UseFlareSolverr,
                    logger,
                    stoppingToken);
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.SnapshotReceived(
                        snapshot.RegisteredUsers,
                        string.Join(", ", snapshot.Counts.Select(pair => $"{pair.Key}={pair.Value}")),
                        snapshot.Items.Count,
                        snapshot.CountFallbacks.Count);
                }

                var wasFastPolling = fastPolling;
                fastPolling = snapshot.RegisteredUsers < RegisteredUserThreshold;
                LogPollingStatus(startupStatusLogged, wasFastPolling, fastPolling, snapshot.RegisteredUsers);
                startupStatusLogged = true;

                var newItems = CollectNewItems(snapshot, state);
                if (newItems.Count > 0)
                {
                    await DiscordWebhook.SendAsync(
                        discordClient,
                        logger,
                        config.WebhookUrl,
                        config.NotificationPrefix,
                        newItems,
                        delivered => RecordDelivered(state, delivered),
                        stoppingToken);
                    logger.SentItems(newItems.Count);
                }
                else
                {
                    logger.NoNewItems();
                }

                consecutiveFailures = 0;
                if (failureAlertSent
                    && await TrySendStatusAlertAsync(
                        discordClient,
                        "check-recovered",
                        "FurAffinity checks recovered",
                        "A FurAffinity check completed successfully. Polling has resumed.",
                        stoppingToken))
                {
                    failureAlertSent = false;
                }

                SaveRefreshedCookies();
                await CheckCookieExpiryAsync(discordClient, stoppingToken);
                WriteHealthFile();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.CheckFailed(exception.Message);
                consecutiveFailures++;
                if (failureAlertSent)
                {
                    logger.FailureAlertAlreadySent();
                }
                else if (exception is SessionExpiredException || consecutiveFailures >= config.FailureAlertThreshold)
                {
                    failureAlertSent = await TrySendStatusAlertAsync(
                        discordClient,
                        "check-failed",
                        "FurAffinity check failed",
                        "A FurAffinity check failed. Your session cookies may have expired. Refresh cookies.txt and check the application logs for details.",
                        stoppingToken);
                }
                else
                {
                    logger.FailureBelowThreshold(consecutiveFailures, config.FailureAlertThreshold);
                }
            }

            try
            {
                var interval = TimeSpan.FromMinutes(fastPolling ? LowTrafficIntervalMinutes : config.IntervalMinutes);
                var delay = interval + Jitter(interval);
                logger.NextCheck(delay.TotalMinutes);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static TimeSpan Jitter(TimeSpan interval) =>
        TimeSpan.FromSeconds(Random.Shared.NextDouble() * Math.Min(30, interval.TotalSeconds * 0.1));

    private void LogPollingStatus(bool statusAlreadyLogged, bool wasFastPolling, bool fastPolling, int registeredUsers)
    {
        if (!statusAlreadyLogged)
        {
            if (fastPolling)
            {
                logger.PollingStartedFast(registeredUsers, RegisteredUserThreshold);
            }
            else
            {
                logger.PollingStartedNormal(registeredUsers, config.IntervalMinutes);
            }
        }
        else if (wasFastPolling != fastPolling)
        {
            if (fastPolling)
            {
                logger.PollingSwitchedToFast(registeredUsers, RegisteredUserThreshold);
            }
            else
            {
                logger.PollingResumedNormal(registeredUsers, RegisteredUserThreshold, config.IntervalMinutes);
            }
        }
    }

    private List<NotificationItem> CollectNewItems(NotificationSnapshot snapshot, NotificationState state)
    {
        List<NotificationItem> newItems = [];
        foreach (var item in snapshot.Items)
        {
            if (!config.NotifyOn.Contains(item.Type))
            {
                logger.SkippingDisabledType(item.Type, item.Id);
                continue;
            }

            if (state.SeenItems.ContainsKey(item.Id))
            {
                logger.SkippingDelivered(item.Type, item.Id, item.Title);
                continue;
            }

            newItems.Add(item);
        }

        foreach (var type in snapshot.CountFallbacks)
        {
            var count = snapshot.Counts.GetValueOrDefault(type);
            var fallbackId = $"count:{type}:{count}";
            if (!config.NotifyOn.Contains(type))
            {
                logger.SkippingDisabledFallback(type);
                continue;
            }

            if (state.SeenItems.ContainsKey(fallbackId))
            {
                logger.SkippingDeliveredFallback(type, count);
                continue;
            }

            logger.CreatingFallback(type, count);
            newItems.Add(new NotificationItem(
                fallbackId,
                type,
                $"{char.ToUpperInvariant(type[0]) + type[1..]} notification count changed",
                $"There are {count} unread notifications. FA's item details were not recognized on this page.",
                snapshot.CategoryUrls.GetValueOrDefault(type, new Uri("https://www.furaffinity.net/")).ToString(),
                null,
                null,
                null));
        }

        return newItems;
    }

    private void SaveRefreshedCookies()
    {
        try
        {
            if (cookies.SaveIfChanged())
            {
                logger.CookiesRefreshed(config.RefreshedCookieFile);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.CookieSaveFailed(exception.Message);
        }
    }

    // The Docker HEALTHCHECK compares this timestamp with the clock, so it needs no knowledge of the poll interval.
    private void WriteHealthFile()
    {
        try
        {
            var healthyUntil = DateTimeOffset.UtcNow + (2 * TimeSpan.FromMinutes(config.IntervalMinutes)) + HealthGracePeriod;
            File.WriteAllText(config.HealthFile, healthyUntil.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.HealthFileFailed(exception.Message);
        }
    }

    private async Task CheckCookieExpiryAsync(HttpClient discordClient, CancellationToken cancellationToken)
    {
        var expiry = cookies.GetSessionExpiry();
        if (!expiryLogged || expiry != loggedExpiry)
        {
            expiryLogged = true;
            loggedExpiry = expiry;
            if (expiry is { } known)
            {
                logger.CookiesExpire(FormatExpiry(known), (known - DateTimeOffset.UtcNow).TotalDays);
            }
            else
            {
                logger.CookiesDoNotExpire();
            }
        }

        if (expiry is not { } expiresAt)
        {
            return;
        }

        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining > CookieExpiryWarning)
        {
            return;
        }

        var expiryText = FormatExpiry(expiresAt);
        var hours = Math.Max(0, remaining.TotalHours);
        logger.CookiesExpiringSoon(expiryText, hours);
        if (alertedExpiry == expiresAt)
        {
            return;
        }

        if (await TrySendStatusAlertAsync(
            discordClient,
            "cookies-expiring",
            "FurAffinity session cookies expire soon",
            string.Create(
                CultureInfo.InvariantCulture,
                $"The Fur Affinity session cookies expire on {expiryText}, in about {hours:0} hours. Run scripts/export-cookies.py (or export cookies.txt again), replace the file, and restart the container before then."),
            cancellationToken))
        {
            alertedExpiry = expiresAt;
        }
    }

    private static string FormatExpiry(DateTimeOffset expiry) =>
        expiry.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private void RecordDelivered(NotificationState state, IReadOnlyList<NotificationItem> delivered)
    {
        foreach (var item in delivered)
        {
            state.SeenItems[item.Id] = DateTimeOffset.UtcNow;
        }

        state.TrimSeenItems();
        StateFile.Save(config.StateFile, state);
        logger.StateSaved(state.SeenItems.Count);
    }

    private async Task<bool> TrySendStatusAlertAsync(HttpClient discordClient, string alertName, string title, string description, CancellationToken cancellationToken)
    {
        try
        {
            await DiscordWebhook.SendAsync(
                discordClient,
                logger,
                config.WebhookUrl,
                config.NotificationPrefix,
                [new NotificationItem($"status:{alertName}", "status", title, description, "https://www.furaffinity.net/", null, null, null)],
                null,
                cancellationToken);
            logger.StatusAlertSent(alertName);
            return true;
        }
        catch (Exception exception)
        {
            logger.StatusAlertFailed(alertName, exception.Message);
            return false;
        }
    }
}
