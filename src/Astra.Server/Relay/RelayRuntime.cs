using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;

namespace Astra.Server.Relay;

/// <summary>
/// What the pipeline asks of its host (PipelineContracts.cs), answered for one relay request: its route is the caller and
/// the template's provider, its credentials are the caller's own, its models come from the caller's model list, and the
/// finished request is a log line. Stored tokens, accounts, pools and smart routing do not exist here.
/// </summary>
internal sealed class RelayRuntime(IHttpContextAccessor accessor, RelayCatalog catalog, RelayOptions options, ILogger<RelayRuntime> logger)
    : IGatewayRouter, IUpstreamAuthResolver, IEffectiveModelResolver, IModelProtocolResolver, IGatewaySettings, IRequestSink, ISmartRouter
{
    private RelayCaller Caller => RelayCaller.Current(accessor);

    public AppSettings Current { get; } = new()
    {
        DebugBodies = false,
        UpdateAutoCheck = false,
        SubscriptionQuotaIntervalMinutes = 0,
        StreamIdleTimeoutSec = options.UpstreamIdleTimeoutSeconds,
    };

    public Task<GatewayRoute> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        var provider = Caller.Provider;
        // The pipeline names a caller on every route; this one is the request itself, not a stored token.
        var caller = new TokenRecord { Id = "request", Name = "request", ProviderId = provider.Id };
        return Task.FromResult(new GatewayRoute(caller, null, null, provider) { Targets = [new RouteTarget(provider, null)] });
    }

    public Task<GatewayRoute> ForModelAsync(GatewayRoute route, string modelId, bool isClaudeCode, CancellationToken ct) => Task.FromResult(route);

    public Task<UpstreamAuth?> ResolveAsync(string providerId, string? accountId, CancellationToken ct = default)
    {
        if (providerId != Caller.Provider.Id || accountId is not null) throw Absent("stored accounts");
        return Task.FromResult<UpstreamAuth?>(Caller.Auth with { CanRefresh = false });
    }

    /// <summary>No stored accounts: a balanced subscription falls back to the request's own credential.</summary>
    public Task<IReadOnlyList<ProviderAccount>> ListAccountsAsync(string providerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

    public Task<UpstreamAuth> ResolveAfterUnauthorizedAsync(string providerId, string? accountId, CancellationToken ct = default) =>
        throw Absent("credential renewal");

    public Task<string?> FailOverAsync(string providerId, string accountId, DateTimeOffset? cooldownUntil, string reason,
        IReadOnlyCollection<string> tried, bool setCurrent = true, CancellationToken ct = default) =>
        throw Absent("account failover");

    public async Task<ProviderModel> ResolveProviderModelAsync(Provider provider, string modelId, CancellationToken ct = default) =>
        (await catalog.GetAsync(Caller, ct)).GetValueOrDefault(modelId) ?? new ProviderModel { ProviderId = provider.Id, ModelId = modelId };

    public async Task<(ProviderModel Model, EffectiveModel Effective)> ResolveByModelIdAsync(
        Provider provider, string modelId, CancellationToken ct = default)
    {
        var model = await ResolveProviderModelAsync(provider, modelId, ct);
        return (model, ModelMerger.Merge(provider, model, null, null));
    }

    public Task<EffectiveModel> ResolveAsync(Provider provider, ProviderModel pm, CancellationToken ct = default) =>
        Task.FromResult(ModelMerger.Merge(provider, pm, null, null));

    public Task<IReadOnlyList<ApiProtocol>> ResolveAsync(Provider provider, ProviderModel model, string? accountId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ApiProtocol>>(model.UpstreamProtocols);

    public ValueTask EnqueueAsync(RequestRecord record)
    {
        logger.LogInformation("Request {Id}: status={Status} http={HttpStatus} inbound={Inbound} upstream={Upstream}",
            record.Id, record.Status, record.HttpStatus, record.InboundProtocol, record.UpstreamProtocol);
        return ValueTask.CompletedTask;
    }

    public bool IsVirtual(string model) => string.Equals(model, SmartRoutingSettings.DefaultVirtualModel, StringComparison.Ordinal);

    public Task<RoutingDecision> DecideAsync(RoutingInput input, CancellationToken ct) =>
        throw new GatewayException(400, "invalid_request_error", "中转没有智能路由，请使用 /v1/models 列出的模型。");

    public Task<(RouteTarget? Target, GradeProblem? Problem)> GradeUseAsync(
        GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct) => throw Absent("routing grades");

    public Task<RouteTarget?> GradeTargetAsync(GatewayRoute route, string? providerId, string modelId, bool isClaudeCode, CancellationToken ct) =>
        throw Absent("routing grades");

    private static InvalidOperationException Absent(string feature) => new($"The relay has no {feature}.");
}
