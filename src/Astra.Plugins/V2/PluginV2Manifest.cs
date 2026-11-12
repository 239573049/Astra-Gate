using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Plugins.V2;

/// <summary>The versions of the v2 contracts this host speaks (design/plugin-v2 02 §13, 06 §6).</summary>
public static class PluginV2Versions
{
    /// <summary>The capability API (<c>engines.api</c>, <c>astra.api</c>).</summary>
    public static readonly SemVer Api = new(2, 0, 0, null);

    /// <summary>The UI protocol (<c>engines.ui</c>, the <c>ui</c> a shell reports).</summary>
    public static readonly SemVer Ui = new(1, 0, 0, null);

    /// <summary>The manifest file a v2 plugin has at its root; its presence (with manifestVersion 2) is what makes it v2.</summary>
    public const string ManifestFile = "astra.plugin.json";
}

/// <summary>
/// A v2 plugin's <c>astra.plugin.json</c> (design/plugin-v2 01 §2), read and checked. What a plugin provides is all
/// here, so the host lists, validates and shows it without running any of its code; <see cref="Contributions"/>
/// holds each extension point's part, checked against that point's schema (<see cref="ExtensionPoints"/>).
/// </summary>
public sealed partial class PluginV2Manifest
{
    private PluginV2Manifest(string root, JsonObject raw)
    {
        Root = root;
        Raw = raw;
    }

    /// <summary>The plugin's folder: imports and assets stay inside it.</summary>
    public string Root { get; }

    /// <summary>The manifest as written.</summary>
    public JsonObject Raw { get; }

    /// <summary><c>publisher.name</c> form, e.g. <c>acme.word-guard</c>: what its data, grants and routes are keyed by.</summary>
    public string Id { get; private set; } = "";

    public string Version { get; private set; } = "";

    /// <summary>A string or a <c>{ "zh": …, "en": … }</c> object.</summary>
    public JsonNode? Name { get; private set; }

    public JsonNode? Description { get; private set; }

    /// <summary>A file inside the plugin (served as an asset), a Lucide icon name, or a <c>data:image</c> URL.</summary>
    public string? Icon { get; private set; }

    public string? PublisherName { get; private set; }
    public string? PublisherUrl { get; private set; }
    public string? License { get; private set; }
    public string? Homepage { get; private set; }

    /// <summary><c>engines.astra</c>: the Astra versions it runs on.</summary>
    public string? EnginesAstra { get; private set; }

    /// <summary><c>engines.api</c>: the capability API range.</summary>
    public string? EnginesApi { get; private set; }

    /// <summary><c>engines.ui</c>: the UI protocol range its views need.</summary>
    public string? EnginesUi { get; private set; }

    /// <summary>The main context's module (UI, commands, jobs, events), absolute; null when it has none.</summary>
    public string? Main { get; private set; }

    /// <summary>The gateway context's module (hooks), absolute; null when it has none.</summary>
    public string? Gateway { get; private set; }

    public IReadOnlyList<string> Permissions { get; private set; } = [];

    /// <summary>Permissions it may ask for at run time (<c>permissions.request</c>), never granted at install.</summary>
    public IReadOnlyList<string> OptionalPermissions { get; private set; } = [];

    /// <summary><c>onStartup</c>, <c>onView:&lt;id&gt;</c>, <c>onCommand:&lt;id&gt;</c>, <c>onEvent:&lt;name&gt;</c>, <c>onJob:&lt;id&gt;</c>.</summary>
    public IReadOnlyList<string> Activation { get; private set; } = [];

    /// <summary>The <c>astra.experimental.*</c> APIs it opts into.</summary>
    public IReadOnlyList<string> Experimental { get; private set; } = [];

    /// <summary>The v1 plugins (package names or specs) it takes over from (design/plugin-v2 07 §3).</summary>
    public IReadOnlyList<string> Replaces { get; private set; } = [];

    /// <summary>Each extension point's contribution, by extension point id, as validated.</summary>
    public IReadOnlyDictionary<string, JsonNode> Contributions { get; private set; } = new Dictionary<string, JsonNode>();

