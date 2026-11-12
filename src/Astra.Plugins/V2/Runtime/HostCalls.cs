using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Plugins;

namespace Astra.Plugins.V2.Runtime;

/// <summary>
/// The host call table (design/plugin-v2 01 §5): every <c>astra.*</c> API is a named call with JSON in and out, and this is
/// the one place that checks its permission, applies its limits and writes the activity log. Calls are sync (answered
/// during the JS slice: no I/O) or async (a ticket the main context settles when the task ends).
/// </summary>
internal sealed class HostCalls(PluginV2Host host, IPluginV2Services services)
{
    /// <summary>Most a plugin's storage may hold, keys and values together.</summary>
    public const long StorageQuota = 10 * 1024 * 1024;

    /// <summary>Most one stored value may be.</summary>
    public const int ValueLimit = 1024 * 1024;

    /// <summary>Most an http.fetch reply body may be.</summary>
    public const int ResponseLimit = 10 * 1024 * 1024;

    private static readonly HashSet<string> SyncCalls =
    [
        "env", "has", "config.get", "i18n.messages", "log", "ui.components", "ui.refresh", "ui.data", "events.emit", "events.subscribe",
        "notifications.send", "permissions.has", "crypto.sha256", "crypto.hmac", "crypto.uuid", "crypto.random",
        .. CryptoCalls.Names,
    ];

    /// <summary>The permission each call needs; calls not listed need none.</summary>
    private static readonly Dictionary<string, string> Needs = new()
    {
        ["storage.get"] = PluginPermissions.Storage,
        ["storage.set"] = PluginPermissions.Storage,
        ["storage.delete"] = PluginPermissions.Storage,
        ["storage.list"] = PluginPermissions.Storage,
        ["secrets.get"] = PluginPermissions.Secrets,
        ["secrets.set"] = PluginPermissions.Secrets,
        ["secrets.delete"] = PluginPermissions.Secrets,
        ["notifications.send"] = PluginPermissions.Notifications,
        ["jobs.runNow"] = PluginPermissions.Jobs,
        ["events.emit"] = PluginPermissions.Events,
        ["ui.ask"] = PluginPermissions.Ui,
        ["ui.flush"] = PluginPermissions.Ui,
        ["ui.refresh"] = PluginPermissions.Ui,
        ["ui.data"] = PluginPermissions.Ui,
        ["ui.files.read"] = PluginPermissions.Ui,
        ["providers.accounts.list"] = PluginPermissions.Providers,
        ["providers.accounts.add"] = PluginPermissions.Providers,
        ["providers.accounts.update"] = PluginPermissions.Providers,
        ["providers.accounts.remove"] = PluginPermissions.Providers,
        ["providers.accounts.secret"] = PluginPermissions.Providers,
        ["data.requests.query"] = PluginPermissions.RequestsRead,
        ["data.requests.stats"] = PluginPermissions.RequestsRead,
        ["data.providers.list"] = PluginPermissions.ProvidersRead,
        ["data.models.list"] = PluginPermissions.ModelsRead,
        ["data.tokens.list"] = PluginPermissions.TokensRead,
    };

    /// <summary>Whether <paramref name="name"/> is a call this host has (for <c>astra.has</c>).</summary>
    public static bool Exists(string name) => SyncCalls.Contains(name) || Needs.ContainsKey(name)
        || name is "http.fetch" or "http.read" or "http.close" or "commands.execute" or "permissions.request" or "timers.sleep";

    /// <summary>The synchronous calls a gateway hook may make (no I/O, nothing that needs a view or a subscription).</summary>
    public static readonly HashSet<string> GatewayCalls =
        ["env", "has", "config.get", "i18n.messages", "log", "ui.components", "permissions.has", "crypto.sha256", "crypto.uuid", "crypto.random", .. CryptoCalls.Names];

