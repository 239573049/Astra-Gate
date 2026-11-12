using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Plugins.V2;

/// <summary>
/// One extension point (design/plugin-v2 01 §5): an id a manifest's <c>contributes</c> uses, the shape of its value,
/// the permissions contributing to it needs, and its own version. The loader knows none of them by name — adding one is
/// a class here and a line in <see cref="ExtensionPoints.All"/>; listing, validation, the permission check and the
/// docs cover it from there.
/// </summary>
public abstract class ExtensionPoint
{
    public abstract string Id { get; }

    /// <summary>This extension point's own version (<c>astra.has('gateway.hooks@2')</c>).</summary>
    public virtual int Version => 1;

    /// <summary>Checks <paramref name="value"/>, adding a line per problem, and stores the typed form on the manifest.</summary>
    public abstract void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors);

    /// <summary>The permissions this contribution needs granted.</summary>
    public virtual IEnumerable<string> RequiredPermissions(JsonNode value) => [];

    protected static string? Str(JsonObject o, string key) => PluginV2Manifest.Str(o, key);
    protected static List<string> Strings(JsonNode? node) => PluginV2Manifest.Strings(node);

    protected static List<JsonObject> Items(JsonNode value, string id, List<string> errors)
    {
        if (value is not JsonArray a)
        {
            errors.Add($"contributes.{id} must be an array");
            return [];
        }
        var items = new List<JsonObject>();
        foreach (var item in a)
        {
            if (item is JsonObject o) items.Add(o);
            else errors.Add($"contributes.{id}: every entry must be an object");
        }
        return items;
    }

    protected static bool IsName(string? s) => s is not null && NamePattern.IsMatch(s);

    protected static void Unique<T>(IEnumerable<T> items, Func<T, string> key, string id, List<string> errors)
    {
        foreach (var dup in items.GroupBy(key).Where(g => g.Count() > 1))
            errors.Add($"contributes.{id}: \"{dup.Key}\" is declared twice");
    }

    private static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);
}

/// <summary>The extension points this host has.</summary>
public static class ExtensionPoints
{
    public static readonly IReadOnlyList<ExtensionPoint> All =
    [
        new ConfigurationPoint(), new CommandsPoint(), new PagesPoint(), new SettingsPoint(), new SlotsPoint(), new MenusPoint(),
        new HooksPoint(), new JobsPoint(), new EventsPoint(), new ProvidersPoint(), new ClientsPoint(), new FlowsPoint(),
    ];

    public static ExtensionPoint? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>The named slots of the host's pages a view may fill (design/plugin-v2 03 §8), with what each passes as params.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SlotNames = new Dictionary<string, string[]>
    {
        ["overview.cards"] = ["range"],
        ["provider.detail.tabs"] = ["providerId"],
        ["provider.account.sections"] = ["providerId", "accountId"],
        ["request.detail.sections"] = ["requestId"],
        ["client.detail.sections"] = ["clientKind"],
        ["token.detail.sections"] = ["tokenId"],
        ["settings.sections"] = [],
        ["tray.panel"] = [],
    };

    /// <summary>The host menus a command may be added to.</summary>
    public static readonly IReadOnlyList<string> MenuNames =
        ["request.row", "provider.row", "provider.account.row", "token.row", "client.row", "command.palette"];

    /// <summary>The navigation groups a page may join (the sidebar's own groups).</summary>
    public static readonly IReadOnlyList<string> NavGroups = ["overview", "gateway", "resources", "tools", "plugins"];
}

/// <summary>A setting the plugin's settings form has (design/plugin-v2 02 §9).</summary>
/// <param name="Type">string, number, integer, boolean, array or object.</param>
public sealed record ConfigurationProperty(string Name, string Type, JsonNode? Title, JsonNode? Description, JsonNode? Default,
    List<string>? Enum, JsonObject? EnumTitles, double? Minimum, double? Maximum, bool Secret, string? Widget, string? Unit, string? Pattern,
    string? ItemsType, bool Required);

public sealed record ConfigurationGroup(JsonNode? Title, List<string> Properties);

public sealed record ConfigurationSchema(List<ConfigurationProperty> Properties, List<ConfigurationGroup> Groups);

public sealed record CommandContribution(string Id, JsonNode? Title, string? Icon, string? Keybinding);

public sealed record PageRoute(string Path, string View);

public sealed record PageContribution(string Id, JsonNode? Title, string? Icon, string Nav, List<PageRoute> Routes);

