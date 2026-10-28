using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Accounts;

/// <summary>
/// The queries and writes of a provider's accounts (web/src/api/hooks.ts "subscription accounts" and the plugin-provider sign-in hooks),
/// with the same keys, data types and refresh rules, so every package that reads <c>['provider-accounts', id]</c> shares one cache entry:
/// <list type="bullet">
/// <item><c>['provider-accounts', id]</c> = <c>List&lt;ProviderAccountDto&gt;</c>; <c>['provider-usage', id]</c> = <see cref="ProviderUsageDto"/> (polled every 5 s);</item>
/// <item><c>['subscription-policy', id]</c>, <c>['account-import-info', id]</c> (never stale), <c>['local-codex-login']</c>, <c>['local-copilot-login']</c>, <c>['reset-credits', account]</c>;</item>
/// <item><c>['plugin-methods', p]</c>, <c>['plugin-sign-in', p, flow]</c> (polled every 1.5 s while waiting), <c>['plugin-usage', p, account]</c>, <c>['plugin-checkin', p]</c>.</item>
/// </list>
/// Writes follow the web's cache updates: the switching endpoints answer with the whole list, which replaces the cached one; the others
/// invalidate accounts and usage.
/// </summary>
public sealed class AccountsApi(ApiClient api, QueryClient queries)
{
    private static string P(string id) => "/api/providers/" + ApiClient.Enc(id);
    private static string A(string id) => "/api/provider-accounts/" + ApiClient.Enc(id);
    private static string Plugin(string id) => "/api/plugin-providers/" + ApiClient.Enc(id);

    /// <summary>The API client.</summary>
    public ApiClient Client => api;

    /// <summary>The query cache.</summary>
    public QueryClient Queries => queries;

    /// <summary>Key of a provider's accounts.</summary>
    public static QueryKey AccountsKey(string providerId) => QueryKey.Of("provider-accounts", providerId);

    /// <summary>Key of a provider's per-account usage.</summary>
    public static QueryKey UsageKey(string providerId) => QueryKey.Of("provider-usage", providerId);

    /// <summary>Key of a provider's subscription policy.</summary>
    public static QueryKey PolicyKey(string providerId) => QueryKey.Of("subscription-policy", providerId);

    /// <summary><c>GET /api/providers/{id}/accounts</c>.</summary>
    public Query<List<ProviderAccountDto>> Accounts(string providerId) => queries.Query(AccountsKey(providerId),
        async ct => await api.GetAsync<List<ProviderAccountDto>>(P(providerId) + "/accounts", ct) ?? []);

    /// <summary><c>GET /api/providers/{id}/usage</c>, every 5 s while shown.</summary>
    public Query<ProviderUsageDto> Usage(string providerId) => queries.Query(UsageKey(providerId),
        async ct => await api.GetAsync<ProviderUsageDto>(P(providerId) + "/usage", ct) ?? new ProviderUsageDto("", "", []),
        new QueryOptions<ProviderUsageDto> { RefetchInterval = _ => TimeSpan.FromSeconds(5) });

    /// <summary><c>GET /api/providers/{id}/subscription-policy</c>.</summary>
    public Query<SubscriptionPolicyDto> Policy(string providerId) => queries.Query(PolicyKey(providerId),
        async ct => await api.GetAsync<SubscriptionPolicyDto>(P(providerId) + "/subscription-policy", ct)
            ?? throw new ApiException(0, "empty policy"));

    /// <summary><c>GET /api/providers/{id}/accounts/import-info</c> (static per provider: never stale).</summary>
    public Query<AccountImportInfoDto> ImportInfo(string providerId) => queries.Query(QueryKey.Of("account-import-info", providerId),
        async ct => await api.GetAsync<AccountImportInfoDto>(P(providerId) + "/accounts/import-info", ct) ?? new AccountImportInfoDto(false, false, [], 0),
        new QueryOptions<AccountImportInfoDto> { StaleTime = TimeSpan.MaxValue });

    /// <summary><c>GET /api/subscription/codex/local-login</c> (no retry).</summary>
    public Query<LocalCodexLoginDto> LocalCodex() => queries.Query(QueryKey.Of("local-codex-login"),
        async ct => await api.GetAsync<LocalCodexLoginDto>("/api/subscription/codex/local-login", ct) ?? new LocalCodexLoginDto(false, null, null, null),
        new QueryOptions<LocalCodexLoginDto> { Retries = 0 });