    public HostCallResult Sync(PluginV2Instance p, string name, string? argsJson, bool gateway = false)
    {
        if (gateway && !GatewayCalls.Contains(name)) return HostCallResult.Fail("not_supported", $"{name} is not available in gateway hooks");
        if (!SyncCalls.Contains(name)) return HostCallResult.Fail("not_supported", $"{name} is not a synchronous call");
        if (Denied(p, name) is { } denied) return denied;
        var args = Parse(argsJson);
        if (CryptoCalls.Names.Contains(name)) return CryptoCalls.Call(name, args);
        switch (name)
        {
            case "env":
                return HostCallResult.Value(new JsonObject
                {
                    ["astraVersion"] = services.HostVersion,
                    ["api"] = PluginV2Versions.Api.ToString(),
                    ["ui"] = PluginV2Versions.Ui.ToString(),
                    ["platform"] = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux",
                    ["plugin"] = new JsonObject { ["id"] = p.Id, ["version"] = p.Manifest.Version },
                    ["dev"] = p.Dev,
                });
            case "has":
                return HostCallResult.Value(Has(p, Str(args) ?? ""));
            case "config.get":
                return HostCallResult.Value(p.Config());
            case "i18n.messages":
                return HostCallResult.Value(p.Messages());
            case "log":
            {
                var o = args as JsonObject;
                var level = o?["level"]?.GetValue<string>() ?? "info";
                var message = o?["message"]?.GetValue<string>() ?? "";
                if (o?["fields"] is JsonObject f) message += " " + f.ToJsonString();
                host.Log(p, level is "debug" or "info" or "warn" or "error" ? level : "info", message);
                return HostCallResult.Value(null);
            }
            case "ui.components":
                return HostCallResult.Value(new JsonArray([.. host.Ui.Schema.Names.Select(n => (JsonNode?)JsonValue.Create(n))]));
            case "ui.refresh":
                host.Ui.Refresh(p, (args as JsonObject)?["session"]?.GetValue<string>());
                return HostCallResult.Value(null);
            case "ui.data":
                return host.Ui.Data(p, args as JsonObject) is { } why ? HostCallResult.Fail("invalid_argument", why) : HostCallResult.Value(null);
            case "events.emit":
            {
                var o = args as JsonObject;
                var ev = o?["name"]?.GetValue<string>() ?? "";
                if (p.Manifest.Events.All(e => e.Name != ev))
                    return HostCallResult.Fail("invalid_argument", $"event \"{ev}\" is not declared in contributes.events");
                host.Publish(p.Id + ":" + ev, o?["payload"]?.DeepClone(), p.Id);
                return HostCallResult.Value(null);
            }
            case "events.subscribe":
                return host.Subscribe(p, Str(args) ?? "") is { } why2 ? HostCallResult.Fail("permission_denied", why2) : HostCallResult.Value(null);
            case "notifications.send":
            {
                var o = args as JsonObject;
                services.Notify(p.Id, Clip(o?["title"]?.GetValue<string>() ?? p.Manifest.DisplayName("en"), 200),
                    o?["body"] is JsonValue b && b.TryGetValue<string>(out var body) ? Clip(body, 2000) : null,
                    o?["tone"]?.GetValue<string>() is { } tone && tone is "success" or "warning" or "danger" ? tone : "neutral");
                return HostCallResult.Value(null);
            }
            case "permissions.has":
                return HostCallResult.Value(PluginPermissions.Holds(p.Granted, Str(args) ?? ""));
            case "crypto.sha256":
                return HostCallResult.Value(Convert.ToHexStringLower(SHA256.HashData(Bytes(args))));
            case "crypto.uuid":
                return HostCallResult.Value(Guid.NewGuid().ToString());
            case "crypto.random":
            {
                var n = args is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Clamp(d, 1, 4096) : 16;
                return HostCallResult.Value(Convert.ToBase64String(RandomNumberGenerator.GetBytes(n)));
            }
        }
        return HostCallResult.Fail("not_supported", name);
    }

