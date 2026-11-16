using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Host;

// What a plugin's OpenCode `tool` hook and magpie's `search` hook give, and how to use them (magpie's
// internal/plugin/tools.go and search.go, MIT). Nothing here is wired to a UI or to the MCP gateway: a later
// feature (built-in tools exposed over MCP, a web-search backend) consumes it.

/// <summary>A tool a plugin gives (OpenCode's <c>tool</c> hook): a name an MCP client accepts, a description and the arguments as JSON Schema.</summary>
/// <param name="Schema">The arguments as a JSON Schema object, made by the host from the plugin's zod shape (or kept as the plugin gave it).</param>
public sealed record PluginTool(string Name, string? Description, JsonNode? Schema);

/// <summary>One plugin's tools, by the plugin's spec.</summary>
public sealed record PluginToolSet(string Spec, List<PluginTool> Tools);

/// <summary>A web search API a plugin gives (magpie's <c>search</c> hook, which OpenCode ignores).</summary>
/// <param name="Id"><c>[a-z0-9-]</c>, at most 32 characters; unique within its plugin.</param>
/// <param name="KeysUrl">Where the user gets a key.</param>
/// <param name="KeyOptional">The API also works without a key.</param>
/// <param name="NeedUrl">The user gives an address (a SearXNG's), passed to the plugin as <c>url</c>.</param>
/// <param name="Auth">The provider id the API signs in as (one of the same plugin's): it takes no key, and each call is one of that provider's accounts.</param>
public sealed record PluginSearchDef(string Id, string Name, string? KeysUrl, bool KeyOptional, bool NeedUrl, string? Auth);

/// <summary>A search API with the plugin that gives it.</summary>
public sealed record PluginSearchApi(string Spec, PluginSearchDef Def);

/// <summary>One page a plugin's search found.</summary>
public sealed record PluginSearchPage(string Title, string Url, string Text, string Age);

/// <summary>A plugin's search API turning the key away: the status it threw (401, 402, 403, 429 ...), to be treated as a built-in API's refusal.</summary>
public sealed class PluginSearchRefusedException(int status, string message, string? retryAfter) : Exception(message)
{
    public int Status { get; } = status;
    public string? RetryAfter { get; } = retryAfter;
}

public sealed partial class PluginHost
{
    /// <summary>How long a tool may run before its call is answered that it took too long (agents give a tool call as little as 60 s).</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(55);

    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HostSlack = TimeSpan.FromSeconds(5);

    /// <summary>The tools the loaded plugins give, in plugin order, starting the host if need be. A plugin that did not load gives none.</summary>
    public async Task<List<PluginToolSet>> ToolsAsync(CancellationToken ct = default) =>
        (await LoadedAsync(ct)).Where(l => l.Error is null && l.Tools.Count > 0).Select(l => new PluginToolSet(l.Spec, l.Tools)).ToList();

