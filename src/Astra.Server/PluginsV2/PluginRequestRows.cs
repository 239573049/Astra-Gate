using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Core.Requests;

namespace Astra.Server.PluginsV2;

/// <summary>
/// A request as v2 plugins see it (design/plugin-v2 02 §10, 09 §4.2): the rows of <c>data.requests.query</c> and the payload of
/// <c>request.completed</c>. Metadata only — never bodies, the client IP, the user agent, error text or trace ids. The shape is
/// a plugin contract of its own, not the admin request DTO.
/// </summary>
public static class PluginRequestRows
{
    public static JsonObject Row(RequestRecord r) => new()
    {
        ["id"] = r.Id,
        ["at"] = r.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
        ["client"] = r.ClientKind,
        ["token"] = r.TokenName,
        ["tokenId"] = r.TokenId,
        ["provider"] = r.ProviderName,
        ["providerId"] = r.ProviderId,
        ["protocol"] = r.InboundProtocol,
        ["model"] = r.RequestedModel,
        ["stream"] = r.Stream,
        ["status"] = r.Status,
        ["httpStatus"] = r.HttpStatus,
        ["inputTokens"] = r.TotalInputTokens,
        ["outputTokens"] = r.TotalOutputTokens,
        ["cacheReadTokens"] = r.CacheReadTokens,
        ["cacheWriteTokens"] = r.CacheWriteTokens,
        ["reasoningTokens"] = r.ReasoningTokens,
        ["costNanoUsd"] = r.CostNanoUsd,
        ["ttftMs"] = r.TtftMs,
        ["totalMs"] = r.TotalMs,
    };
}
