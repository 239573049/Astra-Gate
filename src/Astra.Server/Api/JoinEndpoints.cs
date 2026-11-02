using System.Text.Json.Nodes;
using Astra.Core.Tokens;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Astra.Server.Api;

/// <summary>
/// <c>POST /v1/astra/join/exchange</c>: the one public door of the join links. It takes the one-time code (never a key) and
/// answers with the token's key once; everything else about invites is admin API (<see cref="TokenEndpoints"/>). Like the other
/// <c>/v1/astra</c> views it is reachable from the network, so it is rate limited per address.
/// </summary>
public static class JoinEndpoints
{
    public const string RateLimitPolicy = "astra-join";

    public static void AddJoinRateLimit(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
    }

    public static void MapJoinEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/astra/join/exchange", async (JsonObject? body, HttpContext http, TokenInviteService invites, CancellationToken ct) =>
        {
            var code = body?["token"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var (result, refusal) = await invites.ExchangeAsync(code, http.Connection.RemoteIpAddress?.ToString(), ct);
            if (result is not null)
                return Results.Text(new JsonObject { ["key"] = result.Key, ["name"] = result.Name }.ToJsonString(), "application/json", System.Text.Encoding.UTF8);
            var (status, message) = refusal switch
            {
                TokenInvites.Used => (StatusCodes.Status410Gone, "这个接入链接已经被使用过"),
                TokenInvites.Expired => (StatusCodes.Status410Gone, "这个接入链接已过期"),
                TokenInvites.Revoked => (StatusCodes.Status410Gone, "这个接入链接已被撤销"),
                "token_disabled" => (StatusCodes.Status403Forbidden, "这个令牌已被停用"),
                _ => (StatusCodes.Status404NotFound, "接入链接无效"),
            };
            var error = new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = $"invite_{refusal}" } };
            return Results.Text(error.ToJsonString(), "application/json", System.Text.Encoding.UTF8, status);
        }).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
    }
}