    /// <summary>
    /// Runs the plugin <paramref name="spec"/>'s tool <paramref name="name"/> in the host: its answer as text (a string
    /// answer as it is, an <c>{output}</c> object's output, anything else as JSON). <paramref name="args"/> is a JSON object
    /// (null for none); the host parses it with the tool's own schema first. <paramref name="agent"/> is what the tool's
    /// context names as the caller. A failure — an unknown tool, bad arguments, a throw, running past
    /// <see cref="ToolTimeout"/> — is a <see cref="PluginHostException"/>.
    /// </summary>
    public async Task<string> RunToolAsync(string spec, string name, JsonNode? args = null, string? agent = null, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ToolTimeout + HostSlack);
        var p = new JsonObject
        {
            ["spec"] = spec, ["name"] = name, ["args"] = args?.DeepClone() ?? new JsonObject(), ["agent"] = agent ?? "",
            ["ms"] = (long)ToolTimeout.TotalMilliseconds,
        };
        try
        {
            var result = await CallAsync("tool", p, limit.Token);
            return result?["text"]?.GetValue<string>() ?? "";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginHostException($"{name} took longer than {ToolTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>The web search APIs the loaded plugins give, in plugin order, starting the host if need be. The first of a <see cref="VendorId"/> wins.</summary>
    public async Task<List<PluginSearchApi>> SearchesAsync(CancellationToken ct = default)
    {
        var seen = new HashSet<string>();
        var list = new List<PluginSearchApi>();
        foreach (var l in await LoadedAsync(ct))
        {
            if (l.Error is not null) continue;
            foreach (var d in l.Searches)
            {
                var api = new PluginSearchApi(l.Spec, d);
                if (seen.Add(VendorId(api))) list.Add(api);
            }
        }
        return list;
    }

    /// <summary>
    /// How the API is told apart from the built-in ones and other plugins' (magpie's <c>VendorID</c>): the plugin's short name
    /// and the API's own id, so no plugin's id can take another's place.
    /// </summary>
    public static string VendorId(PluginSearchApi api)
    {
        var name = api.Spec.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' ? c : '-');
        return sb.ToString().Trim('-') + ":" + api.Def.Id;
    }

    /// <summary>
    /// Asks the plugin <paramref name="spec"/>'s search API <paramref name="id"/> for <paramref name="query"/> with the user's
    /// <paramref name="key"/> (and <paramref name="url"/> for one that <see cref="PluginSearchDef.NeedUrl"/>). A refusal of the
    /// key is a <see cref="PluginSearchRefusedException"/>; anything else a <see cref="PluginHostException"/>.
    /// </summary>
    public Task<List<PluginSearchPage>> SearchAsync(string spec, string id, string query, int count, string? key = null, string? url = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        SearchCoreAsync(new JsonObject { ["spec"] = spec, ["id"] = id, ["query"] = query, ["count"] = count, ["key"] = key ?? "", ["url"] = url ?? "" }, timeout, ct);

    /// <summary>
    /// <see cref="SearchAsync"/> for an API that signs in (<see cref="PluginSearchDef.Auth"/>): as the one account at its
    /// plugin-auth.json key (<c>provider</c> or <c>provider#slot</c>), through <paramref name="proxy"/> ("", "direct" or an address).
    /// The host renews that sign-in first when due; a signed-out account is an error, never another account.
    /// </summary>
    public Task<List<PluginSearchPage>> SearchAsAsync(string spec, string id, string query, int count, string account, string? proxy = null,
        TimeSpan? timeout = null, CancellationToken ct = default) =>
        string.IsNullOrEmpty(account)
            ? throw new PluginHostException("no account to search as")
            : SearchCoreAsync(new JsonObject { ["spec"] = spec, ["id"] = id, ["query"] = query, ["count"] = count, ["account"] = account, ["proxy"] = proxy ?? "" }, timeout, ct);

    private async Task<List<PluginSearchPage>> SearchCoreAsync(JsonObject p, TimeSpan? timeout, CancellationToken ct)
    {
        var within = timeout ?? SearchTimeout;
        p["ms"] = Math.Max((long)within.TotalMilliseconds, 1);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(within + HostSlack);
        JsonNode? result;
        try
        {
            result = await CallAsync("search", p, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginHostException("the search took too long");
        }
        if (result?["status"] is JsonValue s && s.TryGetValue<int>(out var status) && status != 0)
            throw new PluginSearchRefusedException(status, PluginProviders.Str(result, "error") ?? $"the search was refused ({status})", PluginProviders.Str(result, "retryAfter"));
        var pages = new List<PluginSearchPage>();
        foreach (var node in result?["pages"] as JsonArray ?? [])
            if (node is JsonObject o)
                pages.Add(new PluginSearchPage(Text(o, "title"), Text(o, "url"), Text(o, "text"), Text(o, "age")));
        return pages;
    }

    private static string Text(JsonObject o, string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    // The plugins the running host loaded, starting it when there is none and any enabled plugin runs in it.
    private async Task<List<PluginHostLoaded>> LoadedAsync(CancellationToken ct)
    {
        if (!await HasPluginsAsync(ct)) return [];
        await GetAsync(ct);
        return _current?.Loaded ?? [];
    }
}