    /// <summary><c>GET /api/subscription/copilot/local-login</c> (no retry).</summary>
    public Query<LocalCopilotLoginDto> LocalCopilot() => queries.Query(QueryKey.Of("local-copilot-login"),
        async ct => await api.GetAsync<LocalCopilotLoginDto>("/api/subscription/copilot/local-login", ct) ?? new LocalCopilotLoginDto(false, null, null),
        new QueryOptions<LocalCopilotLoginDto> { Retries = 0 });

    /// <summary><c>GET /api/provider-accounts/{id}/reset-credits</c> (live list, no retry).</summary>
    public Query<JsonObject> ResetCredits(string accountId) => queries.Query(QueryKey.Of("reset-credits", accountId),
        async ct => await api.GetAsync<JsonObject>(A(accountId) + "/reset-credits", ct) ?? [],
        new QueryOptions<JsonObject> { Retries = 0 });

    // ---- writes (subscription) ----

    private void InvalidateAccounts(string providerId)
    {
        queries.Invalidate(AccountsKey(providerId));
        queries.Invalidate(UsageKey(providerId));
    }

    // The switching endpoints answer with the provider's whole (re-ordered, re-flagged) account list.
    private void SetList(string providerId, List<ProviderAccountDto>? list)
    {
        if (list is not null) queries.SetData(AccountsKey(providerId), list);
        queries.Invalidate(UsageKey(providerId));
    }

    /// <summary><c>POST /api/provider-accounts/{id}/activate</c>: it becomes the provider's current account.</summary>
    public async Task ActivateAsync(string providerId, string accountId)
        => SetList(providerId, await api.PostAsync<List<ProviderAccountDto>>(A(accountId) + "/activate"));

    /// <summary><c>PATCH /api/provider-accounts/{id}</c> (enabled, name, own proxy; "" clears the proxy).</summary>
    public async Task UpdateAsync(string providerId, string accountId, AccountPatch patch)
        => SetList(providerId, await api.PatchAsync<AccountPatch, List<ProviderAccountDto>>(A(accountId), patch));

    /// <summary><c>PUT /api/providers/{id}/accounts/order</c>: the new failover order (first is tried first).</summary>
    public async Task ReorderAsync(string providerId, List<string> ids)
        => SetList(providerId, await api.PutAsync<AccountOrder, List<ProviderAccountDto>>(P(providerId) + "/accounts/order", new AccountOrder(ids)));

    /// <summary><c>POST /api/provider-accounts/{id}/refresh</c>: refreshes the login's token.</summary>
    public async Task RefreshTokenAsync(string providerId, string accountId)
    {
        await api.PostAsync<ProviderAccountDto>(A(accountId) + "/refresh");
        InvalidateAccounts(providerId);
    }

    /// <summary><c>POST /api/provider-accounts/{id}/quota</c>: fetches the upstream quota (501 when the family has no probe).</summary>
    public async Task FetchQuotaAsync(string providerId, string accountId)
    {
        await api.PostAsync<ProviderAccountDto>(A(accountId) + "/quota");
        InvalidateAccounts(providerId);
    }

    /// <summary><c>DELETE /api/provider-accounts/{id}</c>.</summary>
    public async Task DeleteAsync(string providerId, string accountId)
    {
        await api.DeleteAsync(A(accountId));
        InvalidateAccounts(providerId);
    }

    /// <summary><c>PUT /api/providers/{id}/subscription-policy</c>; the provider's settings JSON holds it, so the provider is refetched too.</summary>
    public async Task UpdatePolicyAsync(string providerId, SubscriptionPolicyPatch patch)
    {
        var policy = await api.PutAsync<SubscriptionPolicyPatch, SubscriptionPolicyDto>(P(providerId) + "/subscription-policy", patch);
        if (policy is not null) queries.SetData(PolicyKey(providerId), policy);
        queries.Invalidate(QueryKey.Of("provider", providerId));
    }

    /// <summary><c>POST /api/providers/{id}/accounts/login</c>: <paramref name="accountId"/> logs that account in again, <paramref name="httpProxy"/> is the new account's own proxy.</summary>
    public async Task<LoginStart> StartLoginAsync(string providerId, string? accountId, string? httpProxy)
    {
        var body = new LoginOptions(string.IsNullOrEmpty(accountId) ? null : accountId, string.IsNullOrWhiteSpace(httpProxy) ? null : httpProxy.Trim());
        var answer = await api.PostAsync<LoginOptions, JsonObject>(P(providerId) + "/accounts/login", body) ?? [];
        return LoginStart.Parse(answer);
    }

