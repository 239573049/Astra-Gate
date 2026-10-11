using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Packages;

/// <summary>
/// What a plugin at a target (an installed package's folder, or a path) has, read the way magpie
/// reads it (internal/plugin/middleware.go, agent.go, store.go): gateway middleware, an agent, a pi
/// extension, and an OpenCode plugin for the host to load.
/// </summary>
public sealed class PackageManifest
{
    private const string PiAgent = "@earendil-works/pi-coding-agent";
    private const string PiAgentOld = "@mariozechner/pi-coding-agent";

    /// <summary>The files a package that names none loads, as host.js looks for them (INDEX_FILES).</summary>
    private static readonly string[] IndexFiles = ["index.ts", "index.tsx", "index.js", "index.mjs", "index.cjs"];

    private PackageManifest(string target) => Target = target;

    /// <summary>The folder (or file) this was read from.</summary>
    public string Target { get; }

    /// <summary>The package's name, or the file or folder name when it has no package.json.</summary>
    public string Name { get; private set; } = "";

    public string? Version { get; private set; }

    /// <summary>The middleware module (package.json magpie.middleware, or the target itself when it is a
    /// *.middleware.js / .mjs file); null when it has none.</summary>
    public string? MiddlewareFile { get; private set; }

    /// <summary>The agent module (package.json magpie.agent, or a *.agent.js / .mjs file); null when it has none.</summary>
    public string? AgentFile { get; private set; }

    /// <summary>Whether it is a pi package or extension, which the host loads through pi (pi.js).</summary>
    public bool IsPi { get; private set; }

    /// <summary>Whether it names a module for the host to import (main, exports, or an index file).</summary>
    public bool HasEntry { get; private set; }

    /// <summary>package.json magpie.options: what its options editor starts from before any are set.</summary>
    public JsonObject? OptionsExample { get; private set; }

    /// <summary>package.json magpie.icon: an icon name, or an https: / data: URL.</summary>
    public string? Icon { get; private set; }

    /// <summary>
    /// Whether everything it has runs in Astra's own process (middleware, agent) so the plugin host has
    /// nothing of it to load: magpie's InMagpieOnly — middleware or an agent, and no main or exports
    /// (an index file beside them doesn't count, as in magpie).
    /// </summary>
    public bool InProcessOnly => (MiddlewareFile is not null || AgentFile is not null) && !_mainOrExports;

    private bool _mainOrExports;

    /// <summary>The root a module's imports must stay inside: the package folder, or a single file's folder.</summary>
    public string ModuleRoot => Directory.Exists(Target) ? Target : Path.GetDirectoryName(Target)!;

    /// <summary>
    /// Why nothing could load it, or null: a package that names no file to import (no main, exports or
    /// index file) and is neither pi's, middleware nor an agent — a command alone (bin), like an MCP
    /// server, installs fine and then never loads.
    /// </summary>
    public string? NotPluginReason { get; private set; }

    /// <summary>Reads the plugin at <paramref name="target"/>; a missing or unreadable one reads as empty.</summary>
    public static PackageManifest Read(string target)
    {
        var m = new PackageManifest(target);
        if (File.Exists(target))
        {
            m.ReadFile(target);
            return m;
        }
        if (Directory.Exists(target)) m.ReadFolder(target);
        return m;
    }

    private void ReadFile(string file)
    {
        Name = Path.GetFileName(file);
        var lower = Name.ToLowerInvariant();
        if (lower.EndsWith(".middleware.js", StringComparison.Ordinal) || lower.EndsWith(".middleware.mjs", StringComparison.Ordinal))
            MiddlewareFile = file;
        else if (lower.EndsWith(".agent.js", StringComparison.Ordinal) || lower.EndsWith(".agent.mjs", StringComparison.Ordinal))
            AgentFile = file;
        else
            HasEntry = _mainOrExports = true; // any other file is an OpenCode plugin module for the host
        try
        {
            var text = File.ReadAllText(file);
            IsPi = text.Contains('"' + PiAgent + '"') || text.Contains('\'' + PiAgent + '\'')
                   || text.Contains('"' + PiAgentOld + '"') || text.Contains('\'' + PiAgentOld + '\'');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ReadFolder(string dir)
    {
        Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var pkg = ReadPackageJson(dir);
        if (pkg is null)
        {
            // pi's own layout: a folder of extensions with no package.json and no index file
            HasEntry = IndexFiles.Any(f => File.Exists(Path.Combine(dir, f)));
            IsPi = !HasEntry && Directory.Exists(Path.Combine(dir, "extensions"));
            if (!HasEntry && !IsPi) NotPluginReason = NoEntryReason(Name, hasBin: false);
            return;
        }

        if (Str(pkg, "name") is { } name) Name = name;
        Version = Str(pkg, "version");
        var magpie = pkg["magpie"] as JsonObject;
        if (magpie is not null)
        {
            if (Str(magpie, "middleware") is { } mw) MiddlewareFile = Resolve(dir, mw);
            if (Str(magpie, "agent") is { } agent) AgentFile = Resolve(dir, agent);
            OptionsExample = magpie["options"] as JsonObject;
            Icon = Str(magpie, "icon");
        }

        var exports = pkg["exports"];
        _mainOrExports = Str(pkg, "main") is not null || (exports is not null && exports.GetValueKind() != JsonValueKind.Null);
        HasEntry = _mainOrExports || IndexFiles.Any(f => File.Exists(Path.Combine(dir, f)));
        IsPi = pkg["pi"] is JsonObject
               || (pkg["keywords"] as JsonArray)?.Any(k => k is JsonValue v && v.TryGetValue<string>(out var s) && s == "pi-package") == true
               || DependsOn(pkg["dependencies"]) || DependsOn(pkg["peerDependencies"]);

        if (!HasEntry && !IsPi && MiddlewareFile is null && AgentFile is null)
        {
            var bin = pkg["bin"];
            NotPluginReason = NoEntryReason(Name, bin is not null && bin.GetValueKind() != JsonValueKind.Null);
        }
    }

    // A module path as package.json names it ("./x.middleware.js"), under dir.
    private static string Resolve(string dir, string rel) =>
        Path.GetFullPath(Path.Combine(dir, rel.Trim().Replace('/', Path.DirectorySeparatorChar)));

    private static bool DependsOn(JsonNode? deps) =>
        deps is JsonObject o && (o.ContainsKey(PiAgent) || o.ContainsKey(PiAgentOld));

    private static string NoEntryReason(string name, bool hasBin)
    {
        var why = $"{name} isn't an OpenCode or magpie plugin: its package names no file to load (no main or exports in package.json, no index file)";
        return hasBin ? why + ", only a command (bin). If it is an MCP server, add it to your client's MCP servers instead" : why;
    }

    private static JsonObject? ReadPackageJson(string dir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "package.json"))) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
