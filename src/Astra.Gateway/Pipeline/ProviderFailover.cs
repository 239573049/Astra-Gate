namespace Astra.Gateway.Pipeline;

/// <summary>What a failed upstream attempt means: whether another pool member should get the request, and the health report.</summary>
public readonly record struct AttemptVerdict(bool FailOver, ProviderOutcome Outcome);

/// <summary>
/// Classifies upstream results for provider pools (issue #2). A rate limit, rejected credentials, an unknown model, a
/// server error and a network failure or timeout before the response started are the provider's problem: the next
/// member gets the request. A 4xx about the request itself would fail anywhere, so it goes back to the client.
/// </summary>
public static class ProviderFailover
{
    public static AttemptVerdict Classify(int status) => status switch
    {
        >= 200 and < 300 => new(false, ProviderOutcome.Success),
        429 => new(true, ProviderOutcome.RateLimited),
        401 or 403 => new(true, ProviderOutcome.AuthRejected),
        // The model (or route) is missing on this provider only; it is up.
        404 => new(true, ProviderOutcome.Success),
        408 => new(true, ProviderOutcome.Timeout),
        >= 500 => new(true, ProviderOutcome.ServerError),
        _ => new(false, ProviderOutcome.Neutral),
    };

    /// <summary>
    /// An exception thrown while sending, before anything reached the client; null when it is not the provider's fault —
    /// above all when the client went away (<paramref name="ct"/> cancelled), which is never failed over.
    /// </summary>
    public static AttemptVerdict? Classify(Exception exception, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;
        return exception switch
        {
            HttpRequestException => new(true, ProviderOutcome.Unreachable),
            // SocketsHttpHandler's connect timeout (ct is not cancelled, so the client is still there).
            OperationCanceledException => new(true, ProviderOutcome.Timeout),
            // An account-based provider with no usable sign-in (SendAsync turns that into a 401).
            GatewayException { Status: 401 } => new(true, ProviderOutcome.AuthRejected),
            _ => null,
        };
    }
}
