using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Config;

/// <summary>
/// Writes a user-defined MCP server (a local stdio command or a remote address) into the MCP list of Claude Code, Codex, Gemini CLI,
/// Qwen Code and OpenCode (<see cref="McpClientTargets"/>, issue #34). Astra only records the definition and writes it into each chosen client's own
/// file — the client starts the process; Astra never runs it. The sibling of <see cref="ClientMcpApplier"/> (which writes the one
/// <c>astra</c> gateway entry) with the same rules: writes go through <see cref="ClientConfigApplier"/> (first-write and rolling
/// backups, per-key state), a foreign entry of the same name is never overwritten, a drifted one only with force. State is filed
/// under <c>&lt;kind&gt;-mcpx</c>, apart from the take-over and from the gateway entry.
/// </summary>
public sealed class ClientMcpServerApplier(ClientEnvironment env, IClientConfigStateStore store, ClientConfigApplier applier)
{
    public static bool Supports(string kind) => McpClientTargets.Supports(kind);

    public static IReadOnlyList<string> SupportedKinds => McpClientTargets.Kinds;

    public static string StateKind(string kind) => kind + "-mcpx";

    private static ConfigFileFormat Format(string kind) => McpClientTargets.Format(kind);

    private static ConfigChangeKind Kind(string kind) => McpClientTargets.ChangeKind(kind);

    private static string KeyPath(string kind, string name) => McpClientTargets.Container(kind) + "." + name;

    private static string NoteKey(string kind, string name) => KeyPath(kind, name) + "#written";

    /// <summary>The file a server is written into: the client's own, or its file inside <paramref name="project"/> (when the client has one).</summary>
    public string File(string kind, string? project = null) => McpClientTargets.File(env, kind, project);

    public ClientMcpStatus Inspect(string kind, string name, string? project = null)
    {
        var file = File(kind, project);
        try
        {
            var ops = ConfigEditorOps.Create(Format(kind), env.ReadTextOrNull(file) ?? "");
            var path = KeyPath(kind, name);
            var current = ops.Read(new ConfigChange(file, Format(kind), path, null, null, Kind(kind)));
            if (current is null) return new ClientMcpStatus(ClientMcpState.Missing, file, null, null);
            var state = store.Get(StateKind(kind), file, path);
            if (state is null) return new ClientMcpStatus(ClientMcpState.Conflict, file, null, null);
            // A Codex table reads as just "present": its command or address (against what Astra noted it wrote) stands in for the content.
            bool same;
            if (kind == ClientKinds.Codex)
            {
                var (key, written) = Identity(store.Get(StateKind(kind), file, NoteKey(kind, name))?.AppliedValueJson);
                same = key is not null && written is not null
                    && ops.Read(new ConfigChange(file, Format(kind), path + "." + key, null, null)) is { } c && Json.DecodeString(c) == Json.DecodeString(written);
            }
            else same = ConfigValueCodec.EqualsValue(ConfigFileFormat.Json, current, state.AppliedValueJson);
            return new ClientMcpStatus(same ? ClientMcpState.Applied : ClientMcpState.Drifted, file, null, null);
        }
        catch (EditorException e)
        {
            return new ClientMcpStatus(ClientMcpState.Conflict, file, null, null, e.Message);
        }
    }

