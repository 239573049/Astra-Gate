using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Gateway.Routing;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

// What GatewayPipeline asks of the host it runs in. The server answers from SQLite through the classes named on each
// contract; `astra-server relay` (Astra.Server/Relay) has no storage and answers per request, from the credential and
// template the request arrives with. The features a host may not have at all — the privacy guard, debug capture, token
// caps, provider pools, gateway middleware and plugin providers — are nullable constructor parameters instead.

/// <summary>Who is calling and where the request may go (<see cref="GatewayRouter"/>).</summary>
public interface IGatewayRouter
{
    Task<GatewayRoute> ResolveAsync(HttpRequest request, CancellationToken ct);

    Task<GatewayRoute> ForModelAsync(GatewayRoute route, string modelId, bool isClaudeCode, CancellationToken ct);

    Task<(RouteTarget? Target, GradeProblem? Problem)> GradeUseAsync(
        GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct);

    Task<RouteTarget?> GradeTargetAsync(GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct);
}

/// <summary>The upstream credentials of a provider and its accounts (<see cref="UpstreamAuthResolver"/>).</summary>
public interface IUpstreamAuthResolver
{
    Task<UpstreamAuth?> ResolveAsync(string providerId, string? accountId, CancellationToken ct = default);

    Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string providerId, CancellationToken ct = default);

    Task<UpstreamAuth> ResolveAfterUnauthorizedAsync(string providerId, string? accountId, CancellationToken ct = default);

    Task<string?> FailOverAsync(string providerId, string accountId, DateTimeOffset? cooldownUntil, string reason,
        IReadOnlyCollection<string> tried, bool setCurrent = true, CancellationToken ct = default);
}

/// <summary>A provider's model and its effective capabilities and prices (<see cref="EffectiveModelResolver"/>).</summary>
public interface IEffectiveModelResolver
{
    Task<ProviderModel> ResolveProviderModelAsync(Provider provider, string modelId, CancellationToken ct = default);

    Task<(ProviderModel Model, EffectiveModel Effective)> ResolveByModelIdAsync(Provider provider, string modelId, CancellationToken ct = default);

    Task<EffectiveModel> ResolveAsync(Provider provider, ProviderModel pm, CancellationToken ct = default);
}

/// <summary>The runtime settings a request reads (<see cref="SettingsService"/>).</summary>
public interface IGatewaySettings
{
    AppSettings Current { get; }
}

/// <summary>Where a finished request goes: the request log, usage and cost (<see cref="UsageWriter"/>).</summary>
public interface IRequestSink
{
    ValueTask EnqueueAsync(RequestRecord record);
}

/// <summary>Smart routing's virtual model (<see cref="SmartRouter"/>).</summary>
public interface ISmartRouter
{
    /// <summary>Whether <paramref name="model"/> is the virtual model, switched on or not.</summary>
    bool IsVirtual(string model);

    /// <exception cref="GatewayException">The virtual model cannot be served (smart routing is off or absent).</exception>
    Task<RoutingDecision> DecideAsync(RoutingInput input, CancellationToken ct);
}

/// <summary>
/// How large a request body may be — as sent, and once its <c>Content-Encoding</c> is undone — and how long reading it
/// may take. The server keeps <see cref="Default"/>; the relay sets its own.
/// </summary>
public sealed record RequestBodyLimits(long MaxBytes, TimeSpan? ReadTimeout = null)
{
    public static RequestBodyLimits Default { get; } = new(GatewayPipeline.MaxRequestBytes);
}