public sealed record SlotContribution(string Slot, string View, JsonNode? Title);

public sealed record MenuContribution(string Menu, string Command, JsonNode? Title, string? Icon);

/// <summary>A model a provider lists in its manifest (design/plugin-v2 08 §2).</summary>
public sealed record ProviderModelContribution(string Id, JsonNode? Name, string Protocol, long? Context, long? Output, bool Vision, bool Reasoning);

/// <summary>A service provider a plugin adds (08 §2): the host sends its requests, the plugin's prepare shapes them.</summary>
/// <param name="Protocols">chat, responses, anthropic, gemini: what its upstream speaks.</param>
/// <param name="SignInFlow">The ui.flows id that signs an account in, or null.</param>
/// <param name="Reply">How its upstream replies are framed and what the plugin's <c>transformResponse</c> turns them into, or null (the reply is already in one of <paramref name="Protocols"/>' wire format).</param>
public sealed record ProviderContribution(string Id, JsonNode? Name, string? Icon, List<string> Protocols, List<ProviderModelContribution> Models, string? SignInFlow,
    ProviderReply? Reply = null);

/// <summary>A provider's <c>reply</c> block (08 §2.2): the host frames the upstream reply (<see cref="Framing"/>), the plugin's transformResponse translates frame by frame into <see cref="Output"/>'s wire format.</summary>
public sealed record ProviderReply(string Framing, string Output);

/// <summary>The framings a provider's <c>reply.framing</c> may name.</summary>
public static class ReplyFramings
{
    public const string Sse = "sse";
    public const string Ndjson = "ndjson";
    public const string Json = "json";
    public const string AwsEventStream = "aws-eventstream";

    /// <summary>Connect streaming (application/connect+proto / +json): a flags byte and a 4-byte big-endian length per message; flag 0x02 is the end-of-stream message.</summary>
    public const string Connect = "connect";

    public static readonly string[] All = [Sse, Ndjson, Json, AwsEventStream, Connect];
}

/// <summary>A client a plugin teaches Astra to connect (08 §3): the same descriptor as a v1 agent plugin's.</summary>
public sealed record ClientContribution(string Id, JsonNode? Name, string Config, string Format, string Model, string? Prefix, string? Bin, string? Dir,
    string? Icon, string? Notice, List<string> Ua);

/// <summary>A step-by-step flow (sign-in, wizard) shown in a sheet (08 §4).</summary>
public sealed record FlowContribution(string Id, string View, JsonNode? Title);

/// <summary>One set of gateway hooks (design/plugin-v2 02 §3).</summary>
/// <param name="On">request, upstream, event, text, response, complete.</param>
/// <param name="View">raw (the client's own JSON) or ir (the protocol-free view).</param>
/// <param name="Reads">storage keys handed to the hooks as <c>ctx.shared</c>.</param>
public sealed record HookContribution(string Id, List<string> On, string View, HookMatch Match, List<string>? Events, List<string> Reads);

/// <summary>Which requests a hook set is offered: the host checks it before taking an engine.</summary>
public sealed record HookMatch(List<string>? Protocols, List<string>? Models, List<string>? Clients, bool? Stream)
{
    public bool Matches(string protocol, string? model, string? client, bool stream) =>
        (Protocols is null || Protocols.Contains(protocol))
        && (Models is null || (model is not null && Models.Any(p => Glob(p, model))))
        && (Clients is null || (client is not null && Clients.Contains(client)))
        && (Stream is null || Stream == stream);

    private static bool Glob(string pattern, string value)
    {
        if (!pattern.Contains('*')) return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        var parts = pattern.Split('*');
        var at = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) continue;
            var found = value.IndexOf(p, at, StringComparison.OrdinalIgnoreCase);
            if (found < 0 || (i == 0 && found != 0)) return false;
            at = found + p.Length;
        }
        return parts[^1].Length == 0 || value.EndsWith(parts[^1], StringComparison.OrdinalIgnoreCase);
    }
}

/// <param name="Every">An interval (30s minimum: 30s, 15m, 6h, 1d); null with a cron.</param>
/// <param name="Cron">A five-field cron, in the server's local time.</param>
public sealed record JobContribution(string Id, TimeSpan? Every, string? Cron);

public sealed record EventContribution(string Name, JsonNode? Description);

internal sealed class ConfigurationPoint : ExtensionPoint
{
    private static readonly string[] Types = ["string", "number", "integer", "boolean", "array", "object"];
    private static readonly string[] Widgets = ["text", "textarea", "password", "select", "segmented", "radio", "switch", "tags", "number", "slider", "code", "keyvalue", "date", "model", "provider"];

