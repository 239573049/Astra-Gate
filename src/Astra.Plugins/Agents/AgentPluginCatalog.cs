using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Clients;
using Astra.Core.Plugins;
using Astra.Plugins.Js;
using Jint;
using Jint.Native;

namespace Astra.Plugins.Agents;

/// <summary>
/// The agents enabled plugins add (magpie's internal/agentplug): each plugin's <c>magpie.agent</c> module
/// (or a *.agent.js file) compiled on Jint in Astra's process, its <c>agent</c> export checked as magpie
/// checks it, and its <c>connect</c> run on demand. Loaded again when the plugin list or a module's file
/// changes, looked at once a second.
/// </summary>
public sealed partial class AgentPluginCatalog(PluginCatalog catalog, Action<string, string> log) : IAgentPluginCatalog
{
    /// <summary>How long a module may take to load, and connect to answer.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    /// <summary>The MCP file formats an agent's library.mcp may be in, named for the agent whose file it is written as.</summary>
    public static readonly string[] McpFormats =
    [
        "alma", "antigravity", "claude", "cline", "codex", "commandcode", "copilot", "crush", "cursor", "devin", "gemini",
        "goose", "grok", "hermes", "kimi", "omp", "opencode", "zed",
    ];

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile AgentSet _current = new("", [], new Dictionary<string, string>());
    private long _generation;
    private long _nextCheck;

    public long Generation => Interlocked.Read(ref _generation);

    public IReadOnlyList<AgentPluginDescriptor> List() => _current.Agents.Select(a => a.Descriptor).ToList();