    public ApplyReport Apply(string kind, LocalMcpSpec spec, bool force, string? project = null)
    {
        RequireSupported(kind);
        var before = Inspect(kind, spec.Name, project);
        if (before.Error is not null) throw new ClientMcpException($"The client's file cannot be read: {before.Error}", ClientMcpState.Conflict);
        switch (before.State)
        {
            case ClientMcpState.Conflict:
                throw new ClientMcpException($"{before.File} already has an MCP entry \"{spec.Name}\" that Astra did not write. Rename or remove it first.", ClientMcpState.Conflict);
            case ClientMcpState.Drifted when !force:
                throw new ClientMcpException($"The MCP entry \"{spec.Name}\" in {before.File} was changed since Astra wrote it.", ClientMcpState.Drifted);
            case ClientMcpState.Drifted or ClientMcpState.Applied:
                var restored = applier.ForceRestore(RemovalPlan(kind, spec.Name, project));
                if (restored.Keys.Any(k => k.Outcome == RestoreKeyOutcome.Drifted))
                    throw new ClientMcpException("The old entry could not be removed.", ClientMcpState.Drifted);
                break;
        }

        var file = File(kind, project);
        var plan = applier.Preview(StateKind(kind), [new ConfigChange(file, Format(kind), KeyPath(kind, spec.Name), null, Entry(kind, spec), Kind(kind))]);
        var report = applier.Apply(plan);
        if (kind == ClientKinds.Codex)
            store.Upsert(new ClientConfigStateEntry
            {
                ClientKind = StateKind(kind), FilePath = file, KeyPath = NoteKey(kind, spec.Name), OriginalAbsent = true,
                AppliedValueJson = plan.Changes[0].After, AppliedAt = DateTimeOffset.UtcNow,
            });
        if (Inspect(kind, spec.Name, project).State != ClientMcpState.Applied)
            throw new ClientMcpException("The client changed its file while Astra was writing it. Try again.", ClientMcpState.Drifted);
        return report;
    }

    public RestoreReport Remove(string kind, string name, bool force, string? project = null) => RemoveFrom(kind, name, force, File(kind, project));

    /// <summary>Removes the entry Astra wrote into one specific file (a client's own or a project's).</summary>
    public RestoreReport RemoveAt(string kind, string name, bool force, string file) => RemoveFrom(kind, name, force, file);

    /// <summary>Forgets what Astra recorded for an entry in a file that no longer exists (a deleted project): nothing is left to restore.</summary>
    public void Forget(string kind, string name, string file)
    {
        store.Delete(StateKind(kind), file, KeyPath(kind, name));
        store.Delete(StateKind(kind), file, NoteKey(kind, name));
    }

    /// <summary>Every file Astra wrote an entry named <paramref name="name"/> into for this client (the user-wide one and any project's).</summary>
    public IReadOnlyList<string> WrittenFiles(string kind, string name)
    {
        var keyPath = KeyPath(kind, name);
        return store.List(StateKind(kind)).Where(e => e.KeyPath == keyPath).Select(e => e.FilePath).Distinct(StringComparer.Ordinal).ToList();
    }

    private RestoreReport RemoveFrom(string kind, string name, bool force, string file)
    {
        RequireSupported(kind);
        var plan = RemovalPlanFor(kind, name, file);
        var report = force ? applier.ForceRestore(plan) : applier.Disable(plan);
        if (store.Get(StateKind(kind), file, KeyPath(kind, name)) is null) store.Delete(StateKind(kind), file, NoteKey(kind, name));
        return report;
    }

