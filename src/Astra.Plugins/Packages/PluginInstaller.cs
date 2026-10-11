using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Plugins;
using Astra.Plugins.Runtime;
using Astra.Plugins.Store;

namespace Astra.Plugins.Packages;

/// <summary>
/// Adds, updates and removes plugins, as magpie's internal/plugin/store.go does: npm and git packages
/// go into <see cref="PluginCatalog.PackagesDir"/> with <c>bun add --ignore-scripts</c> (their install
/// scripts never run, as in OpenCode), a path is used where it is. A package that is only gateway
/// middleware or an agent comes straight from npm's tarball instead, so those plugins never need Bun.
/// </summary>
public sealed partial class PluginInstaller(
    PluginCatalog catalog, BunRuntime bun, NpmTarballFetcher npm, Func<IEnumerable<string>?>? toolPath = null)
{
    /// <summary>The package pi's extensions import pi from; the host loads them with it.</summary>
    public const string PiAgent = "@earendil-works/pi-coding-agent";

    private readonly SemaphoreSlim _lock = new(1, 1);

    private string PackagesDir => catalog.PackagesDir;

    /// <summary>
    /// Installs <paramref name="spec"/> and adds it to the list — in place of a plugin of the same package,
    /// whose options and provider picks it keeps. Returns the entry saved (an npm spec without a version
    /// is saved as name@latest).
    /// </summary>
    public async Task<PluginEntry> AddAsync(string spec, string? source, Action<string> log, CancellationToken ct = default)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new ArgumentException("no plugin given");
        await _lock.WaitAsync(ct);
        try
        {
            var before = await catalog.SnapshotAsync(ct);
            var namesBefore = before.Plugins.ToDictionary(p => p.Entry.Spec, p => PluginSpec.Name(p.Entry.Spec, PackagesDir));
            var installedNow = false;
            if (PluginSpec.IsPath(spec))
            {
                spec = PluginSpec.ExpandPath(spec);
                if (!File.Exists(spec) && !Directory.Exists(spec)) throw new FileNotFoundException($"{spec} doesn't exist");
                source ??= PluginSources.Path;
            }
            else if (PluginSpec.IsGit(spec))
            {
                await BunAddAsync(spec, log, ct);
                if (PluginSpec.Name(spec, PackagesDir) == spec)
                    throw new InvalidOperationException($"bun installed {spec} but the plugins' package.json doesn't list it");
                installedNow = true;
                source ??= spec.StartsWith("github:", StringComparison.Ordinal) || spec.Contains("github.com/") ? PluginSources.GitHub : PluginSources.Git;
            }
            else
            {
                var name = PluginSpec.Name(spec, PackagesDir);
                if (!PluginSpec.IsPackageName(name)) throw new ArgumentException($"\"{spec}\" isn't an npm package name");
                if (name == spec) spec += "@latest";
                await InstallNpmAsync(name, spec[(name.Length + 1)..], log, ct);
                installedNow = true;
                source ??= PluginSources.Npm;
            }

            var target = PluginSpec.Target(spec, PackagesDir);
            var manifest = PackageManifest.Read(target);
            if (manifest.NotPluginReason is { } why)
            {
                // installed just now for this, and nothing loads it: taken out again
                if (installedNow && !namesBefore.ContainsValue(PluginSpec.Name(spec, PackagesDir)))
                    await RemovePackageAsync(PluginSpec.Name(spec, PackagesDir), log, ct);
                throw new InvalidOperationException(why);
            }
            if (manifest.IsPi) await EnsurePiAsync(target, log, ct);

            var entry = new PluginEntry
            {
                Spec = spec,
                Name = PluginSpec.PackageName(spec, PackagesDir),
                Source = source,
                InstalledVersion = manifest.Version,
            };
            var same = before.Plugins.FirstOrDefault(p => p.Entry.Spec == spec || namesBefore[p.Entry.Spec] == PluginSpec.Name(spec, PackagesDir));
            if (same is not null)
            {
                entry.OptionsJson = same.Entry.OptionsJson;
                entry.Enabled = same.Entry.Enabled;
                await catalog.Store.ReplaceAsync(same.Entry.Spec, entry, ct);
            }
            else
            {
                await catalog.Store.AddAsync(entry, ct);
            }
            log($"added {spec}" + (manifest.Version is { } v ? $" ({v})" : ""));
            return entry;
        }
        finally
        {
            catalog.Invalidate();
            _lock.Release();
        }
    }

    /// <summary>
    /// Installs npm's newest version of the plugin <paramref name="name"/> (every npm plugin when null) and
    /// fetches each git one again. One pinned to a version npm has a newer one of is moved to the newest
    /// and unpinned, as magpie's Update does; a path is left as it is.
    /// </summary>
    public async Task UpdateAsync(string? name, Action<string> log, CancellationToken ct = default)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var plugins = name is null
            ? snapshot.Plugins
            : [PluginCatalog.Find(snapshot, name, packagesDir: PackagesDir) ?? throw new KeyNotFoundException($"no plugin \"{name}\"")];
        var errors = new List<string>();
        foreach (var p in plugins)
        {
            var spec = p.Entry.Spec;
            if (PluginSpec.IsPath(spec)) continue;
            try
            {
                if (PluginSpec.IsGit(spec))
                {
                    await BunAsync(["update", "--ignore-scripts", PluginSpec.Name(spec, PackagesDir)], log, ct);
                }
                else
                {
                    var pkg = PluginSpec.Name(spec, PackagesDir);
                    var newest = await npm.ResolveAsync(pkg, "latest", ct);
                    if (PluginSpec.Pinned(spec) && IsNewer(newest.Version, p.Manifest.Version))
                        await AddAsync(pkg, p.Entry.Source, log, ct);
                    else if (!PluginSpec.Pinned(spec))
                        await InstallNpmAsync(pkg, newest.Version, log, ct);
                    else
                        log($"{spec} is pinned and npm has nothing newer");
                }
                if (PackageManifest.Read(p.Target).IsPi) await EnsurePiAsync(p.Target, log, ct);
                await catalog.Store.SetInstalledVersionAsync(spec, PluginSpec.Version(PluginSpec.Target(spec, PackagesDir)), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                errors.Add($"{spec}: {e.Message}");
                log($"{spec}: {e.Message}");
            }
        }
        catalog.Invalidate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }

    /// <summary>Takes the plugin <paramref name="name"/> off the list, and its package out of the packages folder.</summary>
    public async Task RemoveAsync(string name, Action<string> log, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var snapshot = await catalog.SnapshotAsync(ct);
            var p = PluginCatalog.Find(snapshot, name, packagesDir: PackagesDir) ?? throw new KeyNotFoundException($"no plugin \"{name}\"");
            await catalog.Store.RemoveAsync(p.Entry.Spec, ct);
            if (!PluginSpec.IsPath(p.Entry.Spec))
            {
                var pkg = PluginSpec.Name(p.Entry.Spec, PackagesDir);
                var stillUsed = snapshot.Plugins.Any(o => o != p && !PluginSpec.IsPath(o.Entry.Spec) && PluginSpec.Name(o.Entry.Spec, PackagesDir) == pkg);
                if (!stillUsed) await RemovePackageAsync(pkg, log, ct);
            }
            log($"removed {p.Entry.Spec}");
        }
        finally
        {
            catalog.Invalidate();
            _lock.Release();
        }
    }

    // npm's package at version (or tag): from its tarball when it runs only in Astra's process, else bun add.
    private async Task InstallNpmAsync(string name, string versionOrTag, Action<string> log, CancellationToken ct)
    {
        var resolved = await npm.ResolveAsync(name, versionOrTag, ct);
        var dest = Path.Combine(PackagesDir, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
        if (bun.Current is null)
        {
            var staging = Path.Combine(catalog.Paths.PluginsDir, ".staging", Guid.NewGuid().ToString("N"));
            try
            {
                log($"fetching {name}@{resolved.Version} from npm");
                await npm.InstallAsync(resolved, staging, ct);
                var manifest = PackageManifest.Read(staging);
                if (manifest.InProcessOnly)
                {
                    EnsurePackagesFolder();
                    if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    Directory.Move(staging, dest);
                    RecordDependency(name, resolved.Version);
                    return;
                }
                log($"{name} runs in the plugin host, which needs Bun");
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }
        // the exact version, not a tag: bun resolves "latest" by its own config (a minimumReleaseAge, a
        // registry of the user's own) and may quietly install an older one (magpie's installAt)
        await BunAddAsync($"{name}@{resolved.Version}", log, ct);
        var got = PluginSpec.Version(dest);
        if (got is null || IsNewer(resolved.Version, got))
            throw new InvalidOperationException($"bun add {name}@{resolved.Version} installed {got ?? "nothing"}");
    }

    private async Task EnsurePiAsync(string target, Action<string> log, CancellationToken ct)
    {
        if (File.Exists(Path.Combine(PackagesDir, "node_modules", PiAgent, "package.json"))
            || File.Exists(Path.Combine(target, "node_modules", PiAgent, "package.json")))
            return;
        await BunAddAsync(PiAgent, log, ct);
    }

    private async Task RemovePackageAsync(string pkg, Action<string> log, CancellationToken ct)
    {
        if (bun.Current is not null && DependsOn(pkg))
        {
            await BunAsync(["remove", "--ignore-scripts", pkg], log, ct);
            return;
        }
        var dir = Path.Combine(PackagesDir, "node_modules", pkg.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        RecordDependency(pkg, null);
    }

    private Task BunAddAsync(string spec, Action<string> log, CancellationToken ct) =>
        BunAsync(["add", "--ignore-scripts", spec], log, ct);

    // Runs bun in the packages folder; a failure carries bun's last lines, and what bun says about a
    // minimumReleaseAge holding a version back is said plainly.
    private async Task BunAsync(string[] args, Action<string> log, CancellationToken ct)
    {
        var exe = await bun.EnsureAsync(ct);
        EnsurePackagesFolder();
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = PackagesDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment.Clear();
        foreach (var (k, v) in ChildEnvironment.Build(forHost: false, toolPath?.Invoke())) info.Environment[k] = v;
        info.Environment["BUN_INSTALL_CACHE_DIR"] = Path.Combine(catalog.Paths.PluginsDir, "bun-cache");

        log("$ bun " + string.Join(' ', args));
        var lines = new List<string>();
        using var process = new Process { StartInfo = info };
        void Line(string? s)
        {
            if (s is null) return;
            lock (lines) lines.Add(s);
            log(s);
        }
        process.OutputDataReceived += (_, e) => Line(e.Data);
        process.ErrorDataReceived += (_, e) => Line(e.Data);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"bun {args[0]} took too long");
        }
        if (process.ExitCode == 0) return;

        string text;
        lock (lines) text = string.Join('\n', lines.TakeLast(6));
        if (MinimumReleaseAge().Match(text) is { Success: true } m)
            throw new InvalidOperationException(
                $"{args[^1]} isn't installed yet: Bun's minimumReleaseAge (in a .bunfig.toml) holds back versions published less than {AgeText(m.Groups[1].Value)} ago ({text})");
        throw new InvalidOperationException($"bun {string.Join(' ', args)} exited {process.ExitCode}: {text}");
    }

    private void EnsurePackagesFolder()
    {
        Directory.CreateDirectory(PackagesDir);
        var pj = Path.Combine(PackagesDir, "package.json");
        if (!File.Exists(pj)) File.WriteAllText(pj, "{\n  \"name\": \"magpie-plugins\",\n  \"private\": true\n}\n");
    }

    // A package fetched from its tarball is listed in the packages folder's package.json as bun would list
    // it, so a later bun add or remove keeps it rather than pruning it as extraneous.
    private void RecordDependency(string pkg, string? version)
    {
        var pj = Path.Combine(PackagesDir, "package.json");
        if (!File.Exists(pj)) return;
        var doc = JsonNode.Parse(File.ReadAllText(pj)) as JsonObject ?? new JsonObject();
        var deps = doc["dependencies"] as JsonObject;
        if (version is null)
        {
            deps?.Remove(pkg);
        }
        else
        {
            if (deps is null) doc["dependencies"] = deps = new JsonObject();
            deps[pkg] = version;
        }
        using var file = File.Create(pj);
        using (var writer = new System.Text.Json.Utf8JsonWriter(file, new System.Text.Json.JsonWriterOptions { Indented = true }))
            doc.WriteTo(writer);
        file.Write("\n"u8);
    }

    private bool DependsOn(string pkg)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(PackagesDir, "package.json")))?["dependencies"] is JsonObject deps && deps.ContainsKey(pkg);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Whether semver <paramref name="a"/> is newer than <paramref name="b"/> (a prerelease is older than its release).</summary>
    public static bool IsNewer(string? a, string? b)
    {
        if (a is null) return false;
        if (b is null) return true;
        static (int[] Nums, string Pre) Parse(string v)
        {
            v = v.TrimStart('v');
            var dash = v.IndexOf('-');
            var pre = dash >= 0 ? v[(dash + 1)..] : "";
            var core = (dash >= 0 ? v[..dash] : v).Split('+')[0].Split('.');
            return (core.Select(p => int.TryParse(p, out var n) ? n : 0).Concat([0, 0, 0]).Take(3).ToArray(), pre);
        }
        var (na, pa) = Parse(a);
        var (nb, pb) = Parse(b);
        for (var i = 0; i < 3; i++)
            if (na[i] != nb[i]) return na[i] > nb[i];
        if (pa.Length == 0 || pb.Length == 0) return pa.Length == 0 && pb.Length > 0;
        return string.CompareOrdinal(pa, pb) > 0;
    }

    private static string AgeText(string secs)
    {
        if (!int.TryParse(secs, out var n)) return secs + " seconds";
        static string Plural(int x, string unit) => x == 1 ? "1 " + unit : $"{x} {unit}s";
        if (n >= 86400 && n % 86400 == 0) return Plural(n / 86400, "day");
        if (n >= 3600 && n % 3600 == 0) return Plural(n / 3600, "hour");
        if (n >= 60 && n % 60 == 0) return Plural(n / 60, "minute");
        return Plural(n, "second");
    }

    [GeneratedRegex(@"minimum-release-age: (\d+) seconds")]
    private static partial Regex MinimumReleaseAge();
}
