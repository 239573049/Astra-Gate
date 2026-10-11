using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;

namespace Astra.Server.Api;

// Account import (plan §5.4): one endpoint for every subscription family — pasted refresh tokens, Claude setup-tokens,
// credential JSON files, API keys and GitHub / Copilot tokens. CredentialImport (Astra.Providers) classifies each
// entry; this file validates it against the upstream and persists it.
public static partial class SubscriptionEndpoints
{
    /// <summary>
    /// Body of the account import. <c>text</c> holds one or more entries (one per line, or a JSON document);
    /// <c>asApiKey</c> reads every entry as an API key; <c>dryRun</c> only classifies and de-duplicates (no network,
    /// nothing written); <c>httpProxy</c> becomes each imported account's own proxy.
    /// </summary>
    public sealed record AccountImportRequest(string? Text, bool? AsApiKey = null, bool? DryRun = null, string? HttpProxy = null);

    /// <summary>What a provider's import accepts: whether it imports at all, takes API keys, and the format ids to hint.</summary>
    public sealed record AccountImportInfoDto(bool Supported, bool ApiKey, List<string> Formats, int MaxBatch);

    /// <summary>The services one import run needs (kept together so the Copilot batch endpoint can share the run).</summary>
    internal sealed record ImportServices(
        AstraDatabase Db, ISecretProtector Protector, OAuthClient Client, SubscriptionQuotaService Quota, ProviderProbe Probe);

    /// <summary>How long an imported setup-token is assumed to live when its expiry is unknown (Claude issues them for a year).</summary>
    private static readonly TimeSpan SetupTokenLifetime = TimeSpan.FromDays(365);

