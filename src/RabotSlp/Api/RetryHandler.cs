using System.Net;
using Microsoft.Extensions.Logging;

namespace RabotSlp.Api;

/// <summary>
/// Retries throttled (429) and transiently failing requests. The SLP API throttles even at a
/// low request rate and sends a Retry-After header (in seconds), which is honoured here.
/// Only used for GET requests, so resending the same request is safe.
/// </summary>
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private readonly int _maxRetries;
    private readonly TimeSpan _defaultDelay;
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public RetryHandler(
        int maxRetries,
        TimeSpan defaultDelay,
        ILogger? logger = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _maxRetries = Math.Max(0, maxRetries);
        _defaultDelay = defaultDelay;
        _logger = logger;
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            TimeSpan delay;
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (!IsRetryable(response.StatusCode) || attempt >= _maxRetries)
                {
                    return response;
                }

                delay = GetDelay(response, attempt);
                _logger?.LogWarning(
                    "SLP API answered {StatusCode}, retry {Attempt}/{MaxRetries} in {Delay}.",
                    (int)response.StatusCode, attempt + 1, _maxRetries, delay);
                response.Dispose();
            }
            catch (HttpRequestException ex) when (attempt < _maxRetries)
            {
                delay = Backoff(attempt);
                _logger?.LogWarning(
                    ex, "SLP API request failed, retry {Attempt}/{MaxRetries} in {Delay}.",
                    attempt + 1, _maxRetries, delay);
            }

            await _delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests
        || status == HttpStatusCode.RequestTimeout
        || status == HttpStatusCode.InternalServerError
        || status == HttpStatusCode.BadGateway
        || status == HttpStatusCode.ServiceUnavailable
        || status == HttpStatusCode.GatewayTimeout;

    private TimeSpan GetDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        TimeSpan? requested = null;

        if (retryAfter?.Delta is { } delta)
        {
            requested = delta;
        }
        else if (retryAfter?.Date is { } date)
        {
            requested = date - DateTimeOffset.UtcNow;
        }

        if (requested is null)
        {
            return Backoff(attempt);
        }

        if (requested.Value < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return requested.Value > MaxDelay ? MaxDelay : requested.Value;
    }

    private TimeSpan Backoff(int attempt)
    {
        var factor = 1L << Math.Min(attempt, 5);
        var delay = TimeSpan.FromTicks(_defaultDelay.Ticks * factor);
        return delay > MaxDelay ? MaxDelay : delay;
    }
}
