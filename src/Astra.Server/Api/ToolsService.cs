using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tools;
using Astra.Gateway.Mcp;
using Astra.Gateway.Tools;
using Astra.Server.Hosting;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace Astra.Server.Api;

/// <summary>
/// Administration of Astra's built-in tools (<c>/api/tools</c>): the search APIs with their keys (masks only leave the server), the
/// "search on behalf of models" switch, web fetch limits, the image model, the tool list and running a tool (the CLI's <c>astra tool</c>).
/// </summary>
public sealed class ToolsService(ToolsSettingsService settings, ISecretProtector secrets, SearchService search,
    BuiltInTools builtIn, ServerOptions server)
{
    private const int MaxKeys = 20;
    private const int MaxKeyLength = 512;

    // ---------------------------------------------------------------- settings

    public async Task<ToolsSettingsDto> GetAsync(CancellationToken ct)
    {
        var s = settings.Current;
        var plugin = (await search.PluginApisAsync(ct)).ToDictionary(p => p.VendorId);
        var apis = new List<SearchApiDto>();
        foreach (var a in s.Search.Apis)
        {
            var info = SearchVendors.Find(a.Vendor);
            var p = plugin.GetValueOrDefault(a.Vendor);
            var isPlugin = info is null;
            var name = info?.Name ?? (p.Name ?? a.Vendor);
            apis.Add(new SearchApiDto(a.Vendor, name, a.Enabled, a.Pick, a.Url, info?.KeysUrl ?? p.KeysUrl, info?.KeyOptional ?? p.KeyOptional, info?.NeedsUrl ?? p.NeedsUrl,
                isPlugin, !isPlugin || p.VendorId is not null, a.Vendor == SearchVendors.Tavily, a.Keys.Select(k => KeyDto(a.Vendor, k)).ToList()));
        }
        var available = SearchVendors.All.Where(v => s.Search.Apis.All(a => a.Vendor != v.Id))
            .Select(v => new SearchVendorDto(v.Id, v.Name, v.KeysUrl, v.KeyOptional, v.NeedsUrl, false)).ToList();
        available.AddRange(plugin.Values.Where(p => !p.SignIn && s.Search.Apis.All(a => a.Vendor != p.VendorId))
            .Select(p => new SearchVendorDto(p.VendorId, p.Name, p.KeysUrl, p.KeyOptional, p.NeedsUrl, true)));
        return new ToolsSettingsDto(
            new WebSearchSettingsDto(s.Search.OnBehalf, s.Search.Mode, s.Search.ResultCount, apis, available),
            new WebFetchSettingsDto(s.Fetch.MaxBytes, s.Fetch.TimeoutSeconds, s.Fetch.MaxChars, s.Fetch.AllowPrivate),
            new ImageToolSettingsDto(s.Image.Model, s.Image.Size),
            server.GatewayBaseUrl.TrimEnd('/') + "/mcp/" + McpBuiltIn.Slug);
    }

    private SearchKeyDto KeyDto(string vendor, SearchKeyConfig k)
    {
        var plain = secrets.Unprotect(k.KeyEnc);
        var rest = search.RestOf(vendor, plain);
        return new SearchKeyDto(k.Id, SearchKeyRotation.Mask(plain), k.Enabled, rest?.Why, rest?.Until);
    }

    /// <summary>Changes the switches and limits: <c>search</c> {onBehalf, mode, resultCount}, <c>fetch</c> {maxBytes, timeoutSeconds, maxChars, allowPrivate}, <c>image</c> {model, size}.</summary>
    public async Task<ToolsSettingsDto> UpdateAsync(JsonObject body, CancellationToken ct)
    {
        var searchBody = body["search"] as JsonObject;
        var fetchBody = body["fetch"] as JsonObject;
        var imageBody = body["image"] as JsonObject;
        if (searchBody?["mode"] is { } mode && !SearchModes.All.Contains(Str(mode, "mode")))
            throw new AdminApiException(400, "mode must be auto or always");
        await settings.UpdateAsync(s =>
        {
            if (searchBody is not null)
            {
                if (searchBody["onBehalf"] is { } ob) s.Search.OnBehalf = Bool(ob, "onBehalf");
                if (searchBody["mode"] is { } m) s.Search.Mode = Str(m, "mode");
                if (searchBody["resultCount"] is { } rc) s.Search.ResultCount = Int(rc, "resultCount");
            }
            if (fetchBody is not null)
            {
                if (fetchBody["maxBytes"] is { } mb) s.Fetch.MaxBytes = Int(mb, "maxBytes");
                if (fetchBody["timeoutSeconds"] is { } ts) s.Fetch.TimeoutSeconds = Int(ts, "timeoutSeconds");
                if (fetchBody["maxChars"] is { } mc) s.Fetch.MaxChars = Int(mc, "maxChars");
                if (fetchBody["allowPrivate"] is { } ap) s.Fetch.AllowPrivate = Bool(ap, "allowPrivate");
            }
            if (imageBody is not null)
            {
                if (imageBody["model"] is { } im) s.Image.Model = Str(im, "model");
                if (imageBody["size"] is { } sz) s.Image.Size = Str(sz, "size");
            }
        }, ct);
        return await GetAsync(ct);
    }

    // ---------------------------------------------------------------- search APIs

    public async Task<ToolsSettingsDto> AddApiAsync(JsonObject body, CancellationToken ct)
    {
        var vendor = Str(body["vendor"], "vendor");
        var url = body["url"] is { } u ? Url(Str(u, "url")) : null;
        if (SearchVendors.Find(vendor) is null && !(await search.PluginApisAsync(ct)).Any(p => p.VendorId == vendor))
            throw new AdminApiException(400, "unknown search API");
        var needsUrl = SearchVendors.Find(vendor)?.NeedsUrl ?? (await search.PluginApisAsync(ct)).First(p => p.VendorId == vendor).NeedsUrl;
        if (needsUrl && url is null) throw new AdminApiException(400, "this search API needs its address");
        await settings.UpdateAsync(s =>
        {
            if (s.Search.Apis.Any(a => a.Vendor == vendor)) throw new AdminApiException(409, "this search API is already in the list");
            s.Search.Apis.Add(new SearchApiConfig { Vendor = vendor, Url = url });
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<ToolsSettingsDto> UpdateApiAsync(string vendor, JsonObject body, CancellationToken ct)
    {
        var url = body.ContainsKey("url") ? Url(body["url"] is null ? "" : Str(body["url"], "url")) : null;
        await settings.UpdateAsync(s =>
        {
            var api = Find(s, vendor);
            if (body["enabled"] is { } e) api.Enabled = Bool(e, "enabled");
            if (body["pick"] is { } p)
            {
                var pick = Str(p, "pick");
                if (pick is not (SearchPicks.Turns or SearchPicks.Order)) throw new AdminApiException(400, "pick must be turns or order");
                api.Pick = pick;
            }
            if (body.ContainsKey("url")) api.Url = url;
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<ToolsSettingsDto> DeleteApiAsync(string vendor, CancellationToken ct)
    {
        await settings.UpdateAsync(s =>
        {
            Find(s, vendor);
            s.Search.Apis.RemoveAll(a => a.Vendor == vendor);
        }, ct);
        return await GetAsync(ct);
    }

    /// <summary>The APIs in a new order; one the list names but does not hold is refused, one it leaves out goes last.</summary>
    public async Task<ToolsSettingsDto> ReorderApisAsync(JsonObject body, CancellationToken ct)
    {
        var order = Strings(body["order"], "order");
        await settings.UpdateAsync(s =>
        {
            foreach (var v in order) Find(s, v);
            s.Search.Apis = [.. order.Select(v => s.Search.Apis.First(a => a.Vendor == v)), .. s.Search.Apis.Where(a => !order.Contains(a.Vendor))];
        }, ct);
        return await GetAsync(ct);
    }

    // ---------------------------------------------------------------- keys

    public async Task<ToolsSettingsDto> AddKeyAsync(string vendor, JsonObject body, CancellationToken ct)
    {
        var key = Str(body["key"], "key").Trim();
        if (key.Length == 0 || key.Length > MaxKeyLength || key.Any(char.IsWhiteSpace)) throw new AdminApiException(400, "the key looks wrong");
        await settings.UpdateAsync(s =>
        {
            var api = Find(s, vendor);
            if (api.Keys.Count >= MaxKeys) throw new AdminApiException(400, $"at most {MaxKeys} keys per search API");
            if (api.Keys.Any(k => secrets.Unprotect(k.KeyEnc) == key)) throw new AdminApiException(409, "this key is already in the list");
            api.Keys.Add(new SearchKeyConfig { Id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), KeyEnc = secrets.Protect(key) });
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<ToolsSettingsDto> UpdateKeyAsync(string vendor, string id, JsonObject body, CancellationToken ct)
    {
        await settings.UpdateAsync(s =>
        {
            var key = Find(s, vendor).Keys.FirstOrDefault(k => k.Id == id) ?? throw new AdminApiException(404, "key not found");
            if (body["enabled"] is { } e) key.Enabled = Bool(e, "enabled");
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<ToolsSettingsDto> DeleteKeyAsync(string vendor, string id, CancellationToken ct)
    {
        await settings.UpdateAsync(s =>
        {
            var api = Find(s, vendor);
            if (api.Keys.RemoveAll(k => k.Id == id) == 0) throw new AdminApiException(404, "key not found");
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<ToolsSettingsDto> ReorderKeysAsync(string vendor, JsonObject body, CancellationToken ct)
    {
        var order = Strings(body["order"], "order");
        await settings.UpdateAsync(s =>
        {
            var api = Find(s, vendor);
            foreach (var id in order) if (api.Keys.All(k => k.Id != id)) throw new AdminApiException(404, "key not found");
            api.Keys = [.. order.Select(id => api.Keys.First(k => k.Id == id)), .. api.Keys.Where(k => !order.Contains(k.Id))];
        }, ct);
        return await GetAsync(ct);
    }

    public async Task<Dictionary<string, SearchKeyQuotaDto>> QuotasAsync(string vendor, bool fresh, CancellationToken ct)
    {
        var api = Find(settings.Current, vendor);
        var read = await search.QuotasAsync(api, fresh, ct);
        return read.ToDictionary(kv => kv.Key, kv => new SearchKeyQuotaDto(kv.Key, kv.Value.Known, kv.Value.Unlimited, kv.Value.Used, kv.Value.Limit, kv.Value.Of,
            kv.Value.Plan, kv.Value.PaygoLeft, kv.Value.UsedUp, kv.Value.Error));
    }

    /// <summary>Asks one search API alone (with all its keys, whether on or off) so the user sees what it answers.</summary>
    public async Task<SearchTestDto> TestAsync(string vendor, string? query, CancellationToken ct)
    {
        Find(settings.Current, vendor);
        try
        {
            var outcome = await search.SearchAsync(string.IsNullOrWhiteSpace(query) ? "astra gateway" : query.Trim(), 3, vendor, ct);
            return new SearchTestDto(true, outcome.Source, outcome.Pages.Count, outcome.ElapsedMs, null, outcome.Pages.FirstOrDefault()?.Title);
        }
        catch (SearchFailedException ex)
        {
            return new SearchTestDto(false, null, 0, 0, ex.Message, null);
        }
    }

    // ---------------------------------------------------------------- tools

    public async Task<List<ToolInfoDto>> ListToolsAsync(CancellationToken ct)
    {
        var s = settings.Current;
        var offered = (await builtIn.ListToolsAsync(ct)).ToDictionary(t => t.Name);
        var list = new List<ToolInfoDto>
        {
            Info(BuiltInTools.WebSearch, "Web search", "Search the web with the search APIs in Tools settings.", offered, "no search API is on"),
            Info(BuiltInTools.WebFetch, "Web fetch", "Fetch a web page as readable text.", offered, null),
            Info(BuiltInTools.GenerateImage, "Generate image", "Generate an image with the image model set below.", offered, "no image model is set"),
        };
        foreach (var t in offered.Values.Where(t => t.Name is not (BuiltInTools.WebSearch or BuiltInTools.WebFetch or BuiltInTools.GenerateImage)))
            list.Add(new ToolInfoDto(t.Name, t.Title ?? t.Name, t.Description, "plugin", true, null));
        return list;
    }

    private static ToolInfoDto Info(string name, string title, string description, Dictionary<string, Tool> offered, string? reason) =>
        new(name, title, description, "builtin", offered.ContainsKey(name), offered.ContainsKey(name) ? null : reason);

    /// <summary>Runs a tool for the CLI / admin: <paramref name="tokenKey"/> is the gateway key an image generation goes out as (none: the caller's own request).</summary>
    public async Task<ToolRunDto> RunAsync(HttpContext ctx, string name, JsonObject? args, string? tokenKey, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await builtIn.CallAsync(name, args, new BuiltInCall(ctx, tokenKey), ct);
        }
        catch (McpToolNotFoundException)
        {
            throw new AdminApiException(404, $"unknown tool: {name}");
        }
        var content = new List<ToolContentDto>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t: content.Add(new ToolContentDto("text", t.Text, null, null)); break;
                case ImageContentBlock i: content.Add(new ToolContentDto("image", null, i.MimeType, System.Text.Encoding.UTF8.GetString(i.Data.Span))); break;
            }
        }
        return new ToolRunDto(result.IsError == true, content);
    }

    // ---------------------------------------------------------------- helpers

    private static SearchApiConfig Find(ToolsSettings s, string vendor) =>
        s.Search.Apis.FirstOrDefault(a => a.Vendor == vendor) ?? throw new AdminApiException(404, "search API not found");

    private static string Str(JsonNode? node, string field) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new AdminApiException(400, $"{field} must be a string");

    private static bool Bool(JsonNode node, string field) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : throw new AdminApiException(400, $"{field} must be a boolean");

    private static int Int(JsonNode node, string field) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : throw new AdminApiException(400, $"{field} must be a number");

    private static List<string> Strings(JsonNode? node, string field) =>
        node is JsonArray a ? a.Select(x => Str(x, field)).ToList() : throw new AdminApiException(400, $"{field} must be an array");

    private static string? Url(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new AdminApiException(400, "the address must be an http(s) URL without a user name");
        return text;
    }
}