    public IReadOnlyDictionary<string, string> Errors => _current.Errors;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _nextCheck);
        if (_current.Stamp.Length > 0 && (now < next || Interlocked.CompareExchange(ref _nextCheck, now + Stopwatch.Frequency, next) != next))
            return;
        var snapshot = await catalog.SnapshotAsync(ct);
        var found = snapshot.Plugins.Where(p => p.Entry.Enabled && p.Manifest.AgentFile is not null).ToList();
        var stamp = Stamp(snapshot.Generation, found);
        if (_current.Stamp == stamp) return;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_current.Stamp == stamp) return;
            _current = Load(found, stamp);
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    public JsonObject? Connect(string agentId, JsonObject input)
    {
        var agent = _current.Agents.FirstOrDefault(a => a.Descriptor.Id == agentId)
                    ?? throw new InvalidOperationException($"no agent plugin \"{agentId}\" is loaded");
        if (agent.Connect is null) return null;
        lock (agent)
        {
            JsValue result;
            try
            {
                result = agent.Runtime.Call(agent.Connect, Limit, agent.Runtime.ParseJson(input.ToJsonString()));
            }
            catch (JsHookException e)
            {
                throw new InvalidOperationException(e.Message.StartsWith("rejected: ", StringComparison.Ordinal)
                    ? "connect " + e.Message
                    : "connect: " + e.Message);
            }
            if (result.IsUndefined() || result.IsNull()) return null;
            if (!result.IsObject()) throw new InvalidOperationException("connect returned neither an object nor nothing");
            if (result.IsArray()) throw new InvalidOperationException("connect returned no object of keys");
            return JsonNode.Parse(agent.Runtime.ToJson(result) ?? "{}") as JsonObject
                   ?? throw new InvalidOperationException("connect returned no object of keys");
        }
    }

    private static string Stamp(long generation, List<InstalledPlugin> found)
    {
        var b = new StringBuilder().Append(generation);
        foreach (var p in found)
        {
            var file = p.Manifest.AgentFile!;
            var fi = new FileInfo(file);
            b.Append('|').Append(file);
            if (fi.Exists) b.Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(':').Append(fi.Length);
        }
        return b.ToString();
    }

    private AgentSet Load(List<InstalledPlugin> found, string stamp)
    {
        var agents = new List<LoadedAgent>();
        var errors = new Dictionary<string, string>();
        var seen = new Dictionary<string, string>();
        foreach (var p in found)
        {
            try
            {
                var agent = Compile(p);
                if (seen.TryGetValue(agent.Descriptor.Id, out var other))
                    throw new InvalidOperationException($"agent id \"{agent.Descriptor.Id}\" is {other}'s too");
                if (ClientKinds.All.Contains(agent.Descriptor.Id) || agent.Descriptor.Id == "astra")
                    throw new InvalidOperationException($"agent id \"{agent.Descriptor.Id}\" is Astra's own client's");
                seen[agent.Descriptor.Id] = p.Entry.Name;
                agents.Add(agent);
            }
            catch (Exception e) when (e is InvalidOperationException or JsHookException or IOException or UnauthorizedAccessException)
            {
                errors[p.Entry.Spec] = e.Message;
                log("warn", $"agent plugin {p.Entry.Name} didn't load: {e.Message}");
            }
        }
        return new AgentSet(stamp, agents, errors);
    }

    private LoadedAgent Compile(InstalledPlugin p)
    {
        var loader = new JsModuleLoader(p.Manifest.ModuleRoot, p.Manifest.AgentFile!, "an agent plugin");
        var rt = new JsRuntime(loader, (_, line) => log("info", $"agent plugin {p.Entry.Name}: {line}"), ["log", "warn", "error", "info"]);
        rt.Load(Limit);
        var exported = rt.Export("agent");
        if (!exported.IsObject() || exported.IsArray()) throw new InvalidOperationException("exports no agent object");
        var desc = JsonNode.Parse(rt.ToJson(exported) ?? "{}") as JsonObject ?? throw new InvalidOperationException("exports no agent object");
        var d = Check(desc);
        d.Spec = p.Entry.Spec;
        var connect = rt.Export("connect");
        d.HasConnect = connect is Jint.Native.Function.Function;
        return new LoadedAgent(d, rt, d.HasConnect ? connect : null);
    }

    // magpie's Desc.check: the id, the file and the model key are required; the format comes from the
    // file's name when not said; the library's MCP file needs one of the known formats.
    private static AgentPluginDescriptor Check(JsonObject o)
    {
        var d = new AgentPluginDescriptor
        {
            Id = Str(o, "id").Trim(),
            Name = Str(o, "name"),
            Config = Str(o, "config"),
            Format = Str(o, "format"),
            Model = Str(o, "model"),
            Prefix = NullIfEmpty(Str(o, "prefix")),
            Bin = NullIfEmpty(Str(o, "bin")),
            Dir = NullIfEmpty(Str(o, "dir")),
            Icon = NullIfEmpty(Str(o, "icon")),
            Notice = NullIfEmpty(Str(o, "notice")),
        };
        if (o["ua"] is JsonArray ua)
            foreach (var u in ua)
                if (u is JsonValue v && v.TryGetValue<string>(out var s)) d.Ua.Add(s.ToLowerInvariant());
        if (!AgentId().IsMatch(d.Id))
            throw new InvalidOperationException($"agent.id \"{d.Id}\": lower-case letters, digits, '.', '_' and '-' only");
        if (string.IsNullOrWhiteSpace(d.Config)) throw new InvalidOperationException("agent.config names no file");
        if (string.IsNullOrWhiteSpace(d.Model)) throw new InvalidOperationException("agent.model names no key");
        if (d.Name.Length == 0) d.Name = d.Id;
        if (d.Format.Length == 0)
        {
            d.Format = Path.GetExtension(d.Config).ToLowerInvariant() switch
            {
                ".json" or ".jsonc" => "json",
                ".yaml" or ".yml" => "yaml",
                ".env" => "env",
                _ => "",
            };
            if (Path.GetFileName(d.Config).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) d.Format = "env";
        }
        switch (d.Format)
        {
            case "json" or "yaml" or "env":
                break;
            case "":
                throw new InvalidOperationException($"agent.config \"{d.Config}\": say its agent.format (json, yaml or env)");
            default:
                throw new InvalidOperationException($"agent.format \"{d.Format}\": json, yaml or env");
        }
        if (o["library"] is JsonObject library)
        {
            var mcp = Str(library, "mcp").Trim();
            var mcpFormat = Str(library, "mcpFormat").Trim().ToLowerInvariant();
            if (mcp.Length == 0 && mcpFormat.Length > 0)
                throw new InvalidOperationException("agent.library.mcpFormat names a format, but agent.library.mcp no file");
            if (mcp.Length > 0 && mcpFormat.Length == 0)
                throw new InvalidOperationException($"agent.library.mcp \"{mcp}\": say its agent.library.mcpFormat, one of {string.Join(", ", McpFormats)}");
            if (mcp.Length > 0 && !McpFormats.Contains(mcpFormat))
                throw new InvalidOperationException($"agent.library.mcpFormat \"{mcpFormat}\": one of {string.Join(", ", McpFormats)}");
            d.Library = library;
        }
        return d;
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex AgentId();

    private sealed record LoadedAgent(AgentPluginDescriptor Descriptor, JsRuntime Runtime, JsValue? Connect);

    private sealed record AgentSet(string Stamp, List<LoadedAgent> Agents, IReadOnlyDictionary<string, string> Errors);
}