    public override string Id => "configuration";

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        if (value is not JsonObject o || o["properties"] is not JsonObject props)
        {
            errors.Add("contributes.configuration must be an object with properties");
            return;
        }
        var required = Strings(o["required"]);
        var list = new List<ConfigurationProperty>();
        foreach (var (name, node) in props)
        {
            if (node is not JsonObject p || !IsName(name.Replace('.', '_')))
            {
                errors.Add($"configuration.{name}: not a setting");
                continue;
            }
            var type = Str(p, "type") ?? "string";
            if (!Types.Contains(type)) errors.Add($"configuration.{name}: unknown type \"{type}\"");
            var widget = Str(p, "widget");
            if (widget is not null && !Widgets.Contains(widget)) errors.Add($"configuration.{name}: unknown widget \"{widget}\"");
            var secret = p["secret"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
            if (secret && type != "string") errors.Add($"configuration.{name}: only a string can be secret");
            var enumValues = p["enum"] is JsonArray ? Strings(p["enum"]) : null;
            list.Add(new ConfigurationProperty(name, type, p["title"]?.DeepClone(), p["description"]?.DeepClone(), secret ? null : p["default"]?.DeepClone(),
                enumValues, p["enumTitles"]?.DeepClone() as JsonObject, Num(p, "minimum"), Num(p, "maximum"), secret, widget, Str(p, "unit"),
                Str(p, "pattern"), (p["items"] as JsonObject) is { } items ? Str(items, "type") : null, required.Contains(name)));
        }
        var groups = new List<ConfigurationGroup>();
        if (o["groups"] is JsonArray ga)
            foreach (var g in ga.OfType<JsonObject>())
            {
                var names = Strings(g["properties"]);
                foreach (var n in names.Where(n => list.All(p => p.Name != n)))
                    errors.Add($"configuration.groups: \"{n}\" is not a setting");
                groups.Add(new ConfigurationGroup(g["title"]?.DeepClone(), names));
            }
        manifest.Set(configuration: new ConfigurationSchema(list, groups));
    }

    private static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}

internal sealed class CommandsPoint : ExtensionPoint
{
    public override string Id => "commands";

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<CommandContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            if (!IsName(id)) errors.Add($"commands: \"{id}\" is not a command id");
            else list.Add(new CommandContribution(id!, o["title"]?.DeepClone(), Str(o, "icon"), Str(o, "keybinding")));
        }
        Unique(list, c => c.Id, Id, errors);
        manifest.Set(commands: list);
    }
}

internal sealed class PagesPoint : ExtensionPoint
{
    public override string Id => "ui.pages";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Ui];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<PageContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            if (!IsName(id))
            {
                errors.Add($"ui.pages: \"{id}\" is not a page id");
                continue;
            }
            var nav = Str(o, "nav") ?? "plugins";
            if (!ExtensionPoints.NavGroups.Contains(nav)) errors.Add($"ui.pages.{id}: nav must be one of {string.Join(", ", ExtensionPoints.NavGroups)}");
            var routes = new List<PageRoute>();
            if (o["routes"] is JsonArray ra)
                foreach (var r in ra.OfType<JsonObject>())
                {
                    var view = Str(r, "view");
                    var path = (Str(r, "path") ?? "").Trim('/');
                    if (!IsName(view)) errors.Add($"ui.pages.{id}: a route needs a view");
                    else if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(seg => !(IsName(seg) || (seg.Length > 1 && seg[0] == ':' && IsName(seg[1..])))))
                        errors.Add($"ui.pages.{id}: route \"{path}\" has a segment that is neither a name nor :param");
                    else routes.Add(new PageRoute(path, view!));
                }
            else routes.Add(new PageRoute("", Str(o, "view") ?? id!));
            if (routes.All(r => r.Path.Length != 0)) errors.Add($"ui.pages.{id}: needs a route for \"\" (the page itself)");
            list.Add(new PageContribution(id!, o["title"]?.DeepClone(), Str(o, "icon"), nav, routes));
        }
        Unique(list, p => p.Id, Id, errors);
        manifest.Set(pages: list);
    }
}

internal sealed class SettingsPoint : ExtensionPoint
{
    public override string Id => "ui.settings";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Ui];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var view = value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value is JsonObject o ? Str(o, "view") : null;
        if (!IsName(view)) errors.Add("ui.settings must name a view");
        else manifest.Set(settingsView: view);
    }
}

