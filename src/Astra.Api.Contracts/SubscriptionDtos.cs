using System.Text.Json.Nodes;
using Astra.Core.Models;

namespace Astra.Api.Contracts;

/// <summary>
/// Login start: <c>accountId</c> re-logs an existing account in; <c>httpProxy</c> is the proxy this account uses
/// from the login on (empty = the account's current one when re-logging in, else the provider's).
/// </summary>
public sealed record LoginOptions(string? AccountId, string? HttpProxy = null);

/// <summary>
/// Body of the Copilot import: <c>token</c> is an optional GitHub token the user pasted; when it is
/// empty the machine's own sources are probed. <c>accountId</c> adopts the token into an existing
/// account row instead of creating a new one.
/// </summary>
public sealed record LocalCopilotImport(string? Token, string? AccountId);

/// <summary>Body of the batch Copilot import: GitHub tokens separated by newlines / commas / whitespace.</summary>
public sealed record BatchCopilotImport(string? Tokens);

/// <summary>Outcome values of <see cref="BatchImportItemDto.Status"/>.</summary>
public static class BatchImportStatus
{
    public const string Imported = "imported";
    public const string Duplicate = "duplicate";
    public const string InvalidFormat = "invalid_format";
    public const string Failed = "failed";

    /// <summary>Same account as an existing row (matched by email): its credentials were replaced.</summary>
    public const string Updated = "updated";

    /// <summary>Dry run only: the entry is recognised and would be imported.</summary>
    public const string Ready = "ready";
}

/// <summary>
/// One token of a batch import. <see cref="Source"/> is the masked token (never the token itself);
/// <see cref="Message"/> is the failure / duplicate reason, or a warning on an imported account.
/// </summary>
public sealed record BatchImportItemDto(int Index, string Source, string Status, string? Message, ProviderAccountDto? Account,
    /// <summary>What the entry was recognised as (<see cref="CredentialKinds"/>); null when unrecognised.</summary>
    string? Kind = null);

/// <summary>Per-token results of a batch import plus totals.</summary>
public sealed record BatchImportResultDto(List<BatchImportItemDto> Items, int Imported, int Duplicates, int Failed);

/// <summary>A subscription account as shown in the admin API; tokens are never included.</summary>
public sealed record ProviderAccountDto(
    string Id, string ProviderId, string DisplayName, string? AccountEmail, string? Plan, string Status,
    DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastRefreshAtUtc, JsonNode? Quota, DateTimeOffset CreatedAt,
    /// <summary>codex 的额度重置卡列表（<c>extra.credits</c> 快照；列表接口实时刷新）。</summary>
    JsonNode? Credits = null,
    bool Enabled = true,
    /// <summary>The account requests currently go to (without a client / token pin).</summary>
    bool IsCurrent = false,
    int SortOrder = 0,
    DateTimeOffset? CooldownUntilUtc = null,
    string? LastError = null,
    /// <summary><see cref="AccountKinds"/>: <c>oauth</c> or <c>api_key</c>.</summary>
    string Kind = AccountKinds.OAuth,
    /// <summary><see cref="AccountSources"/>; null for accounts older than the column.</summary>
    string? Source = null,
    /// <summary>The account's own proxy (null = the provider's). A proxy URL may carry credentials, but it is the user's own setting.</summary>
    string? HttpProxy = null,
    /// <summary>Masked API key (<c>kind = api_key</c> only), so a key account can be recognised.</summary>
    string? KeyMasked = null);

/// <summary>PATCH body for one account; omitted fields keep their value. <c>httpProxy</c> "" clears the account's proxy.</summary>
public sealed record AccountPatch(bool? Enabled, string? DisplayName, string? HttpProxy = null);

/// <summary>New failover order (account ids, first = tried first).</summary>
public sealed record AccountOrder(List<string> Ids);

/// <summary>
/// Subscription policy of a provider: who may use it (<c>claude-code-only</c> | <c>any</c>), how accounts switch
/// (<c>manual</c> | <c>failover</c> | <c>balanced</c>), whether <see cref="ClaudeSubscription"/> marks the family whose client policy
/// applies by default, and whether non-Claude-Code callers present a Claude Code identity (mimic).
/// </summary>
public sealed record SubscriptionPolicyDto(string ClientPolicy, string SwitchMode, bool ClaudeSubscription, bool MimicClaudeCode);

/// <summary>PUT body for the subscription policy; omitted fields keep their value.</summary>
public sealed record SubscriptionPolicyPatch(string? ClientPolicy, string? SwitchMode, bool? MimicClaudeCode);

/// <summary>Authorization-code (PKCE) login start; the UI opens <see cref="AuthorizeUrl"/>.</summary>
public sealed record LoginModeDto(string Mode, string State, string AuthorizeUrl);

/// <summary>Device-code login start; the UI shows <see cref="UserCode"/> and polls.</summary>
public sealed record LoginDeviceDto(string Mode, string State, string? UserCode, string VerificationUrl, int Interval);

/// <summary>
/// Manual-paste login start (Claude): the authorization page shows the code, the user pastes it back —
/// the client's registered redirect_uri is not a loopback address, so nothing can call us back.
/// </summary>
public sealed record LoginPasteDto(string Mode, string State, string AuthorizeUrl);

/// <summary>Body of the paste completion: whatever the user copied (the code, or the whole callback URL).</summary>
public sealed record CompleteLoginRequest(string? Code);

/// <summary>Server-mediated CLI login start (zcli); the UI opens <see cref="AuthorizeUrl"/> and polls.</summary>
public sealed record LoginCliDto(string Mode, string State, string AuthorizeUrl, int Interval);

/// <summary>Device-flow poll heartbeat (status = pending | slow_down).</summary>
public sealed record LoginPollDto(string Status);

/// <summary>Terminal device-flow poll: success carries the account, anything else the error.</summary>
public sealed record PollFailureDto(string Status, string? Error);

/// <summary>Successful device-flow poll.</summary>
public sealed record LoginDoneDto(ProviderAccountDto Account)
{
    public string Status => "done";
}

/// <summary>Whether the machine already holds a usable <c>codex login</c> credential set.</summary>
public sealed record LocalCodexLoginDto(bool Available, string? AccountEmail, string? Plan, string? Detail);

/// <summary>
/// Whether the machine already holds a GitHub authorization that could back a Copilot account.
/// Only the source is reported — never the token itself. Whether the account actually <em>has</em>
/// Copilot is decided by the exchange at import time, not here.
/// </summary>
public sealed record LocalCopilotLoginDto(bool Available, string? Source, string? Detail);

/// <summary>Imported account + a fresh quota snapshot (when the probe succeeded).</summary>
public sealed record ImportedAccountDto(ProviderAccountDto Account, JsonNode? Quota, string? Warning);

/// <summary>After consuming a reset credit: the account and its refreshed quota snapshot.</summary>
public sealed record ResetCreditResultDto(ProviderAccountDto Account, JsonNode? Quota);

/// <summary>
/// Body of the account import. <c>text</c> holds one or more entries (one per line, or a JSON document);
/// <c>asApiKey</c> reads every entry as an API key; <c>dryRun</c> only classifies and de-duplicates (no network,
/// nothing written); <c>httpProxy</c> becomes each imported account's own proxy.
/// </summary>
public sealed record AccountImportRequest(string? Text, bool? AsApiKey = null, bool? DryRun = null, string? HttpProxy = null);

/// <summary>What a provider's import accepts: whether it imports at all, takes API keys, and the format ids to hint.</summary>
public sealed record AccountImportInfoDto(bool Supported, bool ApiKey, List<string> Formats, int MaxBatch);
