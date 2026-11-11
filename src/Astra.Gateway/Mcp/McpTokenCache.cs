using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Core.Mcp;
using Astra.Data;
using ModelContextProtocol.Authentication;

namespace Astra.Gateway.Mcp;

/// <summary>
/// The OAuth tokens of one upstream, kept in <c>mcp_oauth</c> as a protected value (the SDK's own in-memory cache is internal, and a
/// restart must not cost an admin a new authorization). The SDK reads it on every request, so tokens an admin has just granted through
/// another connection are picked up without restarting the data-plane connection. The value holds the client registration and the
/// issuing authorization server too: client credentials are bound to the server that issued them (MCP authorization spec).
/// Written by hand — a source-generated context for a foreign type would tie the format to the SDK's version.
/// </summary>
public sealed class McpTokenCache(AstraDatabase db, ISecretProtector secrets, string serverId) : ITokenCache
{
    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        var json = new JsonObject
        {
            ["tokenType"] = tokens.TokenType,
            ["accessToken"] = tokens.AccessToken,
            ["refreshToken"] = tokens.RefreshToken,
            ["expiresIn"] = tokens.ExpiresIn,
            ["scope"] = tokens.Scope,
            ["obtainedAt"] = tokens.ObtainedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["clientId"] = tokens.ClientId,
            ["clientSecret"] = tokens.ClientSecret,
            ["tokenEndpointAuthMethod"] = tokens.TokenEndpointAuthMethod,
            ["authorizationServer"] = tokens.AuthorizationServer,
        };
        await db.Mcp.SaveOAuthAsync(new McpOAuthRecord
        {
            ServerId = serverId,
            TokensEnc = secrets.Protect(json.ToJsonString()),
            Issuer = tokens.AuthorizationServer,
            Scope = tokens.Scope,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);
    }

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        var row = await db.Mcp.GetOAuthAsync(serverId, cancellationToken);
        if (row is null) return null;
        try
        {
            if (JsonNode.Parse(secrets.Unprotect(row.TokensEnc)) is not JsonObject json) return null;
            return new TokenContainer
            {
                TokenType = (string?)json["tokenType"] ?? "Bearer",
                AccessToken = (string?)json["accessToken"] ?? "",
                RefreshToken = (string?)json["refreshToken"],
                ExpiresIn = (int?)json["expiresIn"] ?? 0,
                Scope = (string?)json["scope"],
                ObtainedAt = DateTimeOffset.TryParse((string?)json["obtainedAt"], out var at) ? at : DateTimeOffset.UtcNow,
                ClientId = (string?)json["clientId"],
                ClientSecret = (string?)json["clientSecret"],
                TokenEndpointAuthMethod = (string?)json["tokenEndpointAuthMethod"],
                AuthorizationServer = (string?)json["authorizationServer"],
            };
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or System.Security.Cryptography.CryptographicException or InvalidCastException)
        {
            // A value that no longer decrypts (the data-protection keys changed) is as good as none: the server needs authorizing again.
            return null;
        }
    }
}
