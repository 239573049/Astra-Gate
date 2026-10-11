using System.Text.Json.Nodes;

namespace Astra.Core.Plugins;

/// <summary>
/// An agent a plugin adds (a magpie agent plugin's <c>export const agent</c>): a client Astra has no
/// adapter of its own for. The plugin says only where things go — the file, the model key, its
/// prefix — and what its connect returns beside it; Astra does every write, through the same
/// backup and restore machinery as its own clients.
/// </summary>
public sealed class AgentPluginDescriptor
{
    /// <summary>The plugin's spec.</summary>
    public string Spec { get; set; } = "";

    /// <summary>magpie's agent id (^[a-z0-9][a-z0-9._-]{0,63}$); the client kind is "plugin-" + id.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>The file Astra edits, as the plugin writes it (~ and $VAR not yet expanded).</summary>
    public string Config { get; set; } = "";

    /// <summary>json, yaml or env.</summary>
    public string Format { get; set; } = "";

    /// <summary>The key path of the model in the file (dotted for json and yaml).</summary>
    public string Model { get; set; } = "";

    /// <summary>What the agent wants written before the model id.</summary>
    public string? Prefix { get; set; }

    /// <summary>The agent's command, to detect it.</summary>
    public string? Bin { get; set; }

    /// <summary>The agent's folder, to detect it.</summary>
    public string? Dir { get; set; }

    /// <summary>User-Agent words its requests carry, lower-cased.</summary>
    public List<string> Ua { get; set; } = [];

    /// <summary>An icon name, or an https: / data: URL.</summary>
    public string? Icon { get; set; }

    /// <summary>Advice shown after a change (an agent that reads its config when it starts needs a restart).</summary>
    public string? Notice { get; set; }

    /// <summary>Whether the module has connect; one without writes only the model.</summary>
    public bool HasConnect { get; set; }

    /// <summary>Where the agent reads instructions, MCP servers and skills (magpie's Library; Astra has none yet).</summary>
    public JsonObject? Library { get; set; }
}

/// <summary>The agents the enabled plugins add, as the client layer reads them.</summary>
public interface IAgentPluginCatalog
{
    /// <summary>Moves whenever the agents loaded change.</summary>
    long Generation { get; }

    /// <summary>The agents loaded, in the plugin list's order.</summary>
    IReadOnlyList<AgentPluginDescriptor> List();

    /// <summary>Why an agent plugin didn't load, by plugin spec.</summary>
    IReadOnlyDictionary<string, string> Errors { get; }

    /// <summary>Loads the agents again when the plugin list or a module's file changed (looked at once a second).</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs the agent's connect with <paramref name="input"/> (<c>{gateway:{url,v1,key}, model, models, agent:{id,config}}</c>)
    /// and returns the keys to write beside the model, by key path; null when it has no connect or returns nothing.
    /// Throws <see cref="InvalidOperationException"/> with the reason when connect fails.
    /// </summary>
    JsonObject? Connect(string agentId, JsonObject input);
}
