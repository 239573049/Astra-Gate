using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Config;

/// <summary>Where a client's MCP gateway entry stands (<see cref="ClientMcpApplier.Inspect"/>).</summary>
public enum ClientMcpState
{
    /// <summary>No <c>astra</c> entry in the client's file.</summary>
    Missing,

    /// <summary>The entry is the one Astra wrote.</summary>
    Applied,

    /// <summary>Astra wrote the entry and the client or the user changed it since.</summary>
    Drifted,

    /// <summary>An <c>astra</c> entry exists that Astra did not write; it is never overwritten.</summary>
    Conflict,
}

/// <param name="Url">The entry's address, when it could be read.</param>
/// <param name="Authorization">The entry's <c>Authorization</c> header value (a secret: never returned by the API).</param>
/// <param name="Error">Why the file could not be read (a file the client cannot parse either); the state is then <see cref="ClientMcpState.Conflict"/>.</param>
public sealed record ClientMcpStatus(ClientMcpState State, string File, string? Url, string? Authorization, string? Error = null);

/// <summary>A refusal that is the user's to settle (a drifted or foreign entry, or a client that rewrote the file under us).</summary>
public sealed class ClientMcpException(string message, ClientMcpState state) : Exception(message)
{
    public ClientMcpState State { get; } = state;
}

/// <summary>
/// Writes the MCP gateway's single <c>/mcp</c> entry into a client (issue #34, <c>.plan/client-mcp-apply.md</c>): Claude Code's
/// user-level <c>mcpServers.astra</c> in <c>.claude.json</c> and Codex's <c>[mcp_servers.astra]</c> table in <c>config.toml</c>. It is a
/// separate class from the client adapters, so the adapters and their shared golden cases stay as they are, but the writes go
/// through <see cref="ClientConfigApplier"/>: first-write and rolling backups, per-key original/applied state and a restore that
/// leaves values the user changed alone. The state is kept under its own client kind (<c>&lt;kind&gt;-mcp</c>), independent of the
/// client's gateway take-over: disabling the take-over does not touch the entry, and <c>restore-all</c> removes it.
/// </summary>
public sealed class ClientMcpApplier(ClientEnvironment env, IClientConfigStateStore store, ClientConfigApplier applier)
{
    /// <summary>The name the entry has in the client's MCP server list.</summary>
    public const string EntryName = "astra";

    public static bool Supports(string kind) => kind is ClientKinds.ClaudeCode or ClientKinds.Codex;

    public static IReadOnlyList<string> SupportedKinds { get; } = [ClientKinds.ClaudeCode, ClientKinds.Codex];

    /// <summary>The client kind the entry's backups and state are filed under. No colon: it names a backup directory.</summary>
    public static string StateKind(string kind) => kind + "-mcp";

    private ConfigFileFormat Format(string kind) => kind == ClientKinds.Codex ? ConfigFileFormat.Toml : ConfigFileFormat.Json;

    private ConfigChangeKind Kind(string kind) => kind == ClientKinds.Codex ? ConfigChangeKind.Table : ConfigChangeKind.Key;

    private string KeyPath(string kind) => kind == ClientKinds.Codex ? $"mcp_servers.{EntryName}" : $"mcpServers.{EntryName}";

    /// <summary>
    /// The file holding the entry. Claude Code keeps its personal MCP servers in <c>.claude.json</c>, under CLAUDE_CONFIG_DIR when that is
    /// set and in the home directory otherwise; Codex uses <c>config.toml</c> of its own directory (CODEX_HOME).
    /// </summary>
    public string File(string kind)
    {
        RequireSupported(kind);
        if (kind == ClientKinds.Codex)
            return Path.Combine(env.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex ? codex : env.Combine(".codex"), "config.toml");
        return env.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claude
            ? Path.Combine(claude, ".claude.json")
            : env.Combine(".claude.json");
    }

    // ---------------------------------------------------------------- status

    public ClientMcpStatus Inspect(string kind)
    {
        var file = File(kind);
        try
        {
            var ops = ConfigEditorOps.Create(Format(kind), env.ReadTextOrNull(file) ?? "");
            string? Read(string path, ConfigChangeKind k = ConfigChangeKind.Key) => ops.Read(new ConfigChange(file, Format(kind), path, null, null, k));
            var current = Read(KeyPath(kind), Kind(kind));
            var url = DecodeString(Read(KeyPath(kind) + ".url"));
            var authorization = DecodeString(Read(kind == ClientKinds.Codex ? KeyPath(kind) + ".http_headers.Authorization" : KeyPath(kind) + ".headers.Authorization"));
            if (current is null) return new ClientMcpStatus(ClientMcpState.Missing, file, null, null);
            var state = store.Get(StateKind(kind), file, KeyPath(kind));
            if (state is null) return new ClientMcpStatus(ClientMcpState.Conflict, file, url, authorization);
            // A Codex table reads as just "present", so its address (against what Astra noted it wrote) stands in for the content;
            // Claude's entry is compared whole.
            var same = kind == ClientKinds.Codex
                ? url is not null && url == AppliedUrl(store.Get(StateKind(kind), file, NoteKey(kind))?.AppliedValueJson)
                : ConfigValueCodec.EqualsValue(ConfigFileFormat.Json, current, state.AppliedValueJson);
            return new ClientMcpStatus(same ? ClientMcpState.Applied : ClientMcpState.Drifted, file, url, authorization);
        }
        catch (EditorException e)
        {
            return new ClientMcpStatus(ClientMcpState.Conflict, file, null, null, e.Message);
        }
    }

    // ---------------------------------------------------------------- apply / remove

