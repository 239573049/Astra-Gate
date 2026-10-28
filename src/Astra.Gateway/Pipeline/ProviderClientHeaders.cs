using System.Text.Json.Nodes;
using Astra.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The upstream request headers that come from the caller rather than from the provider's configuration. First the
/// client headers the provider's API reads (settings <c>client_headers</c>, a list of header names the template sets):
/// passed on when the caller sent them, never invented. Then what the provider's own client adds to every request
/// (fresh request ids, the conversation intent), where a value the caller sent wins. Generation requests and the relay's
/// model list, embeddings and token counting requests get the same headers.
/// </summary>
public static class ProviderClientHeaders
{
    /// <summary>Settings key of the client headers passed through to the provider.</summary>
    public const string SettingsKey = "client_headers";

    public static void Apply(Provider provider, HttpRequestMessage request, IHeaderDictionary client)
    {
        Forward(provider, request, client);
        if (CopilotCliHeaders.Applies(provider)) CopilotCliHeaders.Apply(request, client);
    }

    /// <summary>Only the passed-through client headers (what a model list request carries).</summary>
    public static void Forward(Provider provider, HttpRequestMessage request, IHeaderDictionary client)
    {
        if (provider.Settings[SettingsKey] is not JsonArray names) return;
        foreach (var node in names)
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var name) || name.Length == 0) continue;
            if (client[name].ToString() is not { Length: > 0 } value) continue;
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
