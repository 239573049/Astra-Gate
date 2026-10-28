using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;

namespace Astra.Desktop.Tests.Clients;

/// <summary>Builds Contracts DTOs from the admin API's JSON (camelCase), the way the page receives them.</summary>
internal static class ClientsTestData
{
    static ClientsTestData() => RuntimeHelpers.RunModuleConstructor(typeof(ClientInfoDto).Module.ModuleHandle);

    public static T Read<T>(string json) => JsonSerializer.Deserialize(json, JsonContexts.Info<T>(Json.Api))!;

    /// <summary>A client; <paramref name="patch"/> overrides fields of the default (an enabled, detected Codex).</summary>
    public static ClientInfoDto Client(string kind = "codex", Action<JsonObject>? patch = null)
    {
        var o = new JsonObject
        {
            ["kind"] = kind, ["name"] = kind, ["protocol"] = "openai-chat", ["availability"] = "available", ["availabilityReason"] = null, ["mode"] = "switch",
            ["detection"] = new JsonObject { ["installed"] = true, ["configExists"] = true, ["version"] = "1.0.0", ["configPaths"] = new JsonArray("/home/u/." + kind + "/config.json") },
            ["status"] = "disabled", ["enabled"] = false, ["providerId"] = "p1", ["accountId"] = null, ["selectedModel"] = null, ["extras"] = new JsonObject(),
            ["appliedAt"] = null, ["warnings"] = new JsonArray(), ["requiresRestart"] = false, ["tokenId"] = null,
        };
        patch?.Invoke(o);
        return Read<ClientInfoDto>(o.ToJsonString());
    }

    public static ProviderDto Provider(string id, bool enabled = true, string authScheme = "bearer", string? priceKey = "openai", decimal multiplier = 1, int models = 3) => Read<ProviderDto>(new JsonObject
    {
        ["id"] = id, ["templateId"] = "tpl-" + id, ["templateVersion"] = null, ["templateUpdateAvailable"] = false, ["name"] = id.ToUpperInvariant(), ["icon"] = null,
        ["category"] = "official", ["endpoints"] = new JsonArray(), ["preferredUpstreamProtocols"] = new JsonArray(), ["authScheme"] = authScheme,
        ["hasApiKey"] = true, ["apiKeyMasked"] = null, ["extraHeaders"] = new JsonObject(), ["httpProxy"] = null, ["priceMultiplier"] = multiplier,
        ["priceKey"] = priceKey, ["adapterId"] = null, ["settings"] = new JsonObject(), ["enabled"] = enabled, ["sortOrder"] = 0, ["notes"] = null, ["website"] = null,
        ["modelCount"] = models, ["boundClients"] = new JsonArray(), ["createdAt"] = "2026-01-01T00:00:00Z", ["updatedAt"] = "2026-01-01T00:00:00Z",
        ["quotaConfig"] = new JsonObject { ["enabled"] = false }, ["quota"] = null, ["quotaCheckedAtUtc"] = null,
    }.ToJsonString());
}