    /// <summary>
    /// Writes the entry. A foreign entry is never overwritten; a drifted one only with <paramref name="force"/>. An entry Astra wrote
    /// is replaced (a new token, a new gateway address): it is removed through its recorded original first, then written again.
    /// </summary>
    public ApplyReport Apply(string kind, string gatewayUrl, string token, bool force)
    {
        var before = Inspect(kind);
        if (before.Error is not null) throw new ClientMcpException($"The client's file cannot be read: {before.Error}", ClientMcpState.Conflict);
        switch (before.State)
        {
            case ClientMcpState.Conflict:
                throw new ClientMcpException($"{before.File} already has an \"{EntryName}\" MCP entry that Astra did not write. Rename or remove it first.", ClientMcpState.Conflict);
            case ClientMcpState.Drifted when !force:
                throw new ClientMcpException($"The \"{EntryName}\" MCP entry in {before.File} was changed since Astra wrote it.", ClientMcpState.Drifted);
            case ClientMcpState.Drifted or ClientMcpState.Applied:
                var restored = applier.ForceRestore(RemovalPlan(kind));
                if (restored.Keys.Any(k => k.Outcome == RestoreKeyOutcome.Drifted))
                    throw new ClientMcpException("The old entry could not be removed.", ClientMcpState.Drifted);
                break;
        }

        var file = File(kind);
        var plan = applier.Preview(StateKind(kind), [new ConfigChange(file, Format(kind), KeyPath(kind), null, Entry(kind, gatewayUrl, token), Kind(kind))]);
        var report = applier.Apply(plan);
        if (kind == ClientKinds.Codex)
            store.Upsert(new ClientConfigStateEntry
            {
                ClientKind = StateKind(kind), FilePath = file, KeyPath = NoteKey(kind), OriginalAbsent = true,
                AppliedValueJson = plan.Changes[0].After, AppliedAt = DateTimeOffset.UtcNow,
            });
        // The client rewrites its own files; if it already did, say so rather than report a write that is gone.
        if (Inspect(kind).State != ClientMcpState.Applied)
            throw new ClientMcpException("The client changed its file while Astra was writing it. Try again.", ClientMcpState.Drifted);
        return report;
    }

    /// <summary>Takes the entry out again, back to what the file was before (nothing left behind when Astra created the file or the table).</summary>
    public RestoreReport Remove(string kind, bool force)
    {
        RequireSupported(kind);
        var plan = RemovalPlan(kind);
        var report = force ? applier.ForceRestore(plan) : applier.Disable(plan);
        // the note goes with the entry, but stays while a changed entry is left in place
        if (store.Get(StateKind(kind), File(kind), KeyPath(kind)) is null) store.Delete(StateKind(kind), File(kind), NoteKey(kind));
        return report;
    }

    /// <summary>
    /// A state row that no plan names, so no restore touches it: it keeps the table's content as Astra wrote it, which the table's own
    /// row cannot (a TOML table is tracked as present or absent only).
    /// </summary>
    private string NoteKey(string kind) => KeyPath(kind) + "#written";

    /// <summary>The offline <c>restore-all</c> step: removes every client's entry Astra wrote.</summary>
    public IReadOnlyList<(string Kind, RestoreReport Report)> RemoveAll(bool force = false) =>
        SupportedKinds.Select(k => (k, Remove(k, force))).ToList();

    private ConfigChangePlan RemovalPlan(string kind)
    {
        var file = File(kind);
        var ops = ConfigEditorOps.Create(Format(kind), env.ReadTextOrNull(file) ?? "");
        var current = ops.Read(new ConfigChange(file, Format(kind), KeyPath(kind), null, null, Kind(kind)));
        return new ConfigChangePlan(StateKind(kind), [new ConfigChange(file, Format(kind), KeyPath(kind), current, null, Kind(kind))], []);
    }

    // ---------------------------------------------------------------- entry text

    /// <summary>The value written, as a plan carries it: JSON text for a JSON file, a JSON object of raw TOML values for a table.</summary>
    private static string Entry(string kind, string gatewayUrl, string token)
    {
        var url = gatewayUrl.TrimEnd('/') + "/mcp";
        var bearer = "Bearer " + token;
        if (kind == ClientKinds.ClaudeCode)
            return new JsonObject { ["type"] = "http", ["url"] = url, ["headers"] = new JsonObject { ["Authorization"] = bearer } }.ToJsonString();
        // Codex documents http_headers as a map of static header values (and bearer_token_env_var, which needs a variable Astra cannot set).
        return new JsonObject
        {
            ["url"] = TomlValueText.ForString(url),
            ["http_headers"] = "{ Authorization = " + TomlValueText.ForString(bearer) + " }",
        }.ToJsonString();
    }

    /// <summary>The address in a stored applied value (a table's items, raw TOML source).</summary>
    private static string? AppliedUrl(string? appliedJson)
    {
        if (appliedJson is null) return null;
        try
        {
            return JsonNode.Parse(appliedJson) is JsonObject items && items["url"] is JsonValue v && v.TryGetValue<string>(out var raw) ? Unquote(raw) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A key read from JSON is JSON text (<c>"x"</c>); from TOML it is a JSON-encoded raw source (<c>"\"x\""</c>) — both end up as the plain text.</summary>
    private static string? DecodeString(string? read)
    {
        if (read is null) return null;
        var text = Json.DecodeString(read);
        return text is null ? null : Unquote(text);
    }

    private static string Unquote(string raw) => raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

    private static void RequireSupported(string kind)
    {
        if (!Supports(kind)) throw new ArgumentException($"{kind} has no MCP gateway entry", nameof(kind));
    }
}