    /// <summary><c>POST …/accounts/login/{state}/poll</c>.</summary>
    public async Task<LoginPoll> PollLoginAsync(string providerId, string state)
        => LoginPoll.Parse(await api.PostAsync<JsonObject>(P(providerId) + "/accounts/login/" + ApiClient.Enc(state) + "/poll") ?? []);

    /// <summary><c>POST …/accounts/login/{state}/complete</c> with the code the authorization page showed (Claude).</summary>
    public async Task<LoginPoll> CompleteLoginAsync(string providerId, string state, string code)
        => LoginPoll.Parse(await api.PostAsync<CompleteLoginRequest, JsonObject>(P(providerId) + "/accounts/login/" + ApiClient.Enc(state) + "/complete", new CompleteLoginRequest(code)) ?? []);

    /// <summary><c>POST …/accounts/import-codex</c>: adopts the machine's codex login.</summary>
    public async Task<ImportedAccountDto?> ImportCodexAsync(string providerId)
    {
        var r = await api.PostAsync<JsonObject, ImportedAccountDto>(P(providerId) + "/accounts/import-codex", []);
        InvalidateAccounts(providerId);
        return r;
    }

    /// <summary><c>POST …/accounts/import-copilot</c> with no token: probes this machine (the consent is the click).</summary>
    public async Task<ImportedAccountDto?> ImportCopilotAsync(string providerId)
    {
        var r = await api.PostAsync<LocalCopilotImport, ImportedAccountDto>(P(providerId) + "/accounts/import-copilot", new LocalCopilotImport(null, null));
        InvalidateAccounts(providerId);
        queries.Invalidate(QueryKey.Of("local-copilot-login"));
        return r;
    }

    /// <summary><c>POST …/accounts/import</c>: a dry run only classifies and de-duplicates (nothing is written, nothing refetched).</summary>
    public async Task<BatchImportResultDto> ImportAsync(string providerId, AccountImportRequest request)
    {
        var r = await api.PostAsync<AccountImportRequest, BatchImportResultDto>(P(providerId) + "/accounts/import", request) ?? new BatchImportResultDto([], 0, 0, 0);
        if (request.DryRun != true) InvalidateAccounts(providerId);
        return r;
    }

    /// <summary><c>POST /api/provider-accounts/{id}/reset-credits/{credit}/consume</c>; the answer carries the refreshed quota.</summary>
    public async Task ConsumeResetCreditAsync(string providerId, string accountId, string creditId)
    {
        await api.PostAsync<JsonObject, ResetCreditResultDto>(A(accountId) + "/reset-credits/" + ApiClient.Enc(creditId) + "/consume", []);
        InvalidateAccounts(providerId);
        queries.Invalidate(QueryKey.Of("reset-credits", accountId));
    }

    // ---- plugin providers ----

    /// <summary><c>GET /api/plugin-providers/{p}/methods</c>.</summary>
    public Query<List<PluginAuthMethodDto>> PluginMethods(string providerId) => queries.Query(QueryKey.Of("plugin-methods", providerId),
        async ct => await api.GetAsync<List<PluginAuthMethodDto>>(Plugin(providerId) + "/methods", ct) ?? []);

    /// <summary>A browser sign-in, followed every 1.5 s until it is done or failed; reaching done refetches the accounts.</summary>
    public Query<PluginSignInDto> PluginSignIn(string providerId, string flow) => queries.Query(QueryKey.Of("plugin-sign-in", providerId, flow),
        async ct =>
        {
            var s = await api.GetAsync<PluginSignInDto>(Plugin(providerId) + "/sign-in/" + ApiClient.Enc(flow), ct)
                ?? throw new ApiException(0, "empty sign-in");
            if (s.State == "done") InvalidatePlugin(providerId);
            return s;
        },
        new QueryOptions<PluginSignInDto> { RefetchInterval = s => s is null || s.State == "waiting" ? TimeSpan.FromMilliseconds(1500) : null });

