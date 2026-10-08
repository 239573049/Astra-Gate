using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Astra.Gateway.Http;

/// <summary>Gateway entry points (plan §6.1). Authentication happens in the pipeline with gateway tokens.</summary>
public static class GatewayEndpoints
{
    public static IEndpointRouteBuilder MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/chat/completions", (HttpContext c, GatewayPipeline p) => p.HandleAsync(c, ApiProtocol.OpenAIChat)).ExcludeFromDescription();
        app.MapPost("/v1/responses", (HttpContext c, GatewayPipeline p) => p.HandleAsync(c, ApiProtocol.OpenAIResponses)).ExcludeFromDescription();
        app.MapPost("/v1/messages", (HttpContext c, GatewayPipeline p) => p.HandleAsync(c, ApiProtocol.Anthropic)).ExcludeFromDescription();
        app.MapPost("/v1/messages/count_tokens", (HttpContext c, GatewayAuxiliary a) => a.CountTokensAsync(c, ApiProtocol.Anthropic, null))
            .ExcludeFromDescription();

        // /v1beta/models/{model}:{generateContent|streamGenerateContent|countTokens}
        app.MapPost("/v1beta/models/{*modelAction}", (HttpContext c, string modelAction, GatewayPipeline p, GatewayAuxiliary a) =>
        {
            var colon = modelAction.LastIndexOf(':');
            var model = colon > 0 ? modelAction[..colon] : modelAction;
            var action = colon > 0 ? modelAction[(colon + 1)..] : "";
            return action switch
            {
                "generateContent" => p.HandleAsync(c, ApiProtocol.Gemini, model, pathStream: false),
                "streamGenerateContent" => p.HandleAsync(c, ApiProtocol.Gemini, model, pathStream: true,
                    pathAltSse: string.Equals(c.Request.Query["alt"].ToString(), "sse", StringComparison.OrdinalIgnoreCase)),
                "countTokens" => a.CountTokensAsync(c, ApiProtocol.Gemini, model),
                _ => a.WriteErrorAsync(c, ApiProtocol.Gemini, 404, "not_found_error", $"不支持的 Gemini 方法：{action}"),
            };
        }).ExcludeFromDescription();

        app.MapGet("/v1/models", (HttpContext c, GatewayAuxiliary a) =>
            a.ListModelsAsync(c, c.Request.Headers.ContainsKey("anthropic-version") ? ApiProtocol.Anthropic : ApiProtocol.OpenAIChat))
            .ExcludeFromDescription();
        app.MapGet("/v1beta/models", (HttpContext c, GatewayAuxiliary a) => a.ListModelsAsync(c, ApiProtocol.Gemini)).ExcludeFromDescription();
        return app;
    }
}