internal sealed class SlotsPoint : ExtensionPoint
{
    public override string Id => "ui.slots";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Ui];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<SlotContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var slot = Str(o, "slot");
            var view = Str(o, "view");
            if (slot is null || !ExtensionPoints.SlotNames.ContainsKey(slot)) errors.Add($"ui.slots: \"{slot}\" is not a slot of this Astra");
            else if (!IsName(view)) errors.Add($"ui.slots.{slot}: needs a view");
            else list.Add(new SlotContribution(slot, view!, o["title"]?.DeepClone()));
        }
        manifest.Set(slots: list);
    }
}

internal sealed class MenusPoint : ExtensionPoint
{
    public override string Id => "ui.menus";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Ui];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<MenuContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var menu = Str(o, "menu");
            var command = Str(o, "command");
            if (menu is null || !ExtensionPoints.MenuNames.Contains(menu)) errors.Add($"ui.menus: \"{menu}\" is not a menu of this Astra");
            else if (!IsName(command)) errors.Add($"ui.menus.{menu}: needs a command");
            else list.Add(new MenuContribution(menu, command!, o["title"]?.DeepClone(), Str(o, "icon")));
        }
        manifest.Set(menus: list);
    }
}

internal sealed class HooksPoint : ExtensionPoint
{
    private static readonly string[] Kinds = ["request", "upstream", "event", "text", "response", "complete"];
    private static readonly string[] Protocols = ["anthropic", "chat", "responses", "gemini"];

    public override string Id => "gateway.hooks";

    public override int Version => 2;

    public override IEnumerable<string> RequiredPermissions(JsonNode value)
    {
        var on = value is JsonArray a ? a.OfType<JsonObject>().SelectMany(o => Strings(o["on"])).ToHashSet() : [];
        if (on.Contains("request")) yield return PluginPermissions.RequestRead;
        if (on.Contains("event") || on.Contains("text") || on.Contains("response")) yield return PluginPermissions.ResponseRead;
        if (on.Contains("upstream")) yield return PluginPermissions.RequestRead;
        if (on.Contains("complete")) yield return PluginPermissions.ResponseRead;
    }

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<HookContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            if (!IsName(id))
            {
                errors.Add($"gateway.hooks: \"{id}\" is not a hook set id");
                continue;
            }
            var on = Strings(o["on"]);
            if (on.Count == 0) errors.Add($"gateway.hooks.{id}: on lists none of {string.Join(", ", Kinds)}");
            foreach (var k in on.Where(k => !Kinds.Contains(k))) errors.Add($"gateway.hooks.{id}: unknown hook \"{k}\"");
            var view = Str(o, "view") ?? "raw";
            if (view is not ("raw" or "ir")) errors.Add($"gateway.hooks.{id}: view must be raw or ir");
            if (on.Contains("text") && view != "ir") errors.Add($"gateway.hooks.{id}: the text hook needs view \"ir\"");
            HookMatch match = new(null, null, null, null);
            if (o["match"] is JsonObject mo)
            {
                var protocols = mo["protocols"] is JsonArray ? Strings(mo["protocols"]) : null;
                foreach (var p in protocols?.Where(p => !Protocols.Contains(p)) ?? []) errors.Add($"gateway.hooks.{id}: unknown protocol \"{p}\"");
                match = new HookMatch(protocols, mo["models"] is JsonArray ? Strings(mo["models"]) : null,
                    mo["clients"] is JsonArray ? Strings(mo["clients"]) : null,
                    mo["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) ? st : null);
            }
            var reads = Strings(o["reads"]);
            if (reads.Count > 0 && !manifest.Permissions.Contains(PluginPermissions.Storage))
                errors.Add($"gateway.hooks.{id}: reads needs the permission \"storage\"");
            list.Add(new HookContribution(id!, on, view, match, o["events"] is JsonArray ? Strings(o["events"]) : null, reads));
        }
        Unique(list, h => h.Id, Id, errors);
        manifest.Set(hooks: list);
    }
}