    /// <summary>Things that don't stop it loading: an extension point this host doesn't know, an unused permission.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    public ConfigurationSchema? Configuration { get; private set; }
    public IReadOnlyList<CommandContribution> Commands { get; private set; } = [];
    public IReadOnlyList<PageContribution> Pages { get; private set; } = [];
    public string? SettingsView { get; private set; }
    public IReadOnlyList<SlotContribution> Slots { get; private set; } = [];
    public IReadOnlyList<MenuContribution> Menus { get; private set; } = [];
    public IReadOnlyList<HookContribution> Hooks { get; private set; } = [];
    public IReadOnlyList<JobContribution> Jobs { get; private set; } = [];
    public IReadOnlyList<EventContribution> Events { get; private set; } = [];
    /// <summary>Service providers (2.1, design/plugin-v2 08 §2).</summary>
    public IReadOnlyList<ProviderContribution> Providers { get; private set; } = [];
    /// <summary>Clients it connects (2.1, 08 §3).</summary>
    public IReadOnlyList<ClientContribution> Clients { get; private set; } = [];
    /// <summary>Step-by-step flows (2.1, 08 §4).</summary>
    public IReadOnlyList<FlowContribution> Flows { get; private set; } = [];

    internal void SetProviders(IReadOnlyList<ProviderContribution> list) => Providers = list;
    internal void SetClients(IReadOnlyList<ClientContribution> list) => Clients = list;
    internal void SetFlows(IReadOnlyList<FlowContribution> list) => Flows = list;