    private static void MapAccountImportEndpoints(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/providers/{providerId}/accounts/import-info", async (string providerId, AstraDatabase db) =>
        {
            var provider = await db.Providers.GetAsync(providerId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            var formats = provider.AuthScheme == AuthSchemes.OAuthSubscription
                ? CredentialImport.FormatsOf(SubscriptionSupport.EffectiveConfig(provider).ProviderKey)
                : new CredentialImportFormats(false, []);
            return Results.Ok(new AccountImportInfoDto(formats.Formats.Count > 0, formats.ApiKey, [.. formats.Formats], MaxBatchImport));
        });

        // 统一导入：逐条识别 → 去重 → 实际换取 / 校验 → 落库；单条失败不影响其它。顺序执行（上游有速率限制）。
        // 响应里只有脱敏片段，绝不回显令牌本身。
        app.MapPost("/api/providers/{providerId}/accounts/import", async (
            string providerId, AccountImportRequest? body, AstraDatabase db, ISecretProtector protector,
            OAuthClient client, SubscriptionQuotaService quotaService, ProviderProbe probe, CancellationToken ct) =>
        {
            var provider = await db.Providers.GetAsync(providerId, ct);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
                return Results.BadRequest(new ErrorOnlyDto("该提供商不是订阅类型（auth_scheme ≠ oauth-subscription）"));
            var config = SubscriptionSupport.EffectiveConfig(provider);
            var formats = CredentialImport.FormatsOf(config.ProviderKey);
            if (formats.Formats.Count == 0) return Results.BadRequest(new ErrorOnlyDto("该订阅不支持导入令牌，请使用浏览器登录"));
            var asApiKey = body?.AsApiKey == true;
            if (asApiKey && !formats.ApiKey) return Results.BadRequest(new ErrorOnlyDto("该订阅不接受 API Key"));

            string? accountProxy;
            try
            {
                accountProxy = ProxiedHttpClients.Normalize(body?.HttpProxy);
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new ErrorOnlyDto(e.Message));
            }

            var entries = CredentialImport.SplitEntries(body?.Text);
            if (entries.Count == 0) return Results.BadRequest(new ErrorOnlyDto("没有可导入的内容（每行一个令牌，或粘贴凭据 JSON）"));
            if (entries.Count > MaxBatchImport)
                return Results.BadRequest(new ErrorOnlyDto($"一次最多导入 {MaxBatchImport} 条，当前 {entries.Count} 条，请分批导入"));

            return Results.Ok(await ImportEntriesAsync(new ImportServices(db, protector, client, quotaService, probe),
                provider, config, entries, asApiKey, body?.DryRun == true, accountProxy,
                entries.Count == 1 ? AccountSources.Token : AccountSources.Batch, ct));
        });
    }

    /// <summary>
    /// Imports <paramref name="entries"/> one by one. Duplicates (within the input, or of a credential an account of this
    /// provider already holds) are skipped; a login whose email matches an existing account updates that account.
    /// A dry run stops after classification and de-duplication and reports <see cref="BatchImportStatus.Ready"/>.
    /// </summary>
    internal static async Task<BatchImportResultDto> ImportEntriesAsync(
        ImportServices services, Provider provider, SubscriptionOAuthConfig config, IReadOnlyList<string> entries,
        bool asApiKey, bool dryRun, string? accountProxy, string source, CancellationToken ct)
    {
        var (db, protector, client, quotaService, probe) = services;
        // 导入时的换码 / 刷新 / 身份请求就走账号将来的出口。
        var importConfig = accountProxy is null ? config : config.WithHttpProxy(accountProxy);

        // 已有账号持有的凭据（访问令牌与刷新槽都算）；解不开的旧账号不参与去重。
        var known = new List<(string Token, string Name)>();
        foreach (var existing in await db.Accounts.ListAsync(provider.Id, ct))
        {
            foreach (var enc in new[] { existing.AccessTokenEnc, existing.RefreshTokenEnc })
            {
                if (enc is null) continue;
                try
                {
                    known.Add((protector.Unprotect(enc), existing.DisplayName));
                }
                catch (Exception)
                {
                    // 密文无法解密的旧账号不参与去重
                }
            }
        }

        var items = new List<BatchImportItemDto>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int imported = 0, duplicates = 0, failed = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[i];
            var index = i + 1;
            var parsed = CredentialImport.Classify(config.ProviderKey, entry, asApiKey, out var formatError);
            if (parsed is null)
            {
                failed++;
                items.Add(new BatchImportItemDto(index, entry.StartsWith('{') ? "{…}" : MaskToken(entry),
                    BatchImportStatus.InvalidFormat, formatError, null));
                continue;
            }
            // 去重键：能续期的那一份（刷新槽）优先，否则访问令牌 / Key 本身。
            var identity = parsed.RefreshToken ?? parsed.Secret;
            var shown = MaskToken(identity);
            if (!seen.Add(identity))
            {
                duplicates++;
                items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Duplicate, "本次输入中重复", null, parsed.Kind));
                continue;
            }
            var match = known.FirstOrDefault(k => TokensEqual(k.Token, identity) || TokensEqual(k.Token, parsed.Secret));
            if (match.Token is not null)
            {
                duplicates++;
                items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Duplicate, $"已存在账号：{match.Name}", null, parsed.Kind));
                continue;
            }
            if (dryRun)
            {
                imported++;
                items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Ready, null, null, parsed.Kind));
                continue;
            }

            try
            {
                var (account, adopted, note) = await ImportOneAsync(db, protector, client, provider.Id, importConfig, parsed, shown,
                    accountProxy, source, ct);
                if (account is null)
                {
                    failed++;
                    items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Failed, note, null, parsed.Kind));
                    continue;
                }
                known.Add((identity, account.DisplayName));

                // 顺手验一次额度。API Key 被上游拒绝就是 Key 不对：不留一个注定失效的账号。
                // 其它凭据的探测失败不致命（可能只是额度接口的权限不够），留一条提示。
                string? warning = note;
                try
                {
                    var (updated, _) = await quotaService.FetchAsync(provider, account, config, ct);
                    account = updated;
                }
                catch (SubscriptionAuthException e) when (parsed.Kind == CredentialKinds.ApiKey && !adopted)
                {
                    await db.Accounts.DeleteAsync(account.Id, ct);
                    failed++;
                    items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Failed, $"上游拒绝了这把 API Key：{e.Message}", null, parsed.Kind));
                    continue;
                }
                catch (Exception e) when (e is SubscriptionAuthException or OAuthProtocolException)
                {
                    warning = e.Message;
                }
                imported++;
                items.Add(new BatchImportItemDto(index, shown, adopted ? BatchImportStatus.Updated : BatchImportStatus.Imported,
                    adopted ? $"已存在账号 {account.DisplayName}，已换成这份新凭据" : warning, ToDto(account), parsed.Kind));
            }
            catch (Exception e) when (e is OAuthProtocolException or HttpRequestException)
            {
                failed++;
                var message = e is OAuthProtocolException oauth
                    ? parsed.Kind == CredentialKinds.GitHubToken ? CopilotImportFailure(oauth) : ImportFailure(oauth)
                    : e.Message;
                items.Add(new BatchImportItemDto(index, shown, BatchImportStatus.Failed, message, null, parsed.Kind));
            }
        }

        // 模型清单归上游管（Copilot）：一批导入只同步一次，失败不影响导入结果。
        if (!dryRun && imported > 0 && config.Style == "github-copilot") await probe.SyncModelsFromUpstreamAsync(provider.Id, ct);

        return new BatchImportResultDto(items, imported, duplicates, failed);
    }

    /// <summary>
    /// Turns one classified credential into an account. Returns a null account with the reason when the credential is
    /// recognised but unusable (e.g. an already expired Copilot token); throws on upstream / transport failures.
    /// </summary>
    private static async Task<(ProviderAccount? Account, bool Adopted, string? Note)> ImportOneAsync(
        AstraDatabase db, ISecretProtector protector, OAuthClient client, string providerId, SubscriptionOAuthConfig config,
        ParsedCredential parsed, string shown, string? accountProxy, string source, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        PendingSubscriptionLogins.PendingLogin Login() => new(
            Ulid.NewUlid(), providerId, null, config, null, null, now + LoginTtl, AccountProxy: accountProxy);

        switch (parsed.Kind)
        {
            case CredentialKinds.CopilotToken:
            {
                // 直连令牌不需要换：原样当访问令牌存下，到期时间取自 exp=。没有 GitHub token 就没法续。
                var expiresAt = parsed.ExpiresAt!.Value;
                if (expiresAt <= now.Add(SubscriptionTokenService.RefreshLead))
                    return (null, false, $"这个 Copilot 令牌已过期（{expiresAt:u}）");
                var direct = await ImportDirectCopilotTokenAsync(db, protector, providerId, config, parsed.Secret, expiresAt, source, accountProxy);
                return (direct, false, $"直连令牌，不会自动续期，{expiresAt:u} 到期");
            }

            case CredentialKinds.GitHubToken:
            {
                // 访问令牌这一格先放 GitHub token：CompleteLoginAsync 拿它换 Copilot 短时令牌并把它留在刷新槽。
                var token = new OAuthClient.TokenResult(parsed.Secret, parsed.Secret, null, null, null);
                var (account, adopted) = await CompleteLoginWithResultAsync(db, protector, client, Login(), token,
                    source: source, nameHint: shown);
                return (account, adopted, null);
            }

            case CredentialKinds.RefreshToken:
            {
                // 先用 refresh token 换一次：既验证它能用，也拿到身份（id_token / 身份端点）。上游不轮换时沿用原值。
                var token = await client.RefreshAsync(config, parsed.Secret, ct);
                if (token.RefreshToken is null) token = token with { RefreshToken = parsed.Secret };
                var (account, adopted) = await CompleteLoginWithResultAsync(db, protector, client, Login(), token,
                    source: source, adoptByEmail: true, nameHint: shown);
                return (account, adopted, null);
            }

            case CredentialKinds.ClaudeCredentials:
            {
                var token = parsed.Secret.Length > 0
                    ? new OAuthClient.TokenResult(parsed.Secret, parsed.RefreshToken, SecondsUntil(parsed.ExpiresAt, now), null, null)
                    : await client.RefreshAsync(config, parsed.RefreshToken!, ct);
                if (token.RefreshToken is null) token = token with { RefreshToken = parsed.RefreshToken };
                var (account, adopted) = await CompleteLoginWithResultAsync(db, protector, client, Login(), token,
                    source: source, adoptByEmail: true, nameHint: shown);
                return (await WithPlanAsync(db, account, parsed.Plan), adopted, null);
            }

            case CredentialKinds.SetupToken:
            {
                // 长期推理令牌：没有刷新槽，到期（或上游 401）后账号失效；额度接口不认它的 scope，标记后跳过探测。
                var expiresAt = parsed.ExpiresAt ?? now + SetupTokenLifetime;
                if (expiresAt <= now.Add(SubscriptionTokenService.RefreshLead))
                    return (null, false, $"这个令牌已过期（{expiresAt:u}）");
                var token = new OAuthClient.TokenResult(parsed.Secret, null, SecondsUntil(expiresAt, now), null, null);
                var (account, _) = await CompleteLoginWithResultAsync(db, protector, client, Login(), token,
                    source: source, nameHint: shown);
                account.Extra[SubscriptionQuotaService.SetupTokenFlag] = true;
                if (parsed.Plan is { Length: > 0 } plan) account.Plan ??= plan;
                await db.Accounts.UpdateAsync(account, ct);
                return (account, false, null);
            }

            case CredentialKinds.CodexAuthJson:
            {
                var token = new OAuthClient.TokenResult(parsed.Secret, parsed.RefreshToken,
                    SubscriptionTokenService.ExpiresInSeconds(parsed.Secret, now), parsed.IdToken, null);
                var (account, adopted) = await CompleteLoginWithResultAsync(db, protector, client, Login(), token,
                    source: source, adoptByEmail: true, nameHint: shown);
                return (account, adopted, null);
            }

            case CredentialKinds.ApiKey:
            {
                var account = new ProviderAccount
                {
                    Id = Ulid.NewUlid(),
                    ProviderId = providerId,
                    DisplayName = shown,
                    Kind = AccountKinds.ApiKey,
                    Source = source,
                    HttpProxy = accountProxy,
                    AccessTokenEnc = protector.Protect(parsed.Secret),
                    Status = AccountStatus.Active,
                    LastRefreshAtUtc = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                account.Extra[KeyMaskedExtra] = shown;
                // 有身份端点、且端点认这把 Key 的家族（Kimi）补 email 与套餐名；装饰性字段，失败不致命。
                // ZCode 形态的身份端点只认 OAuth token，不试。
                if (config.IdentityUrl.Length > 0 && config.Style is not ("zcode" or "zcli"))
                {
                    try
                    {
                        var (email, plan) = await client.FetchIdentityAsync(config, parsed.Secret, ct);
                        if (email is not null) account.DisplayName = email;
                        account.AccountEmail = email;
                        account.Plan = plan;
                    }
                    catch (Exception e) when (e is OAuthProtocolException or HttpRequestException)
                    {
                        // 身份拿不到不影响 Key 本身
                    }
                }
                await db.Accounts.InsertAsync(account, ct);
                return (account, false, null);
            }
        }
        return (null, false, "该凭据类型暂不支持导入");
    }

    private static int SecondsUntil(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } t ? (int)Math.Max(0, Math.Min(int.MaxValue, (t - now).TotalSeconds)) : 3600;

    private static async Task<ProviderAccount> WithPlanAsync(AstraDatabase db, ProviderAccount account, string? plan)
    {
        if (plan is not { Length: > 0 } || account.Plan is { Length: > 0 }) return account;
        account.Plan = plan;
        await db.Accounts.UpdateAsync(account);
        return account;
    }

    /// <summary>把导入时换令牌的失败翻译成用户能懂的一句话。</summary>
    private static string ImportFailure(OAuthProtocolException e) =>
        e.IsInvalidGrant ? "令牌已失效或已被使用过（refresh token 通常只能用一次，请重新获取）" : e.Message;
}
