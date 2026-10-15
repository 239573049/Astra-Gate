using Astra.Core.Tokens;

namespace Astra.Core.Requests;

/// <summary>
/// Why a request failed, in terms a token holder can be shown (the self-service request log): derived from the logged
/// status, HTTP status and error type, never from the error message — gateway messages name providers and accounts,
/// which a holder must not see (.agents/memory/key-self-service-requested-model.md).
/// </summary>
public static class RequestErrorCategories
{
    /// <summary>The client went away.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Stopped by the privacy guard or a middleware plugin.</summary>
    public const string Blocked = "blocked";

    /// <summary>Refused by the token's own budgets or RPM cap.</summary>
    public const string Limit = "limit";

    public const string Timeout = "timeout";

    /// <summary>The upstream (or every provider of a pool) was rate limited.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The key or its client is not allowed (disabled token, client not allowed).</summary>
    public const string Auth = "auth";

    /// <summary>The upstream could not serve it: down, overloaded, unreachable, or its own credentials rejected.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The request itself was rejected (bad body, unknown model, too large).</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>A protocol conversion Astra does not support.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>Astra itself failed.</summary>
    public const string Internal = "internal";

    public const string Other = "other";

    public static readonly IReadOnlyList<string> All =
        [Cancelled, Blocked, Limit, Timeout, RateLimited, Auth, Unavailable, InvalidRequest, Unsupported, Internal, Other];

    /// <summary>The category of a logged request; null for a success.</summary>
    public static string? Of(string? status, int? httpStatus, string? errorType)
    {
        if (status == RequestStatus.Success) return null;
        if (status == RequestStatus.ClientCancelled) return Cancelled;
        if (status == RequestStatus.Blocked) return Blocked;
        if (errorType == TokenLimits.RefusedErrorType) return Limit;
        if (errorType == "timeout" || httpStatus is 408 or 504) return Timeout;
        if (httpStatus == 429) return RateLimited;
        // Refused by the gateway: this key's own permission; refused by the upstream: the provider's credentials.
        if (httpStatus is 401 or 403) return status == RequestStatus.GatewayError ? Auth : Unavailable;
        if (httpStatus is 400 or 404 or 413 or 415 or 422) return InvalidRequest;
        if (httpStatus == 501) return Unsupported;
        if (errorType == "upstream_unreachable" || httpStatus is >= 500 && status == RequestStatus.UpstreamError || httpStatus is 502 or 503 or 529)
            return Unavailable;
        if (status == RequestStatus.GatewayError && httpStatus is null or >= 500) return Internal;
        return Other;
    }
}
