using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Accounts;

/// <summary>
/// The Claude bridge's queries and writes (web/src/api/hooks.ts <c>useClaudeBridge</c> …, linked update 36): <c>['claude-bridge']</c> is the program
/// that was found, its settings and live runs (polled every 10 s while shown); an account's sign-in is asked on demand. The writes refuse
/// callers off this machine (403), so the view says so instead of failing silently.
/// </summary>
public sealed class ClaudeBridgeApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public ClaudeBridgeApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Status = queries.Query(QueryKey.Of("claude-bridge"), async ct => await api.GetAsync<ClaudeBridgeStatusDto>("/api/claude-bridge", ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<ClaudeBridgeStatusDto> { RefetchInterval = _ => TimeSpan.FromSeconds(10) });
    }

    /// <summary>['claude-bridge'].</summary>
    public Query<ClaudeBridgeStatusDto> Status { get; }

    /// <summary>PUT /api/claude-bridge/settings.</summary>
    public async Task UpdateSettingsAsync(ClaudeBridgeSettingsPatch patch, CancellationToken ct = default)
    {
        await _api.PutAsync<ClaudeBridgeSettingsPatch, ClaudeBridgeSettingsDto>("/api/claude-bridge/settings", patch, ct);
        _queries.Invalidate("claude-bridge");
    }

    /// <summary>POST /api/claude-bridge/accounts: adds an account that is answered through the claude program.</summary>
    public async Task<ProviderAccountDto> AddAccountAsync(ClaudeBridgeAccountRequest request, CancellationToken ct = default)
    {
        var account = await _api.PostAsync<ClaudeBridgeAccountRequest, ProviderAccountDto>("/api/claude-bridge/accounts", request, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("provider-accounts");
        _queries.Invalidate("warmup-accounts");
        return account;
    }

    /// <summary>GET /api/claude-bridge/accounts/{id}/signin: who <c>claude auth status</c> says is signed in.</summary>
    public async Task<ClaudeSignInDto> SignInAsync(string accountId, CancellationToken ct = default) =>
        await _api.GetAsync<ClaudeSignInDto>("/api/claude-bridge/accounts/" + ApiClient.Enc(accountId) + "/signin", ct) ?? throw new ApiException(0, "empty response");

    /// <summary>POST /api/claude-bridge/accounts/{id}/login: starts <c>claude auth login</c> for the account.</summary>
    public async Task<ClaudeBridgeLoginDto> StartLoginAsync(string accountId, CancellationToken ct = default) =>
        await _api.PostAsync<ClaudeBridgeLoginDto>("/api/claude-bridge/accounts/" + ApiClient.Enc(accountId) + "/login", ct) ?? throw new ApiException(0, "empty response");

    /// <summary>GET /api/claude-bridge/accounts/{id}/login.</summary>
    public async Task<ClaudeBridgeLoginDto> LoginStateAsync(string accountId, CancellationToken ct = default) =>
        await _api.GetAsync<ClaudeBridgeLoginDto>("/api/claude-bridge/accounts/" + ApiClient.Enc(accountId) + "/login", ct) ?? throw new ApiException(0, "empty response");
}

/// <summary>The bridge panel's wording, shared with the web's component.</summary>
public static class ClaudeBridgeLogic
{
    /// <summary>The "who is signed in" line of a sign-in answer (<c>claudeBridge.signin.*</c>).</summary>
    public static string SignInText(ClaudeSignInDto s) =>
        !s.Known ? I18n.Loc.T("claudeBridge.signin.unknown", ("error", s.Error ?? ""))
        : s.LoggedIn ? I18n.Loc.T("claudeBridge.signin.ok", ("who", string.Join(" · ", new[] { s.Email, s.Plan }.Where(x => !string.IsNullOrEmpty(x)))))
        : I18n.Loc.T("claudeBridge.signin.out");

    /// <summary>The headline of the found program (or the way to get one).</summary>
    public static string FoundText(ClaudeBridgeStatusDto s) =>
        !s.Found ? I18n.Loc.T("claudeBridge.notFound")
        : string.IsNullOrEmpty(s.Version) ? I18n.Loc.T("claudeBridge.foundNoVersion")
        : I18n.Loc.T("claudeBridge.found", ("version", s.Version));
}