internal sealed class JobsPoint : ExtensionPoint
{
    public override string Id => "jobs";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Jobs];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<JobContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            var every = Str(o, "every");
            var cron = Str(o, "cron");
            TimeSpan? interval = null;
            if (!IsName(id)) errors.Add($"jobs: \"{id}\" is not a job id");
            else if ((every is null) == (cron is null)) errors.Add($"jobs.{id}: give every or cron (one of them)");
            else if (every is not null && (interval = Interval.Parse(every)) is null) errors.Add($"jobs.{id}: every \"{every}\" is not 30s or more (30s, 15m, 6h, 1d)");
            else if (cron is not null && !Cron.TryParse(cron, out _)) errors.Add($"jobs.{id}: \"{cron}\" is not a five-field cron");
            else list.Add(new JobContribution(id!, interval, cron));
        }
        Unique(list, j => j.Id, Id, errors);
        manifest.Set(jobs: list);
    }
}

internal sealed class EventsPoint : ExtensionPoint
{
    public override string Id => "events";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Events];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<EventContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var name = Str(o, "name");
            if (!IsName(name)) errors.Add($"events: \"{name}\" is not an event name");
            else list.Add(new EventContribution(name!, o["description"]?.DeepClone()));
        }
        Unique(list, e => e.Name, Id, errors);
        manifest.Set(events: list);
    }
}

/// <summary>"30s", "15m", "6h", "1d" → a TimeSpan of 30 s or more; null otherwise.</summary>
public static class Interval
{
    public static TimeSpan? Parse(string text)
    {
        if (text.Length < 2 || !double.TryParse(text[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;
        TimeSpan? t = text[^1] switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => null,
        };
        return t is { } v && v >= TimeSpan.FromSeconds(30) && v <= TimeSpan.FromDays(366) ? v : null;
    }
}

/// <summary>A five-field cron (minute hour day-of-month month day-of-week) with *, lists, ranges and steps.</summary>
public sealed class Cron
{
    private readonly bool[][] _fields;
    private readonly bool _anyDom, _anyDow;

    private Cron(bool[][] fields, bool anyDom, bool anyDow)
    {
        _fields = fields;
        _anyDom = anyDom;
        _anyDow = anyDow;
    }

    private static readonly (int Min, int Max)[] Ranges = [(0, 59), (0, 23), (1, 31), (1, 12), (0, 7)];

    public static bool TryParse(string text, out Cron cron)
    {
        cron = null!;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return false;
        var fields = new bool[5][];
        for (var i = 0; i < 5; i++)
        {
            var (min, max) = Ranges[i];
            fields[i] = new bool[max + 1];
            foreach (var item in parts[i].Split(','))
            {
                var step = 1;
                var range = item;
                var slash = item.IndexOf('/');
                if (slash >= 0)
                {
                    if (!int.TryParse(item[(slash + 1)..], out step) || step < 1) return false;
                    range = item[..slash];
                }
                int lo, hi;
                if (range == "*") (lo, hi) = (min, max);
                else if (range.Contains('-'))
                {
                    var ab = range.Split('-');
                    if (ab.Length != 2 || !int.TryParse(ab[0], out lo) || !int.TryParse(ab[1], out hi)) return false;
                }
                else
                {
                    if (!int.TryParse(range, out lo)) return false;
                    hi = slash >= 0 ? max : lo;
                }
                if (lo < min || hi > max || lo > hi) return false;
                for (var v = lo; v <= hi; v += step) fields[i][v] = true;
            }
        }
        if (fields[4][7]) fields[4][0] = true;
        cron = new Cron(fields, parts[2] == "*", parts[4] == "*");
        return true;
    }

    /// <summary>The first minute after <paramref name="after"/> (local time) the cron names; null if none within a year.</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var end = t.AddYears(1);
        while (t < end)
        {
            if (!_fields[3][t.Month]) { t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1); continue; }
            var dom = _fields[2][t.Day];
            var dow = _fields[4][(int)t.DayOfWeek];
            var dayOk = _anyDom && _anyDow ? true : _anyDom ? dow : _anyDow ? dom : dom || dow;
            if (!dayOk) { t = t.Date.AddDays(1); continue; }
            if (!_fields[1][t.Hour]) { t = t.Date.AddHours(t.Hour + 1); continue; }
            if (!_fields[0][t.Minute]) { t = t.AddMinutes(1); continue; }
            return t;
        }
        return null;
    }
}

