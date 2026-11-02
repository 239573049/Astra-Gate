using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>
/// One-time join links of gateway tokens: an admin makes a short-lived code for an employee's token, the link
/// (<c>astra://join?gateway=…&amp;token=…</c>) carries it and never the key, and the employee's Astra exchanges it once for the
/// key. Only the code's hash is stored; a redeemed, expired or revoked code is dead.
/// </summary>
public sealed class TokenInviteService(AstraDatabase db, ISecretProtector secrets, ServerOptions server)
{
    /// <summary>Dead codes are kept this long so the admin can still see when and from where one was used.</summary>
    private static readonly TimeSpan History = TimeSpan.FromDays(7);

    /// <summary>Makes a code for <paramref name="tokenId"/>; the link is the only time the code is visible.</summary>
    public async Task<TokenInviteCreatedDto> CreateAsync(string tokenId, int? ttlMinutes, CancellationToken ct)
    {
        var token = await db.Tokens.GetAsync(tokenId, ct) ?? throw new AdminApiException(404, "Token not found");
        if (!token.Enabled) throw new AdminApiException(409, "The token is disabled");
        if (token.KeyEnc is null) throw new AdminApiException(409, "Token has no key yet");
        var ttl = ttlMinutes ?? TokenInvites.DefaultTtlMinutes;
        if (ttl is < TokenInvites.MinTtlMinutes or > TokenInvites.MaxTtlMinutes)
            throw new AdminApiException(400, $"ttlMinutes must be between {TokenInvites.MinTtlMinutes} and {TokenInvites.MaxTtlMinutes}");
        var now = DateTimeOffset.UtcNow;
        var code = TokenInvites.NewCode();
        var invite = new TokenInvite
        {
            Id = Ulid.NewUlid(), TokenId = token.Id, CodeHash = TokenInvites.Hash(code), CreatedAt = now, ExpiresAt = now.AddMinutes(ttl),
        };
        await db.TokenInvites.PruneAsync(now - History, ct);
        await db.TokenInvites.InsertAsync(invite, ct);
        var link = $"astra://join?gateway={Uri.EscapeDataString(server.GatewayBaseUrl)}&token={Uri.EscapeDataString(code)}";
        return new TokenInviteCreatedDto(invite.Id, link, invite.ExpiresAt);
    }

    public async Task<List<TokenInviteDto>> ListAsync(string tokenId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        _ = await db.Tokens.GetAsync(tokenId, ct) ?? throw new AdminApiException(404, "Token not found");
        return (await db.TokenInvites.ListAsync(tokenId, now - History, ct))
            .Select(i => new TokenInviteDto(i.Id, i.DeadReason(now) ?? "pending", i.CreatedAt, i.ExpiresAt, i.UsedAt, i.UsedIp)).ToList();
    }

    public async Task RevokeAsync(string tokenId, string id, CancellationToken ct)
    {
        if (!await db.TokenInvites.RevokeAsync(tokenId, id, DateTimeOffset.UtcNow, ct))
            throw new AdminApiException(404, "No open join link with that id");
    }

    /// <summary>
    /// Hands the token's key to whoever holds a live code, once. Refuses with a reason (<see cref="TokenInvites.Used"/>,
    /// <see cref="TokenInvites.Expired"/>, <see cref="TokenInvites.Revoked"/>, <see cref="TokenInvites.Unknown"/>, or
    /// <c>token_disabled</c>) and consumes nothing when the token cannot be used.
    /// </summary>
    public async Task<(JoinExchangeDto? Result, string? Refusal)> ExchangeAsync(string? code, string? ip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 200) return (null, TokenInvites.Unknown);
        var invite = await db.TokenInvites.FindByHashAsync(TokenInvites.Hash(code), ct);
        if (invite is null) return (null, TokenInvites.Unknown);
        var now = DateTimeOffset.UtcNow;
        if (invite.DeadReason(now) is { } dead) return (null, dead);
        var token = await db.Tokens.GetAsync(invite.TokenId, ct);
        if (token is not { Enabled: true, KeyEnc: not null }) return (null, "token_disabled");
        // The statement itself decides a race between two redeemers: only one gets the key.
        if (!await db.TokenInvites.RedeemAsync(invite.Id, now, ip, ct)) return (null, TokenInvites.Used);
        return (new JoinExchangeDto(secrets.Unprotect(token.KeyEnc), token.Name), null);
    }
}