    /// <summary><c>GET /api/plugin-providers/{p}/accounts/{a}/usage</c> (fresh for a minute, no retry).</summary>
    public Query<JsonObject> PluginUsage(string providerId, string accountId) => queries.Query(QueryKey.Of("plugin-usage", providerId, accountId),
        async ct => await api.GetAsync<JsonObject>(Plugin(providerId) + "/accounts/" + ApiClient.Enc(accountId) + "/usage", ct) ?? [],
        new QueryOptions<JsonObject> { StaleTime = TimeSpan.FromMinutes(1), Retries = 0 });

    /// <summary><c>GET /api/plugin-providers/{p}/checkin</c>.</summary>
    public Query<PluginCheckinStateDto> PluginCheckin(string providerId) => queries.Query(QueryKey.Of("plugin-checkin", providerId),
        async ct => await api.GetAsync<PluginCheckinStateDto>(Plugin(providerId) + "/checkin", ct) ?? new PluginCheckinStateDto(false, false, []));

    private void InvalidatePlugin(string providerId)
    {
        InvalidateAccounts(providerId);
        queries.Invalidate(QueryKeys.Providers);
    }

    /// <summary><c>POST …/prompt</c>: the method's next question given the answers so far (null: none left).</summary>
    public async Task<PluginPrompt?> PluginPromptAsync(string providerId, int method, IReadOnlyDictionary<string, string> inputs)
    {
        var r = await api.PostAsync<PluginPromptInput, JsonObject>(Plugin(providerId) + "/prompt", new PluginPromptInput(method, ToJson(inputs)));
        return PluginPrompt.Parse(r?["prompt"]);
    }

    /// <summary><c>POST …/validate</c>: the plugin's word on one answer (null = fine).</summary>
    public async Task<string?> PluginValidateAsync(string providerId, int method, string key, string value)
    {
        var r = await api.PostAsync<PluginValidateInput, JsonObject>(Plugin(providerId) + "/validate", new PluginValidateInput(method, key, value));
        return AccountQuota.Str(r?["error"]);
    }

    /// <summary><c>POST …/sign-in</c>: starts a browser sign-in.</summary>
    public async Task<PluginSignInDto> StartPluginSignInAsync(string providerId, int method, IReadOnlyDictionary<string, string> inputs)
        => await api.PostAsync<PluginSignInInput, PluginSignInDto>(Plugin(providerId) + "/sign-in", new PluginSignInInput(method, ToJson(inputs), null))
            ?? throw new ApiException(0, "empty sign-in");

    /// <summary><c>POST …/sign-in/{flow}/code</c>: the code the page showed; the answer replaces the followed state.</summary>
    public async Task<PluginSignInDto?> SubmitPluginCodeAsync(string providerId, string flow, string code)
    {
        var s = await api.PostAsync<PluginCodeInput, PluginSignInDto>(Plugin(providerId) + "/sign-in/" + ApiClient.Enc(flow) + "/code", new PluginCodeInput(code));
        if (s is not null)
        {
            queries.SetData(QueryKey.Of("plugin-sign-in", providerId, s.Id), s);
            if (s.State == "done") InvalidatePlugin(providerId);
        }
        return s;
    }

    /// <summary><c>POST …/api-key</c>: signs in with a key.</summary>
    public async Task PluginApiKeyAsync(string providerId, int method, IReadOnlyDictionary<string, string> inputs, string key)
    {
        await api.PostAsync<PluginApiKeyInput, JsonObject>(Plugin(providerId) + "/api-key", new PluginApiKeyInput(method, ToJson(inputs), key));
        InvalidatePlugin(providerId);
    }

    /// <summary><c>POST …/accounts/{a}/sign-out</c>.</summary>
    public async Task PluginSignOutAsync(string providerId, string accountId)
    {
        await api.PostAsync<JsonObject>(Plugin(providerId) + "/accounts/" + ApiClient.Enc(accountId) + "/sign-out");
        InvalidatePlugin(providerId);
    }

    /// <summary>The daily check-in: <paramref name="on"/> switches it, null checks every account in now. The answer replaces the cached state.</summary>
    public async Task PluginCheckinActionAsync(string providerId, bool? on)
    {
        var state = on is { } value
            ? await api.PutAsync<PluginCheckinSwitch, PluginCheckinStateDto>(Plugin(providerId) + "/checkin", new PluginCheckinSwitch(value))
            : await api.PostAsync<PluginCheckinStateDto>(Plugin(providerId) + "/checkin");
        if (state is not null) queries.SetData(QueryKey.Of("plugin-checkin", providerId), state);
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, string> inputs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in inputs) o[k] = v;
        return o;
    }
}
