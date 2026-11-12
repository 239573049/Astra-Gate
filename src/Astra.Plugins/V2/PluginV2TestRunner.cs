using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2;

/// <summary>One test's outcome under <c>astra plugin test</c>.</summary>
public sealed record PluginTestResult(string File, string Name, string Status, double? Ms, string? Error);

/// <summary>
/// <c>astra plugin test</c> (design/plugin-v2 06 §4): runs a plugin folder's tests (<c>test/**/*.test.js|mjs</c>) in the real
/// sandbox — Jaunt, the real <c>astra</c> module, the real host calls with their permission checks — over a throwaway
/// in-memory store with every permission of the manifest granted and no network (an <c>http.fetch</c> answers 404). Each file
/// is the entry module of its own engine; the tests import <c>vitest</c> and <c>@astragate/plugin-sdk/testing</c>, which the
/// sandbox provides, so the same files run under real vitest in Node with the SDK.
/// </summary>
public static class PluginV2TestRunner
{
    /// <summary>How long one file's tests may take altogether.</summary>
    public static TimeSpan FileLimit { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The test files of a plugin folder (sorted; .js / .mjs under test/, no node_modules).</summary>
    public static List<string> TestFiles(string root)
    {
        var dir = Path.Combine(root, "test");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".test.js", StringComparison.Ordinal) || f.EndsWith(".test.mjs", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Runs every test file of the plugin in <paramref name="root"/>; a file that doesn't load is one failed result.</summary>
    public static async Task<List<PluginTestResult>> RunAsync(string root, string hostVersion, CancellationToken ct = default)
    {
        var manifest = PluginV2Manifest.Read(root);
        var services = new IsolatedServices(hostVersion);
        await services.Data.SetGrantsAsync(manifest.Id, [.. manifest.Permissions, .. manifest.OptionalPermissions], ct);
        var home = Directory.CreateTempSubdirectory("astra-plugin-test-").FullName;
        var catalog = new PluginCatalog(new SingleList(new PluginEntry { Spec = root, Name = manifest.Id, Enabled = true, Source = PluginSources.MarketPackage }),
            new AstraPaths(home));
        var results = new List<PluginTestResult>();
        await using var host = new PluginV2Host(catalog, services);
        try
        {
            await host.RefreshAsync(ct);
            var instance = await host.FindAsync(manifest.Id, ct)
                ?? throw new PluginManifestException((await host.ProblemsAsync(ct)).Values.FirstOrDefault() ?? $"{manifest.Id} didn't load");
            foreach (var file in TestFiles(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                await using var main = new PluginMain(instance, (m, n, a) => host.Calls.Sync(m.Plugin, n, a), (m, n, a, c) => host.Calls.AsyncCall(m.Plugin, n, a, c),
                    (level, line) => services.Log(manifest.Id, level, line), entry: file, context: "test", modules: V2Assets.TestModules);
                try
                {
                    var r = await main.RunAsync(new JsonObject { ["op"] = "test.run" }, wall: FileLimit, slice: FileLimit, ct: ct);
                    foreach (var t in (r?["tests"] as JsonArray ?? []).OfType<JsonObject>())
                        results.Add(new PluginTestResult(rel, t["name"]?.GetValue<string>() ?? "", t["status"]?.GetValue<string>() ?? "failed",
                            t["ms"] is JsonValue ms && ms.TryGetValue<double>(out var d) ? d : null, t["error"]?.GetValue<string>()));
                }
                catch (PluginCallException e)
                {
                    results.Add(new PluginTestResult(rel, "(load)", "failed", null, e.Message));
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return results;
    }

    // The plugin list of one folder.
    private sealed class SingleList(PluginEntry entry) : IPluginListStore
    {
        public Task<IReadOnlyList<PluginEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PluginEntry>>([entry]);
        public Task<PluginEntry?> GetAsync(string spec, CancellationToken ct = default) => Task.FromResult<PluginEntry?>(spec == entry.Spec ? entry : null);
        public Task AddAsync(PluginEntry e, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReplaceAsync(string oldSpec, PluginEntry e, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetOptionsAsync(string spec, string? optionsJson, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SetInstalledVersionAsync(string spec, string? version, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RemoveAsync(string spec, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<string, string>> ListPreferencesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task SetPreferenceAsync(string providerId, string spec, CancellationToken ct = default) => Task.CompletedTask;
    }

    // In memory, no network, no notifications: nothing a test does leaves the run. The log goes to the results' console.
    private sealed class IsolatedServices(string hostVersion) : IPluginV2Services
    {
        public string HostVersion => hostVersion;
        public MemoryPluginDataStore Data { get; } = new();
        IPluginDataStore IPluginV2Services.Data => Data;
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
        public HttpClient Http() => new(new NoNetwork());
        public void Notify(string pluginId, string title, string? body, string tone) { }
        public ValueTask<JsonNode?> QueryAsync(string what, JsonNode? args, CancellationToken ct) => ValueTask.FromResult<JsonNode?>(new JsonArray());
        public void Log(string pluginId, string level, string message) { }

        public IPluginProviderAccounts? ProviderAccounts { get; } = new MemoryAccounts();

        // provider accounts that live for the run (providers.accounts.* in a test)
        private sealed class MemoryAccounts : IPluginProviderAccounts
        {
            private readonly Dictionary<string, JsonObject> _rows = [];

            public Task<JsonArray> ListAsync(string pluginId, string providerId, CancellationToken ct)
            {
                lock (_rows)
                    return Task.FromResult(new JsonArray([.. _rows.Where(r => r.Key.StartsWith(providerId + "/", StringComparison.Ordinal)).Select(r => (JsonNode?)r.Value.DeepClone())]));
            }

            public Task SetAsync(string pluginId, string providerId, JsonObject account, CancellationToken ct)
            {
                var id = providerId + "/" + account["key"]!.GetValue<string>();
                lock (_rows)
                {
                    var row = _rows.TryGetValue(id, out var known) ? known : new JsonObject { ["status"] = "active", ["enabled"] = true };
                    foreach (var (k, v) in account) row[k] = v?.DeepClone();
                    _rows[id] = row;
                }
                return Task.CompletedTask;
            }

            public Task<bool> RemoveAsync(string pluginId, string providerId, string key, CancellationToken ct)
            {
                lock (_rows) return Task.FromResult(_rows.Remove(providerId + "/" + key));
            }
        }

        private sealed class NoNetwork : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
