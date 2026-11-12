using System.Text.Json.Nodes;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2.Ui;

/// <summary>The UI 1.0 component schema (components.json): what <see cref="UiTree"/> checks a view's tree against.</summary>
public sealed class UiSchema
{
    private readonly Dictionary<string, Component> _components = [];
    private readonly Dictionary<string, string> _common = [];
    private readonly Dictionary<string, string> _input = [];
    private readonly Dictionary<string, HashSet<string>> _tokens = [];
    private readonly Dictionary<string, HashSet<string>> _slotRoots = [];

    public static UiSchema Default { get; } = new(V2Assets.Components());

    /// <summary>components.json as embedded: what GET /api/plugins/v2/ui/schema serves the shells.</summary>
    public static string Json => V2Assets.ComponentsJson;

    public UiSchema(JsonObject schema)
    {
        Version = schema["version"]?.GetValue<string>() ?? "1.0";
        foreach (var (k, v) in (JsonObject)schema["common"]!) _common[k] = v!.GetValue<string>();
        foreach (var (k, v) in (JsonObject)schema["inputProps"]!) _input[k] = v!.GetValue<string>();
        foreach (var (k, v) in (JsonObject)schema["tokens"]!) _tokens[k] = [.. ((JsonArray)v!).Select(x => x!.GetValue<string>())];
        if (schema["slotRoots"] is JsonObject roots)
            foreach (var (k, v) in roots) _slotRoots[k] = [.. ((JsonArray)v!).Select(x => x!.GetValue<string>())];
        foreach (var (name, node) in (JsonObject)schema["components"]!)
        {
            var o = (JsonObject)node!;
            var props = new Dictionary<string, string>(_common);
            var input = o["input"] is JsonValue iv && iv.GetValue<bool>();
            if (input) foreach (var (k, v) in _input) props[k] = v;
            foreach (var (k, v) in (JsonObject)o["props"]!) props[k] = v!.GetValue<string>();
            var required = o["required"] is JsonArray r ? r.Select(x => x!.GetValue<string>()).ToList() : [];
            _components[name] = new Component(name, props, required, o["children"]?.GetValue<string>() ?? "none", input);
        }
    }

    public string Version { get; }

    /// <summary>Every component name, in schema order.</summary>
    public IReadOnlyList<string> Names => [.. _components.Keys];

    public Component? Find(string type) => _components.GetValueOrDefault(type);

    public IReadOnlySet<string> Token(string name) => _tokens.TryGetValue(name, out var t) ? t : new HashSet<string>();

    /// <summary>The root components a slot allows (the tray's compact set); null when any.</summary>
    public IReadOnlySet<string>? SlotRoots(string slot) => _slotRoots.GetValueOrDefault(slot);

    /// <param name="Children">none, any, one, two or text.</param>
    public sealed record Component(string Name, IReadOnlyDictionary<string, string> Props, IReadOnlyList<string> Required, string Children, bool Input);
}