    /// <summary>The names of the entries Astra wrote into a client's own (user-wide) file, or into its file inside <paramref name="project"/> (from its state).</summary>
    public IReadOnlyList<string> WrittenNames(string kind, string? project = null)
    {
        var file = File(kind, project);
        var prefix = KeyPath(kind, "");
        return store.List(StateKind(kind))
            .Where(e => e.FilePath == file && e.KeyPath.StartsWith(prefix, StringComparison.Ordinal) && !e.KeyPath.EndsWith("#written", StringComparison.Ordinal))
            .Select(e => e.KeyPath[prefix.Length..]).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The offline <c>restore-all</c> step: removes every server entry Astra wrote, user-wide and in any project file.</summary>
    public IReadOnlyList<(string Kind, RestoreReport Report)> RemoveAll(bool force = false)
    {
        var result = new List<(string, RestoreReport)>();
        foreach (var kind in SupportedKinds)
        {
            var prefix = KeyPath(kind, "");
            var entries = store.List(StateKind(kind))
                .Where(e => e.KeyPath.StartsWith(prefix, StringComparison.Ordinal) && !e.KeyPath.EndsWith("#written", StringComparison.Ordinal))
                .Select(e => (e.FilePath, Name: e.KeyPath[prefix.Length..])).Distinct().ToList();
            foreach (var (file, name) in entries)
            {
                // a project that was deleted has no file to restore; only the record is forgotten
                if (!env.FileExists(file))
                {
                    store.Delete(StateKind(kind), file, KeyPath(kind, name));
                    store.Delete(StateKind(kind), file, NoteKey(kind, name));
                    continue;
                }
                result.Add((kind, RemoveFrom(kind, name, force, file)));
            }
        }
        return result;
    }

    private ConfigChangePlan RemovalPlan(string kind, string name, string? project = null) => RemovalPlanFor(kind, name, File(kind, project));

    private ConfigChangePlan RemovalPlanFor(string kind, string name, string file)
    {
        var ops = ConfigEditorOps.Create(Format(kind), env.ReadTextOrNull(file) ?? "");
        var path = KeyPath(kind, name);
        var current = ops.Read(new ConfigChange(file, Format(kind), path, null, null, Kind(kind)));
        return new ConfigChangePlan(StateKind(kind), [new ConfigChange(file, Format(kind), path, current, null, Kind(kind))], []);
    }

    private static string Entry(string kind, LocalMcpSpec spec) => McpClientTargets.Entry(kind, spec);

    /// <summary>The key (<c>url</c> or <c>command</c>) and raw TOML value that identify a Codex table Astra wrote.</summary>
    private static (string? Key, string? Raw) Identity(string? appliedJson)
    {
        if (appliedJson is null) return (null, null);
        try
        {
            if (JsonNode.Parse(appliedJson) is not JsonObject o) return (null, null);
            var key = o.ContainsKey("url") ? "url" : "command";
            return (key, o[key] is JsonValue v && v.TryGetValue<string>(out var raw) ? raw : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Takes over an entry the user already had (import): the entry is removed and written again as Astra's own, so it is tracked from
    /// then on. Its original is the user's entry, so taking it out of Astra later (or <c>restore-all</c>) puts that entry back; the
    /// file is backed up first like any write.
    /// </summary>
    public ApplyReport Adopt(string kind, LocalMcpSpec spec, string? project = null)
    {
        RequireSupported(kind);
        var before = Inspect(kind, spec.Name, project);
        if (before.Error is not null) throw new ClientMcpException($"The client's file cannot be read: {before.Error}", ClientMcpState.Conflict);
        if (before.State != ClientMcpState.Conflict) throw new ClientMcpException($"There is no entry \"{spec.Name}\" of the user's to take over in {before.File}.", before.State);
        applier.Apply(RemovalPlan(kind, spec.Name, project));
        var file = File(kind, project);
        var plan = applier.Preview(StateKind(kind), [new ConfigChange(file, Format(kind), KeyPath(kind, spec.Name), null, Entry(kind, spec), Kind(kind))]);
        var report = applier.Apply(plan);
        if (kind == ClientKinds.Codex)
            store.Upsert(new ClientConfigStateEntry
            {
                ClientKind = StateKind(kind), FilePath = file, KeyPath = NoteKey(kind, spec.Name), OriginalAbsent = true,
                AppliedValueJson = plan.Changes[0].After, AppliedAt = DateTimeOffset.UtcNow,
            });
        return report;
    }

    /// <summary>The entries in a client's file that Astra did not write (the candidates for an import); the gateway's own <c>astra</c> entry is left out.</summary>
    public IReadOnlyList<FoundMcpEntry> Unmanaged(string kind)
    {
        RequireSupported(kind);
        var written = WrittenNames(kind).ToHashSet(StringComparer.Ordinal);
        return McpClientTargets.ReadEntries(env, kind).Where(e => e.Name != "astra" && !written.Contains(e.Name)).ToList();
    }

    private static void RequireSupported(string kind)
    {
        if (!Supports(kind)) throw new ArgumentException($"{kind} has no MCP server list Astra writes", nameof(kind));
    }
}