    public async Task<HostCallResult> AsyncCall(PluginV2Instance p, string name, string? argsJson, CancellationToken ct)
    {
        if (SyncCalls.Contains(name)) return Sync(p, name, argsJson);
        if (Denied(p, name) is { } denied) return denied;
        var args = Parse(argsJson);
        try
        {
            switch (name)
            {
                case "storage.get":
                {
                    var json = await services.Data.GetAsync(p.Id, Key(args), ct);
                    return json is null ? HostCallResult.Value(null) : HostCallResult.Raw(json);
                }
                case "storage.set":
                {
                    var o = args as JsonObject;
                    var key = Key(o?["key"]);
                    var value = o?["value"]?.ToJsonString() ?? "null";
                    if (value.Length > ValueLimit) return HostCallResult.Fail("quota_exceeded", $"a value may be {ValueLimit / 1024} KiB at most");
                    if (await services.Data.SizeAsync(p.Id, ct) + key.Length + value.Length > StorageQuota)
                        return HostCallResult.Fail("quota_exceeded", $"the plugin's storage is full ({StorageQuota / 1024 / 1024} MiB)");
                    await services.Data.SetAsync(p.Id, key, value, ct);
                    host.StorageChanged(p, key, value);
                    return HostCallResult.Value(null);
                }
                case "storage.delete":
                {
                    var key = Key(args);
                    var removed = await services.Data.DeleteAsync(p.Id, key, ct);
                    host.StorageChanged(p, key, null);
                    return HostCallResult.Value(removed);
                }
                case "storage.list":
                {
                    var o = args as JsonObject;
                    var limit = o?["limit"] is JsonValue lv && lv.TryGetValue<int>(out var l) ? Math.Clamp(l, 1, 1000) : 100;
                    var items = await services.Data.ListAsync(p.Id, o?["prefix"]?.GetValue<string>() ?? "", limit + 1, o?["cursor"]?.GetValue<string>(), ct);
                    var page = items.Take(limit).ToList();
                    var result = new JsonObject
                    {
                        ["items"] = new JsonArray([.. page.Select(i => (JsonNode?)new JsonObject { ["key"] = i.Key, ["value"] = JsonNode.Parse(i.ValueJson) })]),
                    };
                    if (items.Count > limit) result["cursor"] = page[^1].Key;
                    return HostCallResult.Value(result);
                }
                case "secrets.get":
                {
                    var name2 = Key(args);
                    var stored = await services.Data.GetSecretAsync(p.Id, name2, ct);
                    await host.ActivityAsync(p, name, name2, stored is null ? "missing" : "ok");
                    return HostCallResult.Value(stored is null ? null : services.Unprotect(stored));
                }
                case "secrets.set":
                {
                    var o = args as JsonObject;
                    var name2 = Key(o?["name"]);
                    var value = o?["value"]?.GetValue<string>() ?? "";
                    if (value.Length > 64 * 1024) return HostCallResult.Fail("quota_exceeded", "a secret may be 64 KiB at most");
                    await services.Data.SetSecretAsync(p.Id, name2, services.Protect(value), ct);
                    await host.ActivityAsync(p, name, name2, "ok");
                    return HostCallResult.Value(null);
                }
                case "secrets.delete":
                {
                    var name2 = Key(args);
                    var removed = await services.Data.DeleteSecretAsync(p.Id, name2, ct);
                    await host.ActivityAsync(p, name, name2, removed ? "ok" : "missing");
                    return HostCallResult.Value(removed);
                }
                case "http.fetch":
                    return await FetchAsync(p, args as JsonObject, ct);
                case "providers.accounts.list":
                case "providers.accounts.add":
                case "providers.accounts.update":
                case "providers.accounts.remove":
                case "providers.accounts.secret":
                    return await AccountsAsync(p, name["providers.accounts.".Length..], args as JsonObject, ct);
                case "http.read":
                    return await ReadStreamAsync(p, args, ct);
                case "http.close":
                    CloseStream(p.Id, args?.GetValue<long>() ?? 0);
                    return HostCallResult.Value(null);
                case "commands.execute":
                {
                    var o = args as JsonObject;
                    var id = o?["id"]?.GetValue<string>() ?? "";
                    var result = await host.ExecuteCommandAsync(id.Contains(':') ? id : p.Id + ":" + id,
                        o?["args"]?.DeepClone(), null, ct);
                    return HostCallResult.Value(result);
                }
                case "jobs.runNow":
                {
                    var id = Str(args) ?? "";
                    if (p.Manifest.Jobs.All(j => j.Id != id)) return HostCallResult.Fail("not_found", $"no job \"{id}\"");
                    await host.RunJobAsync(p, id, ct);
                    return HostCallResult.Value(null);
                }
                case "permissions.request":
                {
                    var asked = args is JsonArray a ? a.Select(x => x?.GetValue<string>()).OfType<string>().ToList() : [];
                    var answer = await host.RequestPermissionsAsync(p, asked, ct);
                    return HostCallResult.Value(answer);
                }
                case "timers.sleep":
                {
                    var ms = args is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Clamp(d, 0, TimeSpan.FromDays(1).TotalMilliseconds) : 0;
                    await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
                    return HostCallResult.Value(null);
                }
                case "ui.ask":
                    return HostCallResult.Value(await host.Ui.AskAsync(p, args as JsonObject, ct));
                case "ui.flush":
                    await host.Ui.FlushAsync(p, args as JsonObject, ct);
                    return HostCallResult.Value(null);
                case "ui.files.read":
                    return host.Ui.ReadFile(p, Str(args) ?? "") is { } file ? HostCallResult.Value(file) : HostCallResult.Fail("not_found", "no such file (refs last 10 minutes)");
                case "data.requests.query":
                    return HostCallResult.Value(await services.QueryAsync("requests.query", args, ct));
                case "data.requests.stats":
                    return HostCallResult.Value(await services.QueryAsync("requests.stats", args, ct));
                case "data.providers.list":
                    return HostCallResult.Value(await services.QueryAsync("providers.list", args, ct));
                case "data.models.list":
                    return HostCallResult.Value(await services.QueryAsync("models.list", args, ct));
                case "data.tokens.list":
                    return HostCallResult.Value(await services.QueryAsync("tokens.list", args, ct));
            }
        }
        catch (PluginCallException e)
        {
            return HostCallResult.Fail(e.Code, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return HostCallResult.Fail("invalid_argument", e.Message);
        }
        return HostCallResult.Fail("not_supported", $"{name} is not a call this Astra has");
    }

    private HostCallResult? Denied(PluginV2Instance p, string name)
    {
        if (!Needs.TryGetValue(name, out var need) || PluginPermissions.Holds(p.Granted, need)) return null;
        return HostCallResult.Fail("permission_denied", $"{name} needs the permission \"{need}\"");
    }

    private bool Has(PluginV2Instance p, string what)
    {
        var at = what.IndexOf('@');
        var id = at < 0 ? what : what[..at];
        var version = at < 0 ? 0 : int.TryParse(what[(at + 1)..], out var v) ? v : int.MaxValue;
        if (ExtensionPoints.Find(id) is { } point) return point.Version >= version;
        if (PluginPermissions.Problem(id) is null) return PluginPermissions.Holds(p.Granted, id);
        var prefix = id + ".";
        var calls = Needs.Keys.Concat(SyncCalls).Concat(["http.fetch", "commands.execute", "permissions.request", "timers.sleep"])
            .Where(c => c == id || c.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return calls.Count > 0 && calls.Any(c => Denied(p, c) is null && (c != "http.fetch" || p.Granted.Any(g => g.StartsWith(PluginPermissions.NetPrefix, StringComparison.Ordinal))));
    }

    // http.fetch: origins from the net: grants only, redirects followed by hand with each hop checked (or, with
    // redirect: 'manual' / 'error', not followed), no private addresses (the plugin must not reach this machine's or the
    // LAN's services through Astra), a capped body. A request may carry a Cookie header and the reply's Set-Cookie lines
    // come back as setCookies (there is no jar: the plugin keeps what it needs); Cookie, Authorization and
    // Proxy-Authorization go to the origin first asked and are dropped for good once a redirect leaves it (02 §http).
    private async Task<HostCallResult> FetchAsync(PluginV2Instance p, JsonObject? o, CancellationToken ct)
    {
        var url = o?["url"]?.GetValue<string>() ?? "";
        var method = (o?["method"]?.GetValue<string>() ?? "GET").ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD")) return HostCallResult.Fail("invalid_argument", $"method {method} is not allowed");
        var timeout = o?["timeoutMs"] is JsonValue tv && tv.TryGetValue<double>(out var t) ? TimeSpan.FromMilliseconds(Math.Clamp(t, 100, 120_000)) : TimeSpan.FromSeconds(15);
        var redirect = o?["redirect"]?.GetValue<string>() ?? "follow";
        if (redirect is not ("follow" or "manual" or "error")) return HostCallResult.Fail("invalid_argument", "redirect must be follow, manual or error");
        string? homeOrigin = null;
        var crossed = false;
        byte[]? body = null;
        if (o?["body"] is JsonValue bv && bv.TryGetValue<string>(out var b))
            body = o["base64"] is JsonValue b64 && b64.TryGetValue<bool>(out var isB64) && isB64 ? Convert.FromBase64String(b) : Encoding.UTF8.GetBytes(b);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var http = services.Http();
        for (var hop = 0; hop <= 5; hop++)
        {
            var origin = PluginPermissions.Origin(url, p.Dev);
            if (origin is null || !PluginPermissions.AllowsUrl(p.Granted, url, s => p.Setting(s), p.Dev))
            {
                await host.ActivityAsync(p, "http.fetch", origin ?? "(not https)", "denied");
                return HostCallResult.Fail("permission_denied", $"{origin ?? url} is not an origin this plugin may reach (net: permissions)");
            }
            homeOrigin ??= origin;
            crossed |= !string.Equals(origin, homeOrigin, StringComparison.OrdinalIgnoreCase);
            var uri = new Uri(url);
            if (!p.Dev || !uri.IsLoopback)
            {
                if (await PrivateAsync(uri.DnsSafeHost, timeoutCts.Token) is { } why)
                {
                    await host.ActivityAsync(p, "http.fetch", origin, "denied: " + why);
                    return HostCallResult.Fail("permission_denied", $"{uri.Host} {why}");
                }
            }
            using var req = new HttpRequestMessage(new HttpMethod(method), uri);
            if (o?["headers"] is JsonObject headers)
                foreach (var (k, v) in headers)
                {
                    var value = v is JsonValue hv && hv.TryGetValue<string>(out var s) ? s : v?.ToJsonString();
                    if (value is null || k.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (crossed && (k.Equals("cookie", StringComparison.OrdinalIgnoreCase) || k.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                                    || k.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!req.Headers.TryAddWithoutValidation(k, value) && body is not null)
                    {
                        req.Content ??= new ByteArrayContent(body);
                        req.Content.Headers.TryAddWithoutValidation(k, value);
                    }
                }
            if (body is not null && method is not ("GET" or "HEAD")) req.Content ??= new ByteArrayContent(body);
            HttpResponseMessage res;
            try
            {
                res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await host.ActivityAsync(p, "http.fetch", origin, "timeout");
                return HostCallResult.Fail("timeout", $"no reply from {origin} within {timeout.TotalSeconds:0.#} s");
            }
            catch (HttpRequestException e)
            {
                await host.ActivityAsync(p, "http.fetch", origin, "failed");
                return HostCallResult.Fail("unavailable", e.Message);
            }
            var handed = false;
            try
            {
                if (redirect != "manual" && (int)res.StatusCode is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } loc)
                {
                    if (redirect == "error")
                    {
                        await host.ActivityAsync(p, "http.fetch", origin, ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " redirect refused");
                        return HostCallResult.Fail("redirect_refused", $"{origin} answered with a redirect (redirect: 'error')");
                    }
                    url = new Uri(uri, loc).ToString();
                    if ((int)res.StatusCode is 301 or 302 or 303 && method != "HEAD")
                    {
                        method = "GET";
                        body = null;
                    }
                    continue;
                }
                var hdrs0 = new JsonObject();
                foreach (var h in res.Headers.Concat(res.Content.Headers))
                    if (!h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                        hdrs0[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                if (o?["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var wantsStream) && wantsStream)
                {
                    // the body stays open: the plugin reads it in chunks (http.read) — a streamed upstream (2.1, 02 §8)
                    if (OpenStreams(p.Id) >= StreamsPerPlugin)
                        return HostCallResult.Fail("quota_exceeded", $"a plugin may have {StreamsPerPlugin} streams open at once");
                    var id = OpenStream(p.Id, res, await res.Content.ReadAsStreamAsync(ct));
                    await host.ActivityAsync(p, "http.fetch", origin, ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " stream");
                    handed = true; // the stream owns the response now
                    return HostCallResult.Value(new JsonObject { ["status"] = (int)StatusOf(id), ["url"] = url, ["headers"] = hdrs0, ["setCookies"] = SetCookies(res), ["stream"] = id });
                }
                var bytes = await ReadCappedAsync(res, timeoutCts.Token);
                await host.ActivityAsync(p, "http.fetch", origin, ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (bytes is null) return HostCallResult.Fail("quota_exceeded", $"the reply is larger than {ResponseLimit / 1024 / 1024} MiB");
                var hdrs = new JsonObject();
                foreach (var h in res.Headers.Concat(res.Content.Headers))
                    if (!h.Key.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                        hdrs[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
                return HostCallResult.Value(new JsonObject
                {
                    ["status"] = (int)res.StatusCode,
                    ["url"] = url,
                    ["headers"] = hdrs,
                    ["setCookies"] = SetCookies(res),
                    ["body"] = Convert.ToBase64String(bytes),
                });
            }
            finally
            {
                if (!handed) res.Dispose();
            }
        }
        return HostCallResult.Fail("unavailable", "too many redirects");
    }

    private static JsonArray SetCookies(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var values) ? new JsonArray([.. values.Select(v => (JsonNode?)v)]) : [];

    // ---- provider accounts (2.1, design/plugin-v2 08 §2): rows the host keeps for a provider the plugin declared; the
    // credential is a host-held secret of that account, never on the row and never in a DTO.

    private static string AccountSecret(string provider, string key) => $"provider:{provider}:{key}";

    private async Task<HostCallResult> AccountsAsync(PluginV2Instance p, string what, JsonObject? o, CancellationToken ct)
    {
        var provider = o?["provider"]?.GetValue<string>() ?? "";
        if (p.Manifest.Providers.All(x => x.Id != provider)) return HostCallResult.Fail("not_found", $"\"{provider}\" is not a provider this plugin declares");
        var accounts = services.ProviderAccounts ?? throw new PluginCallException("not_supported", "this host has no provider accounts");
        switch (what)
        {
            case "list":
                return HostCallResult.Value(await accounts.ListAsync(p.Id, provider, ct));
            case "add":
            {
                if (o?["account"] is not JsonObject a || a["key"]?.GetValue<string>() is not { Length: > 0 and <= 128 } key)
                    return HostCallResult.Fail("invalid_argument", "an account needs a key (1–128 characters)");
                var secret = a["secret"]?.GetValue<string>();
                var row = (JsonObject)a.DeepClone();
                row.Remove("secret");
                if (secret is not null) await services.Data.SetSecretAsync(p.Id, AccountSecret(provider, key), services.Protect(secret), ct);
                await accounts.SetAsync(p.Id, provider, row, ct);
                await host.ActivityAsync(p, "providers.accounts.add", provider + "/" + key, "ok");
                return HostCallResult.Value(null);
            }
            case "update":
            {
                var key = o?["key"]?.GetValue<string>() ?? "";
                var patch = (o?["patch"] as JsonObject)?.DeepClone() as JsonObject ?? [];
                if (patch["secret"]?.GetValue<string>() is { } secret) await services.Data.SetSecretAsync(p.Id, AccountSecret(provider, key), services.Protect(secret), ct);
                patch.Remove("secret");
                patch["key"] = key;
                await accounts.SetAsync(p.Id, provider, patch, ct);
                return HostCallResult.Value(null);
            }
            case "remove":
            {
                var key = o?["key"]?.GetValue<string>() ?? "";
                await services.Data.DeleteSecretAsync(p.Id, AccountSecret(provider, key), ct);
                var removed = await accounts.RemoveAsync(p.Id, provider, key, ct);
                await host.ActivityAsync(p, "providers.accounts.remove", provider + "/" + key, removed ? "ok" : "missing");
                return HostCallResult.Value(removed);
            }
            case "secret":
            {
                var key = o?["key"]?.GetValue<string>() ?? "";
                var stored = await services.Data.GetSecretAsync(p.Id, AccountSecret(provider, key), ct);
                return HostCallResult.Value(stored is null ? null : services.Unprotect(stored));
            }
        }
        return HostCallResult.Fail("not_supported", what);
    }

    // ---- streamed replies (http.fetch with stream: true)

    /// <summary>Streams one plugin may hold open at once.</summary>
    public const int StreamsPerPlugin = 8;

    /// <summary>The most one stream may deliver.</summary>
    public const long StreamLimit = 100L * 1024 * 1024;

    /// <summary>A stream nobody read for this long is closed.</summary>
    public static TimeSpan StreamIdle { get; set; } = TimeSpan.FromSeconds(60);

    private sealed class OpenBody(string pluginId, HttpResponseMessage response, Stream body)
    {
        public string PluginId { get; } = pluginId;
        public HttpResponseMessage Response { get; } = response;
        public Stream Body { get; } = body;
        public long Read { get; set; }
        public long LastUsed { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp();
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, OpenBody> _streams = new();
    private long _nextStream;

    private int OpenStreams(string pluginId)
    {
        SweepStreams();
        return _streams.Values.Count(s => s.PluginId == pluginId);
    }

    private long OpenStream(string pluginId, HttpResponseMessage res, Stream body)
    {
        var id = Interlocked.Increment(ref _nextStream);
        _streams[id] = new OpenBody(pluginId, res, body);
        return id;
    }

    private System.Net.HttpStatusCode StatusOf(long id) => _streams.TryGetValue(id, out var s) ? s.Response.StatusCode : 0;

    private async Task<HostCallResult> ReadStreamAsync(PluginV2Instance p, JsonNode? args, CancellationToken ct)
    {
        var id = args is JsonValue v && v.TryGetValue<long>(out var l) ? l : args?["id"]?.GetValue<long>() ?? 0;
        if (!_streams.TryGetValue(id, out var s) || s.PluginId != p.Id) return HostCallResult.Fail("not_found", "no such stream (it ended, was closed or sat idle too long)");
        await s.Gate.WaitAsync(ct);
        try
        {
            s.LastUsed = System.Diagnostics.Stopwatch.GetTimestamp();
            var buffer = new byte[64 * 1024];
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(StreamIdle);
            int n;
            try
            {
                n = await s.Body.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CloseStream(p.Id, id);
                return HostCallResult.Fail("timeout", $"the stream sent nothing for {StreamIdle.TotalSeconds:0} s");
            }
            catch (Exception e) when (e is IOException or HttpRequestException)
            {
                CloseStream(p.Id, id);
                return HostCallResult.Fail("unavailable", e.Message);
            }
            if (n == 0)
            {
                CloseStream(p.Id, id);
                return HostCallResult.Value(new JsonObject { ["done"] = true });
            }
            s.Read += n;
            if (s.Read > StreamLimit)
            {
                CloseStream(p.Id, id);
                return HostCallResult.Fail("quota_exceeded", $"a stream may carry {StreamLimit / 1024 / 1024} MiB at most");
            }
            return HostCallResult.Value(new JsonObject { ["chunk"] = Convert.ToBase64String(buffer, 0, n) });
        }
        finally
        {
            s.Gate.Release();
        }
    }

    private void CloseStream(string pluginId, long id)
    {
        if (!_streams.TryGetValue(id, out var s) || s.PluginId != pluginId || !_streams.TryRemove(id, out _)) return;
        s.Body.Dispose();
        s.Response.Dispose();
    }

    /// <summary>Closes every stream a plugin holds (it stopped).</summary>
    public void CloseStreams(string pluginId)
    {
        foreach (var (id, s) in _streams.ToArray())
            if (s.PluginId == pluginId) CloseStream(pluginId, id);
    }

    private void SweepStreams()
    {
        foreach (var (id, s) in _streams.ToArray())
            if (System.Diagnostics.Stopwatch.GetElapsedTime(s.LastUsed) > StreamIdle && s.Gate.CurrentCount == 1) CloseStream(s.PluginId, id);
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > ResponseLimit) return null;
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > ResponseLimit) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Why <paramref name="host"/> is not one a plugin may reach (it resolves to this machine or a private network); null when it may.</summary>
    internal static async Task<string?> PrivateAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct);
            }
            catch (SocketException)
            {
                return null; // the request itself will fail to resolve
            }
        }
        foreach (var a in addresses)
            if (IsPrivate(a)) return "resolves to a private or local address";
        return null;
    }

    internal static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal || a.IsIPv6Multicast;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
               || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0 || b[0] >= 224;
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Key(JsonNode? n)
    {
        var key = Str(n) ?? throw new InvalidOperationException("a key must be a string");
        if (key.Length is 0 or > 512) throw new InvalidOperationException("a key must be 1–512 characters");
        return key;
    }

    private static byte[] Bytes(JsonNode? args)
    {
        var o = args as JsonObject;
        if (o?["base64"] is JsonValue b && b.TryGetValue<string>(out var s)) return Convert.FromBase64String(s);
        return Encoding.UTF8.GetBytes(o?["text"]?.GetValue<string>() ?? "");
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
