using System.Net;
using FaNotify.Logging;
using Microsoft.Extensions.Logging;

namespace FaNotify.Http;

internal static class HttpRetry
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(60);

    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        ILogger logger,
        string operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan delay;
            string reason;
            try
            {
                var response = await send(cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                {
                    return response;
                }

                delay = GetDelay(response, attempt);
                reason = $"HTTP {(int)response.StatusCode}";
                response.Dispose();
            }
            catch (Exception exception) when (
                (exception is HttpRequestException or TaskCanceledException)
                && !cancellationToken.IsCancellationRequested
                && attempt < MaxAttempts)
            {
                delay = Backoff(attempt);
                reason = exception.Message;
            }

            logger.RetryingRequest(operation, reason, attempt, MaxAttempts, delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static TimeSpan GetDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var requested = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        var seconds = Math.Clamp((requested ?? Backoff(attempt)).TotalSeconds, MinimumDelay.TotalSeconds, MaximumDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));
}
