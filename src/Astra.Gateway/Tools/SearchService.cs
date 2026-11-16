using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Astra.Core.Tools;
using Astra.Gateway.Pipeline;
using Astra.Plugins.Host;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Tools;

/// <summary>What a search found and which API found it.</summary>
public sealed record SearchOutcome(IReadOnlyList<FoundPage> Pages, string Source, long ElapsedMs);

/// <summary>No search API could answer: every reason, one per API (never with the query or a key).</summary>
public sealed class SearchFailedException(string message) : Exception(message);

/// <summary>What one key of a search API has used of its limit (Tavily reports it). Unknown is never 0 used.</summary>
public sealed record SearchKeyQuota(string KeyId, bool Known, bool Unlimited, long Used, long Limit, string? Of, string? Plan, long PaygoLeft, bool UsedUp, string? Error);

/// <summary>The web search the gateway runs itself: for a model that cannot search, and for the built-in MCP tool.</summary>
public interface ISearchService
{
    /// <summary>Whether at least one API could be asked.</summary>
    bool HasSources { get; }

    /// <summary>
    /// Asks the enabled APIs in their order until one finds pages. <paramref name="query"/> must already be the guarded text.
    /// <paramref name="only"/> asks that one vendor alone (the settings page's test button).
    /// </summary>
    /// <exception cref="SearchFailedException">No API found anything.</exception>
    Task<SearchOutcome> SearchAsync(string query, int? count, string? only, CancellationToken ct);
}

