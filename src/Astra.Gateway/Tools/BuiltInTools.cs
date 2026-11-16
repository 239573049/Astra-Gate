using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Tools;
using Astra.Gateway.Http;
using Astra.Gateway.Mcp;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Plugins.Host;
using ModelContextProtocol.Protocol;

namespace Astra.Gateway.Tools;

/// <summary>
/// Astra's own tools as an MCP server (<see cref="IMcpBuiltIn"/>): <c>web_search</c> (when a search API is on), <c>web_fetch</c>,
/// <c>generate_image</c> (when the settings name an image model; the gateway's images route does the work, as the caller's token),
/// and every tool a provider plugin gives, named <c>&lt;plugin&gt;.&lt;tool&gt;</c>. The tool set and the idea (an agent mounts the same search
/// and fetch as MCP tools) are magpie's (MIT, internal/gateway/search_mcp.go, internal/gwtool).
/// </summary>
public sealed class BuiltInTools(ISearchService search, WebFetcher fetcher, ToolsSettingsService settings, GatewayPipeline pipeline,
    PrivacyGuardService? privacy = null, PluginHost? plugins = null) : IMcpBuiltIn
{
    public const string WebSearch = "web_search";
    public const string WebFetch = "web_fetch";
    public const string GenerateImage = "generate_image";

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Tool SearchTool() => new()
    {
        Name = WebSearch, Title = "Web search",
        Description = "Search the web and return pages with their title, address and an excerpt.",
        InputSchema = Schema("""{"type":"object","properties":{"query":{"type":"string","description":"The search query"},"count":{"type":"integer","description":"How many pages (1-20)"}},"required":["query"]}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = true },
    };

    private static Tool FetchTool() => new()
    {
        Name = WebFetch, Title = "Web fetch",
        Description = "Fetch a web page by its http(s) URL and return its text (an HTML page as Markdown, with its links). A long page comes in parts: the answer says the offset to read on from.",
        InputSchema = Schema("""{"type":"object","properties":{"url":{"type":"string","description":"The page's http:// or https:// URL"},"max_chars":{"type":"integer","description":"At most this many characters (default 20000, at most 100000)"},"offset":{"type":"integer","description":"The character to start from, to read on in a long page (default 0)"}},"required":["url"]}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = true },
    };

    private static Tool ImageTool() => new()
    {
        Name = GenerateImage, Title = "Generate image",
        Description = "Generate an image from a text description with the image model set in Astra. Returns the image.",
        InputSchema = Schema("""{"type":"object","properties":{"prompt":{"type":"string","description":"What the image shows"},"size":{"type":"string","description":"Optional size, e.g. 1024x1024"}},"required":["prompt"]}"""),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, OpenWorldHint = true },
    };

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct)
    {
        var list = new List<Tool>();
        if (search.HasSources) list.Add(SearchTool());
        list.Add(FetchTool());
        if (settings.Current.Image.Model.Length > 0) list.Add(ImageTool());
        foreach (var (name, tool) in await PluginToolsAsync(ct))
            list.Add(new Tool
            {
                Name = name, Title = tool.Name, Description = tool.Description,
                InputSchema = tool.Schema is null ? Schema("""{"type":"object"}""") : JsonDocument.Parse(tool.Schema.ToJsonString()).RootElement.Clone(),
            });
        return list;
    }

    /// <summary>The names the plugin tools are served under, with the plugin they run in.</summary>
    private async Task<List<(string Name, PluginTool Tool, string Spec)>> PluginToolsWithSpecAsync(CancellationToken ct)
    {
        var result = new List<(string, PluginTool, string)>();
        if (plugins is null) return result;
        List<PluginToolSet> sets;
        try { sets = await plugins.ToolsAsync(ct); }
        catch (Exception ex) when (ex is PluginHostException or IOException or InvalidOperationException) { return result; }
        var seen = new HashSet<string>(StringComparer.Ordinal) { WebSearch, WebFetch, GenerateImage };
        foreach (var set in sets)
            foreach (var tool in set.Tools)
            {
                var name = PluginToolName(set.Spec, tool.Name);
                if (seen.Add(name)) result.Add((name, tool, set.Spec));
            }
        return result;
    }

    private async Task<List<(string Name, PluginTool Tool)>> PluginToolsAsync(CancellationToken ct) =>
        (await PluginToolsWithSpecAsync(ct)).Select(t => (t.Name, t.Tool)).ToList();

    /// <summary>A plugin tool's name as MCP clients see it: the plugin's short name and the tool's, never with the <c>__</c> the aggregate view splits at.</summary>
    public static string PluginToolName(string spec, string tool)
    {
        static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '_');
            return sb.ToString().Replace("__", "_").Trim('_');
        }
        var name = spec.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return Clean(name) + "." + Clean(tool);
    }

    public async Task<CallToolResult> CallAsync(string tool, JsonObject? arguments, BuiltInCall call, CancellationToken ct)
    {
        try
        {
            switch (tool)
            {
                case WebSearch when search.HasSources: return await RunSearchAsync(arguments, ct);
                case WebFetch: return await RunFetchAsync(arguments, ct);
                case GenerateImage when settings.Current.Image.Model.Length > 0: return await RunImageAsync(arguments, call, ct);
            }
            var plugin = (await PluginToolsWithSpecAsync(ct)).FirstOrDefault(t => t.Name == tool);
            if (plugin.Tool is null) throw new McpToolNotFoundException(tool);
            var text = await plugins!.RunToolAsync(plugin.Spec, plugin.Tool.Name, arguments, "astra-mcp", ct);
            return Text(text);
        }
        catch (McpToolNotFoundException) { throw; }
        catch (SearchFailedException ex) { return Error(ex.Message); }
        catch (FetchRefusedException ex) { return Error(ex.Message); }
        catch (PluginHostException ex) { return Error(ex.Message); }
        catch (ArgumentException ex) { return Error(ex.Message); }
    }

    private async Task<CallToolResult> RunSearchAsync(JsonObject? args, CancellationToken ct)
    {
        var query = (args?["query"] as JsonValue)?.TryGetValue<string>(out var q) == true ? q.Trim() : "";
        if (query.Length == 0) throw new ArgumentException("query is required");
        int? count = args?["count"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : null;
        // What goes to a search API is the guarded text.
        if (privacy is not null)
        {
            var guarded = privacy.Inspect(query, null);
            if (guarded.Blocked) return Error("the query was blocked by the privacy policy");
            if (guarded.Applied && !guarded.DryRun && guarded.Body.Length > 0) query = guarded.Body;
        }
        var outcome = await search.SearchAsync(query, count, null, ct);
        return Text(SearchDialect.ResultText(new SearchRun("", query, outcome.Pages, null)));
    }

    private async Task<CallToolResult> RunFetchAsync(JsonObject? args, CancellationToken ct)
    {
        var url = (args?["url"] as JsonValue)?.TryGetValue<string>(out var u) == true ? u : "";
        if (url.Length == 0) throw new ArgumentException("url is required");
        int? max = args?["max_chars"] is JsonValue m && m.TryGetValue<int>(out var mi) ? mi : null;
        var offset = args?["offset"] is JsonValue o && o.TryGetValue<int>(out var oi) ? oi : 0;
        var page = await fetcher.FetchAsync(url, max, offset, ct);
        var sb = new StringBuilder();
        sb.Append("URL: ").Append(page.Url).Append('\n');
        if (page.Title is not null) sb.Append("Title: ").Append(page.Title).Append('\n');
        sb.Append("Characters: ").Append(page.Offset).Append('-').Append(page.Offset + page.Text.Length).Append(" of ").Append(page.Total).Append('\n');
        if (page.NextOffset is { } next) sb.Append("More: call again with offset=").Append(next).Append('\n');
        sb.Append('\n').Append(page.Text);
        return Text(sb.ToString());
    }

    private async Task<CallToolResult> RunImageAsync(JsonObject? args, BuiltInCall call, CancellationToken ct)
    {
        var prompt = (args?["prompt"] as JsonValue)?.TryGetValue<string>(out var p) == true ? p.Trim() : "";
        if (prompt.Length == 0) throw new ArgumentException("prompt is required");
        if (call.Origin is null) return Error("image generation needs a request to run as");
        var cfg = settings.Current.Image;
        var size = (args?["size"] as JsonValue)?.TryGetValue<string>(out var s) == true && s.Length > 0 ? s : cfg.Size;
        var body = new JsonObject { ["model"] = cfg.Model, ["prompt"] = prompt, ["n"] = 1 };
        if (size.Length > 0) body["size"] = size;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(GatewayJson.Options));
        var internalCall = InternalCall.Create(call.Origin, "/v1/images/generations", bytes, ct, null, headers =>
        {
            if (call.TokenKey is { Length: > 0 } key)
            {
                headers.Remove("x-api-key");
                headers.Authorization = "Bearer " + key;
            }
        });
        internalCall.BufferLimit = 64 * 1024 * 1024;
        await pipeline.HandleImagesAsync(internalCall.Context, ImageOperation.Generations);
        await internalCall.FinishAsync();
        JsonNode? reply = null;
        try { reply = JsonNode.Parse(internalCall.BodyText); }
        catch (JsonException) { /* below */ }
        if (internalCall.Status / 100 != 2)
            return Error(reply?["error"]?["message"]?.GetValue<string>() ?? $"the image request failed ({internalCall.Status})");
        var blocks = new List<ContentBlock>();
        foreach (var item in reply?["data"] as JsonArray ?? [])
        {
            if (item?["b64_json"]?.GetValue<string>() is { Length: > 0 } b64)
                blocks.Add(new ImageContentBlock { Data = Encoding.UTF8.GetBytes(b64), MimeType = "image/png" });
            else if (item?["url"]?.GetValue<string>() is { Length: > 0 } url)
                blocks.Add(new TextContentBlock { Text = "Image: " + url });
        }
        return blocks.Count == 0 ? Error("the image model returned no image") : new CallToolResult { Content = blocks };
    }

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