internal sealed class ProvidersPoint : ExtensionPoint
{
    public override string Id => "providers";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Providers];

    private static readonly string[] Protocols = ["chat", "responses", "anthropic", "gemini"];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<ProviderContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            if (!IsName(id))
            {
                errors.Add($"providers: \"{id}\" is not a provider id");
                continue;
            }
            var protocols = Strings(o["protocols"]);
            if (protocols.Count == 0) errors.Add($"providers.{id}: protocols lists none of {string.Join(", ", Protocols)}");
            foreach (var p in protocols.Where(p => !Protocols.Contains(p))) errors.Add($"providers.{id}: unknown protocol \"{p}\"");
            var models = new List<ProviderModelContribution>();
            foreach (var m in o["models"] is JsonArray arr ? arr.OfType<JsonObject>() : [])
            {
                var mid = Str(m, "id");
                if (mid is null)
                {
                    errors.Add($"providers.{id}: a model has no id");
                    continue;
                }
                var protocol = Str(m, "protocol") ?? protocols.FirstOrDefault() ?? "chat";
                if (!protocols.Contains(protocol)) errors.Add($"providers.{id}.models.{mid}: protocol {protocol} isn't one the provider lists");
                models.Add(new ProviderModelContribution(mid, m["name"]?.DeepClone(), protocol, Long(m, "context"), Long(m, "output"),
                    m["vision"] is JsonValue v && v.TryGetValue<bool>(out var vb) && vb, m["reasoning"] is JsonValue r && r.TryGetValue<bool>(out var rb) && rb));
            }
            var flow = o["signIn"] is JsonObject si ? Str(si, "flow") : null;
            // reply: no permission of its own — it reshapes only the reply of the plugin's own provider, and what the plugin
            // can reach is still decided by the net: grants on prepare's address (design/plugin-v2 08 §2.2)
            ProviderReply? reply = null;
            if (o["reply"] is { } rv)
            {
                if (rv is not JsonObject ro) errors.Add($"providers.{id}.reply: must be an object {{ framing, output }}");
                else
                {
                    var framing = Str(ro, "framing");
                    var output = Str(ro, "output") ?? protocols.FirstOrDefault() ?? "chat";
                    if (framing is null || !ReplyFramings.All.Contains(framing))
                        errors.Add($"providers.{id}.reply.framing: \"{framing}\" is not one of {string.Join(", ", ReplyFramings.All)}");
                    else if (!protocols.Contains(output))
                        errors.Add($"providers.{id}.reply.output: \"{output}\" isn't one of the provider's protocols ({string.Join(", ", protocols)})");
                    else reply = new ProviderReply(framing, output);
                    foreach (var k in ro.Select(kv => kv.Key).Where(k => k is not ("framing" or "output")))
                        errors.Add($"providers.{id}.reply: unknown key \"{k}\"");
                }
            }
            list.Add(new ProviderContribution(id!, o["name"]?.DeepClone(), Str(o, "icon"), protocols, models, flow, reply));
        }
        Unique(list, p => p.Id, Id, errors);
        manifest.SetProviders(list);
    }

    private static long? Long(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;
}

internal sealed class ClientsPoint : ExtensionPoint
{
    public override string Id => "clients";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Clients];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<ClientContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9][a-z0-9._-]{0,63}$"))
            {
                errors.Add($"clients: \"{id}\" is not a client id (lower case, digits, . _ -)");
                continue;
            }
            var config = Str(o, "config");
            var format = Str(o, "format") ?? "json";
            var model = Str(o, "model");
            if (config is null) errors.Add($"clients.{id}: config (the file Astra edits) is required");
            if (format is not ("json" or "yaml" or "env")) errors.Add($"clients.{id}: format must be json, yaml or env");
            if (model is null) errors.Add($"clients.{id}: model (the key path of the model) is required");
            if (config is null || model is null) continue;
            list.Add(new ClientContribution(id, o["name"]?.DeepClone(), config, format, model, Str(o, "prefix"), Str(o, "bin"), Str(o, "dir"),
                Str(o, "icon"), Str(o, "notice"), Strings(o["ua"])));
        }
        Unique(list, c => c.Id, Id, errors);
        manifest.SetClients(list);
    }
}

internal sealed class FlowsPoint : ExtensionPoint
{
    public override string Id => "ui.flows";

    public override IEnumerable<string> RequiredPermissions(JsonNode value) => [PluginPermissions.Ui];

    public override void Validate(PluginV2Manifest manifest, JsonNode value, List<string> errors)
    {
        var list = new List<FlowContribution>();
        foreach (var o in Items(value, Id, errors))
        {
            var id = Str(o, "id");
            var view = Str(o, "view") ?? id;
            if (!IsName(id)) errors.Add($"ui.flows: \"{id}\" is not a flow id");
            else list.Add(new FlowContribution(id!, view!, o["title"]?.DeepClone()));
        }
        Unique(list, f => f.Id, Id, errors);
        manifest.SetFlows(list);
    }
}
