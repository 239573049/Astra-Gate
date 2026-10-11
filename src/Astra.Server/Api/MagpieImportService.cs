using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins;
using Astra.Plugins.Packages;
using Astra.Plugins.Store;

namespace Astra.Server.Api;

/// <summary>One plugin magpie has, as the import would bring it over.</summary>
/// <param name="Installed">Astra has it already (by spec or package): left as it is.</param>
public sealed record MagpiePluginDto(string Spec, bool Off, bool HasOptions, bool Installed);

/// <summary>What importing from magpie would bring: its plugins.json, and how many sign-ins its plugin-auth.json holds.</summary>
public sealed record MagpieImportPreviewDto(bool Found, string Path, List<MagpiePluginDto> Plugins, int SignIns, string? Error);

/// <summary>POST /api/plugins/import-magpie: <c>signIns</c> also copies magpie's plugin sign-ins (plaintext, as both keep them).</summary>
public sealed record MagpieImportInput(bool? SignIns);

/// <summary>
/// Brings a magpie install's plugins over (its <c>${XDG_CONFIG_HOME:-~/.config}/magpie</c>, as <c>MagpieReader</c> finds
/// it): each plugin in plugins.json is installed again in Astra with its on/off state and options, the provider
/// picks follow, and — only when asked — the sign-ins in its plugin-auth.json are copied beside Astra's (an account
/// Astra has under the same key is kept). Nothing in magpie's folder is changed.
/// </summary>
public sealed class MagpieImportService(ClientEnvironment env, AstraPaths paths, PluginCatalog catalog, PluginInstaller installer, PluginJobs jobs)
{
    private string Dir => System.IO.Path.Combine(
        env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : env.Combine(".config"), "magpie");

    public async Task<MagpieImportPreviewDto> PreviewAsync(CancellationToken ct)
    {
        var file = System.IO.Path.Combine(Dir, "plugins.json");
        var (list, error) = Read(file);
        if (list is null) return new MagpieImportPreviewDto(File.Exists(file), file, [], 0, error);
        var snapshot = await catalog.SnapshotAsync(ct);
        var plugins = Entries(list).Select(e => new MagpiePluginDto(e.Spec, e.Off, e.Options is { Count: > 0 },
            snapshot.Plugins.Any(p => p.Entry.Spec == e.Spec || p.Entry.Name == PluginSpec.Name(e.Spec, catalog.PackagesDir)))).ToList();
        return new MagpieImportPreviewDto(true, file, plugins, SignIns()?.Count ?? 0, null);
    }

    public PluginJob Import(bool signIns)
    {
        if (catalog.Off) throw new AdminApiException(409, "Plugins are switched off on this server (ASTRA_PLUGINS=off)");
        return ImportJob(signIns);
    }

    private PluginJob ImportJob(bool signIns) => jobs.Start("import", "magpie", async (log, ct) =>
    {
        var file = System.IO.Path.Combine(Dir, "plugins.json");
        var (list, error) = Read(file);
        if (list is null) throw new InvalidOperationException(error ?? $"magpie has no {file}");
        var snapshot = await catalog.SnapshotAsync(ct);
        var failed = 0;
        foreach (var e in Entries(list))
        {
            if (snapshot.Plugins.Any(p => p.Entry.Spec == e.Spec || p.Entry.Name == PluginSpec.Name(e.Spec, catalog.PackagesDir)))
            {
                log($"{e.Spec}: Astra has it already");
                continue;
            }
            try
            {
                var entry = await installer.AddAsync(e.Spec, PluginSources.MagpieImport, log, ct);
                if (e.Options is { Count: > 0 }) await catalog.Store.SetOptionsAsync(entry.Spec, e.Options.ToJsonString(), ct);
                if (e.Off) await catalog.Store.SetEnabledAsync(entry.Spec, false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                log($"{e.Spec}: {ex.Message}");
            }
        }
        if (list["prefer"] is JsonObject prefer)
        {
            var now = await catalog.Store.ListAsync(ct);
            foreach (var (provider, spec) in prefer)
                if (spec?.ToString() is { } s && now.FirstOrDefault(p => p.Spec == s || p.Name == PluginSpec.Name(s, catalog.PackagesDir)) is { } match)
                    await catalog.Store.SetPreferenceAsync(provider, match.Spec, ct);
        }
        if (signIns) log($"copied {await CopySignInsAsync(ct)} sign-ins");
        catalog.Invalidate();
        return failed == 0 ? "imported" : $"imported, {failed} failed";
    });

    private static (JsonObject? List, string? Error) Read(string file)
    {
        if (!File.Exists(file)) return (null, null);
        try
        {
            return (JsonNode.Parse(File.ReadAllText(file)) as JsonObject, null);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return (null, $"{file} can't be read: {e.Message}");
        }
    }

    private static IEnumerable<(string Spec, bool Off, JsonObject? Options)> Entries(JsonObject list) =>
        (list["plugins"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(p => (Spec: p["spec"]?.ToString() ?? "", Off: p["off"] is JsonValue v && v.GetValueKind() == JsonValueKind.True, Options: p["options"] as JsonObject))
        .Where(p => p.Spec.Length > 0);

    private JsonObject? SignIns()
    {
        var file = System.IO.Path.Combine(Dir, "plugin-auth.json");
        try
        {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    // Merges magpie's sign-ins into Astra's plugin-auth.json under the lock host.js takes (an O_EXCL lock file, stale
    // after 10 s); a key Astra has already is kept. The file stays owner-only.
    private async Task<int> CopySignInsAsync(CancellationToken ct)
    {
        if (SignIns() is not { } theirs) return 0;
        var file = paths.PluginAuthFile;
        Directory.CreateDirectory(paths.PluginsDir);
        var lockFile = file + ".lock";
        for (var tries = 0; ; tries++)
        {
            try
            {
                await using var _ = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                var ours = File.Exists(file) ? JsonNode.Parse(await File.ReadAllTextAsync(file, ct)) as JsonObject ?? new JsonObject() : new JsonObject();
                var copied = 0;
                foreach (var (key, value) in theirs)
                {
                    if (ours.ContainsKey(key) || value is null) continue;
                    ours[key] = value.DeepClone();
                    copied++;
                }
                if (copied > 0)
                {
                    var part = file + ".part";
                    await File.WriteAllTextAsync(part, ours.ToJsonString(), ct);
                    AstraPaths.RestrictToOwner(part);
                    File.Move(part, file, overwrite: true);
                }
                return copied;
            }
            catch (IOException) when (File.Exists(lockFile) && tries < 50)
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile) > TimeSpan.FromSeconds(10)) File.Delete(lockFile);
                else await Task.Delay(200, ct);
            }
        }
    }
}