/// <summary>Model listing and token counting (no usage record, no billing).</summary>
public sealed class GatewayAuxiliary(GatewayRouter router, AstraDatabase db, CodecRegistry codecs, GatewayHttpClients http,
    UpstreamAuthResolver auth, PrivacyGuardService privacy)
{
    /// <summary>The bound provider's enabled models, in the shape of the asking protocol.</summary>
    public async Task ListModelsAsync(HttpContext ctx, ApiProtocol protocol)
    {
        GatewayRoute route;
        try
        {
            route = await router.ResolveAsync(ctx.Request, ctx.RequestAborted);
        }
        catch (GatewayException e)
        {
            await WriteErrorAsync(ctx, protocol, e.Status, e.Type, e.Message);
            return;
        }
        var list = (await db.Providers.ListModelsAsync(route.Provider.Id, ctx.RequestAborted)).Where(m => m.Enabled).ToList();
        if (route.Client is { Kind: ClientKinds.ClaudeDesktop } desktop)
        {
            // Plan §7.5: Claude Desktop only accepts role ids, so it sees one entry per mapped role.
            list = ClaudeDesktopRoles.Parse(desktop.ExtraJson).Select(r => new ProviderModel
            {
                ProviderId = route.Provider.Id,
                ModelId = ClaudeDesktopRoles.AdvertisedId(r.Key),
                Overrides = new ModelOverrides { DisplayName = $"{char.ToUpperInvariant(r.Key[0])}{r.Key[1..]} → {r.Value}" },
            }).ToList();
        }
        var created = route.Provider.CreatedAt.ToUnixTimeSeconds();
        JsonObject body = protocol switch
        {
            ApiProtocol.Anthropic => new JsonObject
            {
                ["data"] = new JsonArray(list.Select(m => (JsonNode)new JsonObject
                {
                    ["type"] = "model",
                    ["id"] = m.ModelId,
                    ["display_name"] = m.Overrides.DisplayName ?? m.ModelId,
                    ["created_at"] = route.Provider.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                }).ToArray()),
                ["has_more"] = false,
                ["first_id"] = list.FirstOrDefault()?.ModelId,
                ["last_id"] = list.LastOrDefault()?.ModelId,
            },
            ApiProtocol.Gemini => new JsonObject
            {
                ["models"] = new JsonArray(list.Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = "models/" + m.ModelId,
                    ["displayName"] = m.Overrides.DisplayName ?? m.ModelId,
                    ["supportedGenerationMethods"] = new JsonArray("generateContent", "streamGenerateContent", "countTokens"),
                }).ToArray()),
            },
            _ => new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(list.Select(m => (JsonNode)new JsonObject
                {
                    ["id"] = m.ModelId,
                    ["object"] = "model",
                    ["created"] = created,
                    ["owned_by"] = route.Provider.Name,
                }).ToArray()),
            },
        };
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToJsonString(GatewayJson.Options), ctx.RequestAborted);
    }

    /// <summary>
    /// Anthropic count_tokens / Gemini countTokens: forwarded when the provider speaks that protocol, otherwise a
    /// rough local estimate (≈ 4 characters per token) so clients that pre-flight token counts keep working.
    /// </summary>
    public async Task CountTokensAsync(HttpContext ctx, ApiProtocol protocol, string? pathModel)
    {
        var ct = ctx.RequestAborted;
        try
        {
            var route = await router.ResolveAsync(ctx.Request, ct);
            // Claude subscription client policy (plan §5.4): count_tokens is a Claude Code request on the UA alone.
            var isClaudeCode = protocol == ApiProtocol.Anthropic && ClaudeCodeDetector.IsClaudeCode(ctx.Request.Headers, null, countTokens: true);
            SubscriptionSupport.EnforceClientPolicy(route.Provider, isClaudeCode);
            using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);

            // 隐私护栏（plan §6.7）：count_tokens 的请求体同样发往上游，必须先过护栏。
            var guard = privacy.Inspect(body, route.ClientKind);
            if (guard.Blocked)
                throw new GatewayException(400, "invalid_request_error", "请求中包含被 Astra 隐私护栏拦截的敏感信息，已拒绝发送到上游。");
            body = guard.Body;

            var endpoint = route.Provider.EndpointFor(protocol);
            var json = pathModel is null ? JsonNode.Parse(body) as JsonObject : null;
            var model = route.UpstreamModelFor(pathModel ?? json?["model"]?.GetValue<string>() ?? "");
            if (json is not null && json["model"]?.GetValue<string>() is { } requested && requested != model)
            {
                json["model"] = model; // Claude Desktop role id → provider model
                body = json.ToJsonString(GatewayJson.Options);
            }
            var url = endpoint is null ? null : UpstreamUrls.CountTokens(endpoint, model);
            if (url is not null)
            {
                var claudeOAuth = SubscriptionSupport.IsClaudeSubscription(route.Provider) && protocol == ApiProtocol.Anthropic;
                var relay = claudeOAuth && isClaudeCode;
                if (relay) url = GatewayPipeline.WithClientQuery(url, ctx.Request.QueryString);
                var credentials = await auth.ResolveAsync(route.Provider.Id, route.AccountId, ct) ?? UpstreamAuth.None;
                if (credentials.QueryName is not null && credentials.QueryValue is not null)
                    url = UpstreamUrls.WithQuery(url, credentials.QueryName, credentials.QueryValue);
                using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (relay)
                {
                    // Same relay as /v1/messages: Claude Code's own headers, only the credential and OAuth beta change.
                    ClaudeOAuthHeaders.ApplyRelay(request, ctx.Request.Headers, model, route.Provider.ExtraHeaders, "2023-06-01");
                }
                else
                {
                    if (protocol == ApiProtocol.Anthropic)
                    {
                        var version = ctx.Request.Headers["anthropic-version"].ToString();
                        request.Headers.TryAddWithoutValidation("anthropic-version", version.Length > 0 ? version : "2023-06-01");
                        if (ctx.Request.Headers["anthropic-beta"].ToString() is { Length: > 0 } beta) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
                    }
                    foreach (var (name, value) in route.Provider.ExtraHeaders) request.Headers.TryAddWithoutValidation(name, value);
                    if (claudeOAuth) ClaudeOAuthHeaders.ApplyCompat(request, ctx.Request.Headers);
                }
                if (credentials.HeaderName is not null) request.Headers.TryAddWithoutValidation(credentials.HeaderName, credentials.HeaderValue);
                using var response = await http.For(route.Provider).SendAsync(request, ct);
                ctx.Response.StatusCode = (int)response.StatusCode;
                ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                await ctx.Response.WriteAsync(await response.Content.ReadAsStringAsync(ct), ct);
                return;
            }
            var estimate = Math.Max(1, body.Length / 4);
            JsonObject result = protocol == ApiProtocol.Gemini ? new JsonObject { ["totalTokens"] = estimate } : new JsonObject { ["input_tokens"] = estimate };
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(result.ToJsonString(GatewayJson.Options), ct);
        }
        catch (GatewayException e)
        {
            await WriteErrorAsync(ctx, protocol, e.Status, e.Type, e.Message);
        }
        catch (HttpRequestException e)
        {
            await WriteErrorAsync(ctx, protocol, 502, "api_error", e.Message);
        }
    }

    public async Task WriteErrorAsync(HttpContext ctx, ApiProtocol protocol, int status, string type, string message)
    {
        var body = codecs.Get(protocol)?.EncodeError(status, type, message)
                   ?? new JsonObject { ["error"] = new JsonObject { ["type"] = type, ["message"] = message } };
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(body.ToJsonString(GatewayJson.Options), ctx.RequestAborted);
    }
}