    /// <summary>Whether the manifest file at <paramref name="root"/> is a v2 one (has <c>manifestVersion: 2</c>).</summary>
    public static bool IsV2(string root)
    {
        var file = Path.Combine(root, PluginV2Versions.ManifestFile);
        if (!System.IO.File.Exists(file)) return false;
        try
        {
            return JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                is JsonObject o && o["manifestVersion"] is JsonValue v && v.TryGetValue<int>(out var n) && n == 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads and checks the manifest of the plugin at <paramref name="root"/>.</summary>
    /// <exception cref="PluginManifestException">It is missing, not JSON, or breaks a rule; the message says which.</exception>
    public static PluginV2Manifest Read(string root)
    {
        root = Path.GetFullPath(root);
        var file = Path.Combine(root, PluginV2Versions.ManifestFile);
        JsonObject raw;
        try
        {
            raw = JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                  ?? throw new PluginManifestException($"{PluginV2Versions.ManifestFile} is not a JSON object");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PluginManifestException($"{PluginV2Versions.ManifestFile} can't be read: {e.Message}");
        }
        catch (JsonException e)
        {
            throw new PluginManifestException($"{PluginV2Versions.ManifestFile} is not JSON: {e.Message}");
        }
        return Parse(root, raw);
    }

    /// <summary>Checks a manifest object (as <see cref="Read"/> does, for one already in hand).</summary>
    public static PluginV2Manifest Parse(string root, JsonObject raw)
    {
        var m = new PluginV2Manifest(root, raw);
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!(raw["manifestVersion"] is JsonValue mv && mv.TryGetValue<int>(out var n) && n == 2))
            errors.Add("manifestVersion must be 2");
        m.Id = Str(raw, "id") ?? "";
        if (!IdPattern().IsMatch(m.Id)) errors.Add("id must be <publisher>.<name> in lower case, e.g. acme.word-guard");
        m.Version = Str(raw, "version") ?? "";
        if (!SemVer.IsFull(m.Version)) errors.Add("version must be a semver x.y.z");
        m.Name = raw["name"]?.DeepClone();
        if (LocalText.Resolve(m.Name, "en") is null) errors.Add("name is required (a string or { \"zh\": …, \"en\": … })");
        m.Description = raw["description"]?.DeepClone();
        m.Icon = Str(raw, "icon");
        if (raw["publisher"] is JsonObject pub)
        {
            m.PublisherName = Str(pub, "name");
            m.PublisherUrl = Str(pub, "url");
        }
        m.License = Str(raw, "license");
        m.Homepage = Str(raw, "homepage");

        if (raw["engines"] is JsonObject engines)
        {
            m.EnginesAstra = Str(engines, "astra");
            m.EnginesApi = Str(engines, "api");
            m.EnginesUi = Str(engines, "ui");
        }
        if (m.EnginesApi is { } api && !SemVer.Satisfies(PluginV2Versions.Api, api))
            errors.Add($"needs API {api}; this Astra speaks {PluginV2Versions.Api}");

        m.Main = ModuleFile(root, Str(raw, "main"), "main", errors);
        m.Gateway = ModuleFile(root, Str(raw, "gateway"), "gateway", errors);

        m.Permissions = Strings(raw["permissions"]);
        m.OptionalPermissions = Strings(raw["optionalPermissions"]);
        foreach (var p in m.Permissions.Concat(m.OptionalPermissions))
            if (PluginPermissions.Problem(p) is { } why) errors.Add(why);
        m.Activation = Strings(raw["activation"]);
        foreach (var a in m.Activation)
            if (!ActivationPattern().IsMatch(a)) errors.Add($"activation \"{a}\" is not onStartup, onView:<id>, onCommand:<id>, onEvent:<name> or onJob:<id>");
        m.Experimental = Strings(raw["experimental"]);
        m.Replaces = raw["replaces"] is JsonArray rep
            ? rep.Select(r => r is JsonObject o ? Str(o, "v1") : r is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList()
            : [];

        var contributions = new Dictionary<string, JsonNode>();
        if (raw["contributes"] is JsonObject contributes)
        {
            foreach (var (key, value) in contributes)
            {
                if (value is null) continue;
                var point = ExtensionPoints.Find(key);
                if (point is null)
                {
                    warnings.Add($"contributes.{key}: this Astra doesn't know that extension point, so it is ignored");
                    continue;
                }
                var before = errors.Count;
                point.Validate(m, value, errors);
                if (errors.Count == before)
                {
                    contributions[key] = value.DeepClone();
                    foreach (var need in point.RequiredPermissions(value))
                        if (!PluginPermissions.Holds(m.Permissions, need))
                            errors.Add($"contributes.{key} needs the permission \"{need}\"");
                }
            }
        }
        else if (raw["contributes"] is not null) errors.Add("contributes must be an object");
        m.Contributions = contributions;

        if (m.Main is null && (m.Pages.Count > 0 || m.Slots.Count > 0 || m.Commands.Count > 0 || m.Jobs.Count > 0 || m.SettingsView is not null
                               || m.Providers.Count > 0 || m.Clients.Count > 0 || m.Flows.Count > 0))
            errors.Add("main is required: the plugin contributes views, commands, jobs, providers, clients or flows");
        foreach (var p in m.Providers.Where(p => p.SignInFlow is not null && m.Flows.All(f => f.Id != p.SignInFlow)))
            errors.Add($"providers.{p.Id}: signIn.flow \"{p.SignInFlow}\" isn't one of its ui.flows");
        if (m.Gateway is null && m.Hooks.Count > 0)
            errors.Add("gateway is required: the plugin contributes gateway.hooks");

        // a keybinding the console or the desktop already uses never reaches the plugin (the host wins, 05 §8): say so
        foreach (var c in m.Commands.Where(c => c.Keybinding is not null))
            if (HostShortcut(c.Keybinding!) is { } host)
                warnings.Add($"commands.{c.Id}: keybinding {c.Keybinding} is the host's {host} shortcut, so it won't run the command");
        if (errors.Count > 0) throw new PluginManifestException(string.Join("; ", errors.Distinct()));
        m.Warnings = warnings;
        return m;
    }

    /// <summary>The host shortcut a keybinding collides with ("mod+k" → "command palette"), or null (web shell/commands.ts, ShortcutMap).</summary>
    internal static string? HostShortcut(string keybinding)
    {
        var parts = keybinding.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return null;
        var key = parts[^1];
        var mods = parts[..^1].Select(p => p is "cmd" or "ctrl" ? "mod" : p is "option" ? "alt" : p).Order(StringComparer.Ordinal).ToArray();
        var combo = string.Join('+', mods);
        return (combo, key) switch
        {
            ("mod", "k") => "command palette",
            ("mod", "f") => "find",
            ("mod", "r") => "refresh",
            ("mod", "n") => "new provider",
            ("mod", ",") or ("mod", "comma") => "settings",
            ("mod", "1" or "2" or "3" or "4" or "5" or "6") => "page",
            ("alt", "1" or "2" or "3" or "4" or "5" or "6") => "page",
            ("alt+mod", "s") => "sidebar",
            ("mod", "w") => "close window",
            ("mod", "q") => "quit",
            _ => null,
        };
    }

    /// <summary>Whether this Astra (<paramref name="hostVersion"/>) satisfies <c>engines.astra</c>; a preview counts as its release.</summary>
    public bool RunsOn(string hostVersion) =>
        EnginesAstra is null || !SemVer.TryParse(hostVersion, out var v) || SemVer.Satisfies(v.Release, EnginesAstra);

    /// <summary>The name in <paramref name="locale"/> (falling back to English, then to the id).</summary>
    public string DisplayName(string locale) => LocalText.Resolve(Name, locale) ?? Id;

    internal void Set(ConfigurationSchema? configuration = null, IReadOnlyList<CommandContribution>? commands = null,
        IReadOnlyList<PageContribution>? pages = null, string? settingsView = null, IReadOnlyList<SlotContribution>? slots = null,
        IReadOnlyList<MenuContribution>? menus = null, IReadOnlyList<HookContribution>? hooks = null, IReadOnlyList<JobContribution>? jobs = null,
        IReadOnlyList<EventContribution>? events = null)
    {
        if (configuration is not null) Configuration = configuration;
        if (commands is not null) Commands = commands;
        if (pages is not null) Pages = pages;
        if (settingsView is not null) SettingsView = settingsView;
        if (slots is not null) Slots = slots;
        if (menus is not null) Menus = menus;
        if (hooks is not null) Hooks = hooks;
        if (jobs is not null) Jobs = jobs;
        if (events is not null) Events = events;
    }

    // A module or asset path the manifest names: relative, inside the root, and there.
    private static string? ModuleFile(string root, string? rel, string what, List<string> errors)
    {
        if (rel is null) return null;
        if (Path.IsPathRooted(rel) || rel.Contains('\\'))
        {
            errors.Add($"{what} must be a relative path with / separators");
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            errors.Add($"{what} is outside the plugin");
            return null;
        }
        if (!System.IO.File.Exists(full))
        {
            errors.Add($"{what} \"{rel}\" doesn't exist");
            return null;
        }
        return full;
    }

    internal static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    internal static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => s.Length > 0).Distinct().ToList() : [];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}\\.[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^(onStartup|on(View|Command|Event|Job):[A-Za-z0-9._:-]{1,128})$")]
    private static partial Regex ActivationPattern();
}

/// <summary>A manifest that can't be loaded; the message lists every problem found.</summary>
public sealed class PluginManifestException(string message) : Exception(message);

/// <summary>Text a manifest or a plugin's locale file gives as a string or as <c>{ "zh": …, "en": … }</c>.</summary>
public static class LocalText
{
    /// <summary>The text for <paramref name="locale"/>: that language, else English, else the first one given; null when none.</summary>
    public static string? Resolve(JsonNode? node, string locale)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return string.IsNullOrWhiteSpace(s) ? null : s;
        if (node is not JsonObject o) return null;
        var lang = locale.Split('-', '_')[0].ToLowerInvariant();
        foreach (var key in new[] { locale, lang, "en" })
            if (o[key] is JsonValue lv && lv.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t)) return t;
        foreach (var (_, value) in o)
            if (value is JsonValue fv && fv.TryGetValue<string>(out var f) && !string.IsNullOrWhiteSpace(f)) return f;
        return null;
    }
}