/// <summary>
/// <see cref="ISearchService"/> over Astra's settings (magpie's internal/gateway/search_api.go, MIT): each API with its keys in
/// their order (<see cref="SearchKeyRotation"/>), on to the next key while one is refused, on to the next API while one fails.
/// A plugin's search API (<see cref="PluginHost.SearchesAsync"/>) is listed beside the built-in ones.
/// </summary>
public sealed class SearchService(ToolsSettingsService settings, ISecretProtector secrets, GatewayHttpClients http,
    SearchKeyRotation rotation, ILogger<SearchService> logger, PluginHost? plugins = null) : ISearchService
{
    /// <summary>One key of one API is given this long to answer.</summary>
    public static readonly TimeSpan KeyTimeout = TimeSpan.FromSeconds(15);

    private const int MaxReply = 4 << 20;

    public bool HasSources => settings.Current.Search.Apis.Any(a => a.Enabled && (a.Keys.Any(k => k.Enabled) || KeyOptional(a.Vendor)));

    private static bool KeyOptional(string vendor) => SearchVendors.Find(vendor)?.KeyOptional ?? vendor.Contains(':');

    public async Task<SearchOutcome> SearchAsync(string query, int? count, string? only, CancellationToken ct)
    {
        var cfg = settings.Current.Search;
        var n = Math.Clamp(count ?? cfg.ResultCount, 1, 20);
        var errors = new List<string>();
        foreach (var api in cfg.Apis)
        {
            if (only is null ? !api.Enabled : api.Vendor != only) continue;
            var name = NameOf(api.Vendor);
            var keys = api.Keys.Where(k => only is not null || k.Enabled).Select(k => secrets.Unprotect(k.KeyEnc)).Where(k => k.Length > 0).ToList();
            if (keys.Count == 0)
            {
                if (!KeyOptional(api.Vendor)) { errors.Add($"{name}: no key"); continue; }
                keys.Add("");
            }
            foreach (var i in rotation.Order(api.Vendor, keys, api.Pick, DateTimeOffset.UtcNow))
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    var pages = await AskAsync(api, keys[i], query, n, ct);
                    rotation.Wake(api.Vendor, keys[i]);
                    if (pages.Count == 0) { errors.Add($"{name} found nothing"); break; }
                    return new SearchOutcome(pages, name, watch.ElapsedMilliseconds);
                }
                catch (SearchRefusedException refused)
                {
                    if (keys[i].Length > 0)
                    {
                        var (rest, why) = SearchKeyRotation.RestFor(api.Vendor, refused.Status, refused.RetryAfter, DateTimeOffset.UtcNow);
                        rotation.Rest(api.Vendor, keys[i], rest, why, DateTimeOffset.UtcNow);
                    }
                    errors.Add(Label(name, keys.Count, i, keys[i]) + Scrub(refused.Message, keys[i]));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Not about the key (no answer in time, unreadable answer): the next key would fail the same way.
                    errors.Add(Label(name, keys.Count, i, keys[i]) + Scrub(Describe(ex), keys[i]));
                    break;
                }
            }
        }
        if (errors.Count == 0) throw new SearchFailedException("no search API is on");
        logger.LogDebug("网页搜索失败：{Errors}", string.Join("; ", errors));
        throw new SearchFailedException(string.Join("; ", errors));
    }

    private static string Label(string name, int keys, int index, string key) =>
        keys > 1 ? $"{name} key {index + 1} ({SearchKeyRotation.Mask(key)}): " : $"{name}: ";

    private static string Scrub(string message, string key) => key.Length >= 8 ? message.Replace(key, SearchKeyRotation.Mask(key)) : message;

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "no answer in time",
        HttpRequestException h => h.Message,
        _ => ex.Message,
    };

    private string NameOf(string vendor) => SearchVendors.Find(vendor)?.Name ?? vendor;

    private async Task<List<FoundPage>> AskAsync(SearchApiConfig api, string key, string query, int n, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(KeyTimeout);
        if (SearchVendors.Find(api.Vendor) is null) return await AskPluginAsync(api, key, query, n, limit.Token);
        using var request = SearchApis.BuildRequest(api.Vendor, api.Url, key, query, n);
        // The key goes to the address the API is set at and nowhere else: a redirect is not followed, it is an error.
        using var response = await http.For((string?)null).SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token);
        var body = await ReadAsync(response, limit.Token);
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400 && response.Headers.Location is { } to)
            throw new InvalidOperationException($"{status}: it sends the search on to {to.Host}, which is not given its key; set that address instead");
        var (effective, said) = SearchApis.StatusOf(api.Vendor, status, body);
        if (effective >= 300)
        {
            var message = said ?? SearchApis.ErrorMessage(body, $"{status} {response.ReasonPhrase}".Trim());
            var retry = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
            if (effective is 401 or 402 or 403 or 429 || (api.Vendor == SearchVendors.Tavily && effective is 432 or 433))
                throw new SearchRefusedException(effective, message, retry);
            throw new InvalidOperationException(message);
        }
        List<FoundPage> pages;
        try { pages = SearchApis.ReadReply(api.Vendor, body); }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new InvalidOperationException("unexpected reply: " + ex.Message); }
        return pages.Count > n ? pages[..n] : pages;
    }

    private async Task<List<FoundPage>> AskPluginAsync(SearchApiConfig api, string key, string query, int n, CancellationToken ct)
    {
        if (plugins is null) throw new InvalidOperationException("plugins are not running");
        var found = (await plugins.SearchesAsync(ct)).FirstOrDefault(s => PluginHost.VendorId(s) == api.Vendor)
                    ?? throw new InvalidOperationException("the plugin that gave this search API is not on");
        if (found.Def.Auth is not null) throw new InvalidOperationException("search APIs that sign in as a provider are not supported yet");
        try
        {
            var pages = await plugins.SearchAsync(found.Spec, found.Def.Id, query, n, key.Length > 0 ? key : null, api.Url, KeyTimeout, ct);
            return pages.Where(p => p.Url.Length > 0)
                .Select(p => new FoundPage(p.Title.Length > 0 ? p.Title : p.Url, p.Url, SearchApis.Clip(SearchApis.Plain(p.Text), SearchApis.PageText), p.Age))
                .Take(n).ToList();
        }
        catch (PluginSearchRefusedException refused)
        {
            throw new SearchRefusedException(refused.Status, refused.Message, refused.RetryAfter);
        }
        catch (PluginHostException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxReply && (read = await stream.ReadAsync(chunk, ct)) > 0) buffer.Write(chunk, 0, read);
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ---------------------------------------------------------------- what the settings page shows

    /// <summary>The search APIs a plugin gives, for the settings list (empty when plugins are not running).</summary>
    public async Task<IReadOnlyList<(string VendorId, string Name, string? KeysUrl, bool KeyOptional, bool NeedsUrl, bool SignIn)>> PluginApisAsync(CancellationToken ct)
    {
        if (plugins is null) return [];
        try
        {
            return (await plugins.SearchesAsync(ct))
                .Select(s => (PluginHost.VendorId(s), s.Def.Name, s.Def.KeysUrl, s.Def.KeyOptional, s.Def.NeedUrl, s.Def.Auth is not null)).ToList();
        }
        catch (Exception ex) when (ex is PluginHostException or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "插件搜索 API 列表读取失败");
            return [];
        }
    }

    /// <summary>When a key rests (a search asks it last) and why.</summary>
    public (string Why, DateTimeOffset Until)? RestOf(string vendor, string key) =>
        rotation.RestOf(vendor, key, DateTimeOffset.UtcNow) is { } r ? (r.Why, r.Until) : null;

    // Tavily allows /usage 10 times in 10 minutes: a read is kept two minutes.
    private static readonly TimeSpan QuotaKeep = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, (DateTimeOffset At, SearchKeyQuota Quota)> _quotas = [];

    /// <summary>What each key of a Tavily API has used; empty for another vendor. A key found used up rests, one found with credit wakes.</summary>
    public async Task<Dictionary<string, SearchKeyQuota>> QuotasAsync(SearchApiConfig api, bool fresh, CancellationToken ct)
    {
        var result = new Dictionary<string, SearchKeyQuota>();
        if (api.Vendor != SearchVendors.Tavily) return result;
        foreach (var k in api.Keys)
        {
            var key = secrets.Unprotect(k.KeyEnc);
            if (key.Length == 0) continue;
            var id = SearchKeyRotation.KeyId(api.Vendor + " " + (api.Url ?? ""), key);
            SearchKeyQuota? kept = null;
            lock (_quotas)
                if (!fresh && _quotas.TryGetValue(id, out var v) && DateTimeOffset.UtcNow - v.At < QuotaKeep) kept = v.Quota;
            var quota = kept ?? await ReadTavilyAsync(api, key, k.Id, ct);
            lock (_quotas) _quotas[id] = (DateTimeOffset.UtcNow, quota);
            if (quota.UsedUp) rotation.Rest(api.Vendor, key, SearchKeyRotation.OutOfCreditRest, "used up", DateTimeOffset.UtcNow);
            else if (quota.Known && rotation.RestOf(api.Vendor, key, DateTimeOffset.UtcNow) is { Why: "used up" or "out of credit" }) rotation.Wake(api.Vendor, key);
            result[k.Id] = quota;
        }
        return result;
    }

    private async Task<SearchKeyQuota> ReadTavilyAsync(SearchApiConfig api, string key, string id, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            var root = SearchApis.BaseOf(SearchVendors.Find(SearchVendors.Tavily)!, api.Url);
            using var request = new HttpRequestMessage(HttpMethod.Get, root + "/usage");
            request.Headers.Authorization = new("Bearer", key);
            request.Headers.Accept.Add(new("application/json"));
            using var response = await http.For((string?)null).SendAsync(request, limit.Token);
            var body = await ReadAsync(response, limit.Token);
            if (!response.IsSuccessStatusCode) return Unknown(id, Scrub(SearchApis.ErrorMessage(body, $"{(int)response.StatusCode}"), key));
            return ReadTavilyUsage(id, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Unknown(id, Scrub(Describe(ex), key));
        }
    }

    private static SearchKeyQuota Unknown(string id, string error) => new(id, false, false, 0, 0, null, null, 0, false, error);

    /// <summary>
    /// Tavily's <c>GET /usage</c> reply as what the key has left: the key's own limit when it has one, else its account plan's
    /// (the plan's usage, as the account's other keys spend it too). A reply missing what it takes is unknown, never 0 used.
    /// </summary>
    public static SearchKeyQuota ReadTavilyUsage(string id, string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return Unknown(id, "unexpected reply: not JSON"); }
        if (root is not JsonObject top || top["key"] is not JsonObject key) return Unknown(id, "unexpected reply: no key in the reply");
        var account = top["account"] as JsonObject ?? new JsonObject();
        // (value, isNull, present)
        static (long, bool, bool) Num(JsonObject o, string field)
        {
            if (!o.TryGetPropertyValue(field, out var v)) return (0, false, false);
            if (v is null) return (0, true, true);
            return v is JsonValue jv && jv.TryGetValue<double>(out var d) ? ((long)d, false, true) : (0, false, false);
        }
        var plan = account["current_plan"] is JsonValue pv && pv.TryGetValue<string>(out var planName) ? planName : null;
        long paygo = 0;
        if (Num(account, "paygo_limit") is (var pl, false, true) && Num(account, "paygo_usage") is (var pu, false, true)) paygo = Math.Max(0, pl - pu);
        var (keyUsed, keyUsedNull, keyUsedOk) = Num(key, "usage");
        var (keyLimit, keyLimitNull, keyLimitOk) = Num(key, "limit");
        if (!keyLimitOk) return Unknown(id, "unexpected reply: no key limit in the reply");
        var (planLimit, planLimitNull, planOk) = Num(account, "plan_limit");
        long used, limit;
        string of;
        bool unlimited = false;
        if (!keyLimitNull)
        {
            if (!keyUsedOk || keyUsedNull) return Unknown(id, "unexpected reply: no key usage in the reply");
            (used, limit, of) = (keyUsed, keyLimit, "key");
        }
        else if (planOk && !planLimitNull)
        {
            var (planUsed, planUsedNull, planUsedOk) = Num(account, "plan_usage");
            if (!planUsedOk || planUsedNull) return Unknown(id, "unexpected reply: no plan usage in the reply");
            (used, limit, of) = (planUsed, planLimit, "plan");
        }
        else if (planOk)
        {
            (used, limit, of, unlimited) = (keyUsed, 0, "", true);
        }
        else return Unknown(id, "unexpected reply: no plan limit in the reply");
        var usedUp = !unlimited && used >= limit && (of == "key" || paygo == 0);
        return new SearchKeyQuota(id, true, unlimited, used, limit, of.Length == 0 ? null : of, plan, paygo, usedUp, null);
    }
}
