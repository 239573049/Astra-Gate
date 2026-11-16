using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>Why the resource library refused something; the server maps it to an HTTP status.</summary>
public enum ResourceFailure
{
    Invalid,
    NotFound,
    Conflict,
}

public sealed class ResourceLibraryException(string message, ResourceFailure failure) : Exception(message)
{
    public ResourceFailure Failure { get; } = failure;
}

/// <summary>What the library wants for one item, as the caller states it.</summary>
public sealed record ResourceInput(
    string Kind, string Name, string Description, string Body, string? Event, string? Matcher, int Timeout, string? Model,
    IReadOnlyList<LibraryTarget> Targets, bool Enabled);

/// <summary>
/// The state of one item on one target. <see cref="State"/>: <c>applied</c>, <c>drifted</c> (changed since Astra wrote it),
/// <c>missing</c> (removed since), <c>conflict</c> (a file or entry of the user's is in the way), <c>project-missing</c>,
/// <c>unsupported</c> (the client takes no item of this kind, or not in a project, or not with this setting), <c>pending</c> (wanted, not yet written)
/// or <c>off</c> (the item is switched off).
/// </summary>
public sealed record ResourceTargetState(string Client, string Project, string State, string? Path, string? Message);

public sealed record ResourceItemInfo(LibraryItemRecord Item, IReadOnlyList<ResourceTargetState> Targets);

/// <summary>One client as the resource pages see it.</summary>
public sealed record ResourceClientInfo(string Kind, bool Detected, string? Path, bool Project);

public sealed record ResourceProjectInfo(string Path, string Name, bool Exists);

public sealed record ResourceState(
    string Kind, IReadOnlyList<ResourceClientInfo> Clients, IReadOnlyList<ResourceItemInfo> Items, IReadOnlyList<ResourceProjectInfo> Projects);

/// <summary>
/// The rest of the resource library (<c>.plan/resources-library.md</c>; the model and the "take back only what you wrote" discipline are
/// magpie's <c>library</c> package, MIT): instructions as an Astra-owned marked section of the client's instruction file, sub-agents and
/// slash commands as files of the client's own folder, hooks as entries of its hook arrays and environment variables as keys of its
/// settings — each user-wide or inside a project directory. <see cref="ILibraryResourceLedger"/> records everything written; Astra only
/// ever removes what the ledger lists and that is still exactly as written. Whatever the user changed since is theirs (drift): it is
/// reported, never overwritten without <c>force</c>, and kept when the item is removed. Existing files are copied to
/// <c>~/.astra/backups/library/</c> before Astra changes them; hooks and variables are written through <see cref="ClientConfigApplier"/>.
/// </summary>
public sealed partial class ResourceLibrary(
    ClientEnvironment env, AstraPaths paths, ILibraryResourceLedger ledger, IClientConfigStateStore state, ClientConfigApplier applier)
{
    private const int KeepBackups = 30;
    private static readonly string[] ReservedAgents = ["general-purpose", "explore", "plan", "default", "build", "task", "astra"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _backupRoot;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex InstructionName();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,48}[a-z0-9])?$")]
    private static partial Regex AgentName();

    [GeneratedRegex(@"^[a-z0-9]([a-z0-9_-]{0,62}[a-z0-9])?$")]
    private static partial Regex CommandName();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex HookName();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvName();

    [GeneratedRegex(@"^[A-Za-z]{2,40}$")]
    private static partial Regex OwnEvent();

    private async Task<T> LockedAsync<T>(Func<Task<T>> run, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await run();
        }
        finally
        {
            _backupRoot = null;
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- validation

    /// <summary>Checks an input and returns why it is not acceptable, or null.</summary>
    public static string? Validate(ResourceInput i)
    {
        if (!ResourceKinds.All.Contains(i.Kind)) return $"'{i.Kind}' is not a resource kind";
        var name = i.Name;
        switch (i.Kind)
        {
            case ResourceKinds.Instruction:
                if (!InstructionName().IsMatch(name)) return "An instruction's name is 1-48 characters of a-z, 0-9, _ or -";
                if (string.IsNullOrWhiteSpace(i.Body)) return "An instruction needs a text";
                if (i.Body.Contains("astra:begin", StringComparison.Ordinal) || i.Body.Contains("astra:end", StringComparison.Ordinal))
                    return "The text may not contain Astra's own section markers";
                if (i.Body.Length > 64 * 1024) return "The text is too long";
                break;
            case ResourceKinds.Agent:
                if (!AgentName().IsMatch(name) || name.Length < 2) return "A sub-agent's name is 2-50 characters of a-z, 0-9 or -, not starting or ending with -";
                if (ReservedAgents.Contains(name)) return $"'{name}' is a name clients keep for themselves";
                if (string.IsNullOrWhiteSpace(i.Description)) return "A sub-agent needs a description";
                if (i.Description.Contains('\n')) return "The description is one line";
                if (string.IsNullOrWhiteSpace(i.Body)) return "A sub-agent needs a prompt";
                if (i.Body.Length > 64 * 1024) return "The prompt is too long";
                break;
            case ResourceKinds.Command:
                if (!CommandName().IsMatch(name)) return "A command's name is 1-64 characters of a-z, 0-9, _ or -, not starting or ending with - or _";
                if (i.Description.Contains('\n') || i.Description.Length > 200) return "The description is one line of at most 200 characters";
                if (string.IsNullOrWhiteSpace(i.Body)) return "A command needs a prompt";
                if (i.Body.Length > 64 * 1024) return "The prompt is too long";
                break;
            case ResourceKinds.Hook:
                if (!HookName().IsMatch(name)) return "A hook's name is 1-64 characters of letters, digits, _ or -";
                if (string.IsNullOrWhiteSpace(i.Body) || i.Body.Contains('\n') || i.Body.Contains('\0')) return "A hook needs a one-line command";
                if (i.Description.Contains('\n') || i.Description.Length > 200) return "The description is one line of at most 200 characters";
                if (i.Timeout is < 0 or > 86_400) return "The timeout is 0 to 86400 seconds";
                if (EventProblem(i.Event, i.Matcher) is { } why) return why;
                break;
            case ResourceKinds.Env:
                if (!EnvName().IsMatch(name)) return "A variable's name is letters, digits and _, not starting with a digit";
                if (i.Body.Contains('\0') || i.Body.Length > 8192) return "The value cannot be written into a client's settings";
                break;
        }
        foreach (var t in i.Targets)
        {
            if (!ResourcePlaces.KindsFor(i.Kind).Contains(t.Client)) return $"{t.Client} takes no {i.Kind} from Astra";
            if (t.Project.Length > 0)
            {
                if (!ResourcePlaces.SupportsProject(i.Kind)) return $"A {i.Kind} cannot be placed into a project";
                if (!Path.IsPathRooted(t.Project)) return "A project is a full directory path";
            }
        }
        return null;
    }

    private static string? EventProblem(string? ev, string? matcher)
    {
        if (string.IsNullOrEmpty(ev)) return "A hook needs an event";
        if (ev.Contains(':'))
        {
            var (client, own) = (ev[..ev.IndexOf(':')], ev[(ev.IndexOf(':') + 1)..]);
            if (!ResourcePlaces.HookKinds.Contains(client) || !OwnEvent().IsMatch(own)) return $"'{ev}' is not an event of a client Astra writes hooks for";
            return null;
        }
        if (!ResourcePlaces.HookEvents.Contains(ev)) return $"'{ev}' is not a hook event";
        if (string.IsNullOrEmpty(matcher)) return null;
        if (ev is "after-tool" or "before-tool") return ResourcePlaces.HookTools.Contains(matcher) ? null : "A tool hook is narrowed to shell or edit";
        if (ev == "session-start")
        {
            var parts = matcher.Split('|');
            string[] starts = ["startup", "resume", "clear", "compact", "fork"];
            return parts.All(starts.Contains) && parts.Distinct().Count() == parts.Length ? null : "A session start is narrowed to some of startup, resume, clear, compact, fork";
        }
        return $"'{ev}' takes no matcher";
    }

    // ---------------------------------------------------------------- state

    public Task<ResourceState> StateAsync(string kind, CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (!ResourceKinds.All.Contains(kind)) throw new ResourceLibraryException($"'{kind}' is not a resource kind", ResourceFailure.Invalid);
        var items = await ledger.ListItemsAsync(kind, ct);
        var writes = await ledger.ListWritesAsync(null, ct);
        var infos = new List<ResourceItemInfo>();
        foreach (var item in items) infos.Add(new ResourceItemInfo(item, await TargetStatesAsync(item, writes.Where(w => w.ItemId == item.Id).ToList(), ct)));
        var clients = ResourcePlaces.KindsFor(kind)
            .Select(k => new ResourceClientInfo(k, ResourcePlaces.Detected(env, k, kind), PlaceOf(kind, k), ResourcePlaces.SupportsProject(kind))).ToList();
        var projects = (await ledger.ListProjectsAsync(ct)).Select(p => new ResourceProjectInfo(p.Path, p.Name, Directory.Exists(p.Path))).ToList();
        return new ResourceState(kind, clients, infos, projects);
    }, ct);

    private string? PlaceOf(string kind, string client) => kind switch
    {
        ResourceKinds.Instruction => ResourcePlaces.InstructionFile(env, client),
        ResourceKinds.Agent => ResourcePlaces.AgentPlace(env, client, null)?.Dir,
        ResourceKinds.Command => ResourcePlaces.CommandPlace(env, client, null)?.Dir,
        ResourceKinds.Hook => ResourcePlaces.Hook(env, client)?.File,
        ResourceKinds.Env => ResourcePlaces.EnvSetting(env, client)?.File,
        _ => null,
    };

    private async Task<IReadOnlyList<ResourceTargetState>> TargetStatesAsync(LibraryItemRecord item, IReadOnlyList<LibraryWriteRecord> writes, CancellationToken ct)
    {
        _ = ct;
        var result = new List<ResourceTargetState>();
        var targets = ResourceText.ParseTargets(item.TargetsJson);
        foreach (var t in targets)
        {
            var mine = writes.Where(w => w.Client == t.Client && w.Project == t.Project).ToList();
            if (t.Project.Length > 0 && !Directory.Exists(t.Project)) { result.Add(new(t.Client, t.Project, "project-missing", null, null)); continue; }
            if (Desired(item, t, out var problem).Count == 0 && problem is not null) { result.Add(new(t.Client, t.Project, "unsupported", null, problem)); continue; }
            if (mine.Count == 0) { result.Add(new(t.Client, t.Project, item.Enabled ? "pending" : "off", null, null)); continue; }
            var worst = "applied";
            foreach (var w in mine)
            {
                var held = Inspect(w);
                if (held == "drifted") worst = "drifted";
                else if (held == "missing" && worst == "applied") worst = "missing";
            }
            result.Add(new(t.Client, t.Project, item.Enabled ? worst : "off", mine[0].Path, null));
        }
        // writes of targets the item no longer lists (removed from the item but not yet taken out)
        foreach (var w in writes.Where(w => !targets.Any(t => t.Client == w.Client && t.Project == w.Project)).GroupBy(w => (w.Client, w.Project)))
            result.Add(new(w.Key.Client, w.Key.Project, "pending", w.First().Path, "to be removed"));
        return result;
    }

    // ---------------------------------------------------------------- what an item wants

    private sealed record Want(string Client, string Project, string Path, string Mode, string Locator, string Content, string? Entry);

    private List<Want> Desired(LibraryItemRecord item, LibraryTarget t, out string? problem)
    {
        problem = null;
        var extra = ResourceText.Extra(item.ExtraJson);
        var project = t.Project.Length > 0 ? t.Project : null;
        switch (item.Kind)
        {
            case ResourceKinds.Instruction:
            {
                var file = project is null ? ResourcePlaces.InstructionFile(env, t.Client) : ResourcePlaces.ProjectInstructionFile(project, t.Client);
                if (file is null) { problem = "no instruction file"; return []; }
                return [new(t.Client, t.Project, file, WriteModes.Block, item.Name, ResourceText.NormalizeBody(item.Body), null)];
            }
            case ResourceKinds.Agent:
            {
                var place = ResourcePlaces.AgentPlace(env, t.Client, project);
                if (place is null) { problem = "no agent folder"; return []; }
                return [new(t.Client, t.Project, Path.Combine(place.Dir, item.Name + place.Extension), WriteModes.File, "", RenderAgent(item, place, extra), null)];
            }
            case ResourceKinds.Command:
            {
                var place = ResourcePlaces.CommandPlace(env, t.Client, project);
                if (place is null) { problem = "no command folder"; return []; }
                if (place.Args is "braces" or "noplace" && ResourceText.UsesPositional(item.Body))
                {
                    problem = "takes no words by place ($1, $2)";
                    return [];
                }
                return [new(t.Client, t.Project, Path.Combine(place.Dir, item.Name + place.Extension), WriteModes.File, "", RenderCommand(item, place), null)];
            }
            case ResourceKinds.Hook:
            {
                var place = ResourcePlaces.Hook(env, t.Client);
                if (place is null) { problem = "no hook file"; return []; }
                var ev = ResourceText.ExtraString(extra, "event") ?? "";
                var matcher = ResourceText.ExtraString(extra, "matcher");
                string? nativeEvent;
                if (ev.Contains(':'))
                {
                    if (ev[..ev.IndexOf(':')] != t.Client) { problem = "an event of another client"; return []; }
                    nativeEvent = ev[(ev.IndexOf(':') + 1)..];
                }
                else if (!place.Events.TryGetValue(ev, out nativeEvent)) { problem = "no such event"; return []; }
                string? m = null;
                if (!ev.Contains(':'))
                {
                    if (ev == "session-start") m = matcher is null ? null : StartMatcher(t.Client, matcher, out problem);
                    else if (matcher is not null) m = place.Tools.GetValueOrDefault(matcher);
                    if (problem is not null) return [];
                }
                else m = matcher;
                var timeout = ResourceText.ExtraInt(extra, "timeout");
                if (place.Milliseconds) timeout *= 1000;
                var entry = HookEntry(item.Body, m, timeout);
                return [new(t.Client, "", place.File, WriteModes.Hook, nativeEvent, "", entry)];
            }
            case ResourceKinds.Env:
            {
                var place = ResourcePlaces.EnvSetting(env, t.Client);
                if (place is null) { problem = "no settings"; return []; }
                return [new(t.Client, "", place.File, WriteModes.Config, place.KeyPrefix + item.Name, item.Body, null)];
            }
        }
        problem = "unknown kind";
        return [];
    }

    private static string? StartMatcher(string client, string matcher, out string? problem)
    {
        problem = null;
        string[] all = ["startup", "resume", "clear", "compact", "fork"];
        var set = matcher.Split('|');
        if (client == ClientKinds.GeminiCli)
        {
            string[] gemini = ["startup", "resume", "clear"];
            var inGemini = set.Where(gemini.Contains).ToList();
            if (inGemini.Count == 0) { problem = "it has no session start called " + matcher; return null; }
            if (inGemini.Count == gemini.Length) return null;
            if (inGemini.Count > 1) { problem = "its session start can be narrowed to one of startup, resume, clear"; return null; }
            return inGemini[0];
        }
        return set.Length == all.Length ? null : string.Join('|', all.Where(set.Contains));
    }

    private static string HookEntry(string command, string? matcher, int timeout)
    {
        var inner = new JsonObject { ["type"] = "command", ["command"] = command };
        if (timeout > 0) inner["timeout"] = timeout;
        var entry = new JsonObject();
        if (!string.IsNullOrEmpty(matcher)) entry["matcher"] = matcher;
        entry["hooks"] = new JsonArray(inner);
        return entry.ToJsonString();
    }

    private static string RenderAgent(LibraryItemRecord item, ResourceFilePlace place, JsonObject extra)
    {
        var body = item.Body;
        switch (place.Format)
        {
            case "toml-role":
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("name = ").Append(ResourceText.Quote(item.Name)).Append('\n');
                sb.Append("description = ").Append(ResourceText.Quote(item.Description)).Append('\n');
                if (ResourceText.ExtraString(extra, "model") is { } model) sb.Append("model = ").Append(ResourceText.Quote(model)).Append('\n');
                sb.Append("developer_instructions = ").Append(ResourceText.TomlMultiline(body)).Append('\n');
                return sb.ToString();
            }
            case "md-subagent":
                // named by its file; mode subagent is for the main agent to call
                return ResourceText.FrontMatter([("description", ResourceText.Quote(item.Description)), ("mode", "subagent")], body);
            default:
                return ResourceText.FrontMatter([("name", ResourceText.Quote(item.Name)), ("description", ResourceText.Quote(item.Description))], body);
        }
    }

    private static string RenderCommand(LibraryItemRecord item, ResourceFilePlace place)
    {
        var prompt = ResourceText.Arguments(item.Body, place.Args);
        if (place.Format == "toml-command")
        {
            var sb = new System.Text.StringBuilder();
            if (item.Description.Length > 0) sb.Append("description = ").Append(ResourceText.Quote(item.Description)).Append('\n');
            sb.Append("prompt = ").Append(ResourceText.TomlMultiline(prompt)).Append('\n');
            return sb.ToString();
        }
        return item.Description.Length == 0
            ? prompt.TrimEnd('\n', '\r') + "\n"
            : ResourceText.FrontMatter([("description", ResourceText.Quote(item.Description))], prompt);
    }

    // ---------------------------------------------------------------- inspecting what was written

    /// <summary><c>ours</c> (exactly as written), <c>missing</c> or <c>drifted</c>.</summary>
    private string Inspect(LibraryWriteRecord w)
    {
        try
        {
            switch (w.Mode)
            {
                case WriteModes.File:
                    if (!env.FileExists(w.Path)) return "missing";
                    return ResourceText.Hash(env.ReadTextOrNull(w.Path) ?? "") == w.ContentHash ? "ours" : "drifted";
                case WriteModes.Block:
                {
                    if (env.ReadTextOrNull(w.Path) is not { } text) return "missing";
                    var body = ResourceText.GetBlock(text, w.Locator);
                    if (body is null) return "missing";
                    return ResourceText.Hash(body) == w.ContentHash ? "ours" : "drifted";
                }
                case WriteModes.Hook:
                {
                    var array = ReadHookArray(w.Path, w.Locator);
                    if (array is null) return "missing";
                    var entry = JsonNode.Parse(w.Entry ?? "null");
                    return array.Any(e => JsonNode.DeepEquals(e, entry)) ? "ours" : "missing";
                }
                case WriteModes.Config:
                {
                    var place = ConfigPlace(w.Client);
                    if (place is null) return "missing";
                    var ops = ConfigEditorOps.Create(place.Format, env.ReadTextOrNull(w.Path) ?? "");
                    var current = ops.Read(new ConfigChange(w.Path, place.Format, w.Locator, null, null));
                    if (current is null) return "missing";
                    var recorded = state.Get(w.Client + "-env", w.Path, w.Locator)?.AppliedValueJson;
                    return ConfigValueCodec.EqualsValue(place.Format, current, recorded) ? "ours" : "drifted";
                }
            }
        }
        catch (EditorException)
        {
            return "drifted";
        }
        return "missing";
    }

    private EnvPlace? ConfigPlace(string client) => ResourcePlaces.EnvSetting(env, client);

    private JsonArray? ReadHookArray(string file, string ev)
    {
        var ops = ConfigEditorOps.Create(ConfigFileFormat.Json, env.ReadTextOrNull(file) ?? "");
        var raw = ops.Read(new ConfigChange(file, ConfigFileFormat.Json, "hooks." + ev, null, null));
        return raw is null ? null : JsonNode.Parse(raw) as JsonArray;
    }

    // ---------------------------------------------------------------- save / sync / delete

    /// <summary>Creates or updates an item and writes it into its targets. <paramref name="force"/> overwrites what the user changed.</summary>
    public Task<ResourceItemInfo> SaveAsync(string? id, ResourceInput input, bool force, CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (Validate(input) is { } why) throw new ResourceLibraryException(why, ResourceFailure.Invalid);
        var now = DateTimeOffset.UtcNow;
        LibraryItemRecord item;
        if (id is null)
        {
            if (await ledger.GetItemByNameAsync(input.Kind, input.Name, ct) is not null)
                throw new ResourceLibraryException($"There is already a {input.Kind} named '{input.Name}'", ResourceFailure.Conflict);
            item = new LibraryItemRecord { Id = Ulid.NewUlid(), Kind = input.Kind, CreatedAt = now };
        }
        else
        {
            item = await ledger.GetItemAsync(id, ct) ?? throw new ResourceLibraryException("No such item", ResourceFailure.NotFound);
            if (item.Kind != input.Kind) throw new ResourceLibraryException("An item's kind cannot change", ResourceFailure.Invalid);
            if (item.Name != input.Name)
            {
                if (await ledger.GetItemByNameAsync(input.Kind, input.Name, ct) is not null)
                    throw new ResourceLibraryException($"There is already a {input.Kind} named '{input.Name}'", ResourceFailure.Conflict);
                // a rename: take the old files out first (their paths carry the name)
                await RemoveWritesAsync(item, await ledger.ListWritesAsync(item.Id, ct), force, ct);
            }
        }
        item.Name = input.Name;
        item.Description = input.Description;
        item.Body = input.Body;
        item.Enabled = input.Enabled;
        item.UpdatedAt = now;
        item.TargetsJson = ResourceText.FormatTargets(input.Targets);
        var extra = new JsonObject();
        if (input.Event is { Length: > 0 }) extra["event"] = input.Event;
        if (input.Matcher is { Length: > 0 }) extra["matcher"] = input.Matcher;
        if (input.Timeout > 0) extra["timeout"] = input.Timeout;
        if (input.Model is { Length: > 0 }) extra["model"] = input.Model;
        item.ExtraJson = extra.ToJsonString();
        await ledger.UpsertItemAsync(item, ct);
        await SyncCoreAsync(item, force, ct);
        return (await InfoAsync(item.Id, ct))!;
    }, ct);

    /// <summary>Writes the item into its targets again (after the user settled a drift or a conflict).</summary>
    public Task<ResourceItemInfo> SyncAsync(string id, bool force, CancellationToken ct = default) => LockedAsync(async () =>
    {
        var item = await ledger.GetItemAsync(id, ct) ?? throw new ResourceLibraryException("No such item", ResourceFailure.NotFound);
        await SyncCoreAsync(item, force, ct);
        return (await InfoAsync(id, ct))!;
    }, ct);

    /// <summary>Takes the item out of every client (what the user changed stays) and deletes it; <paramref name="force"/> removes changed content too.</summary>
    public Task DeleteAsync(string id, bool force, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        var item = await ledger.GetItemAsync(id, ct) ?? throw new ResourceLibraryException("No such item", ResourceFailure.NotFound);
        await RemoveWritesAsync(item, await ledger.ListWritesAsync(id, ct), force, ct);
        await ledger.DeleteItemAsync(id, ct);
        return true;
    }, ct);

    private async Task<ResourceItemInfo?> InfoAsync(string id, CancellationToken ct)
    {
        var item = await ledger.GetItemAsync(id, ct);
        if (item is null) return null;
        return new ResourceItemInfo(item, await TargetStatesAsync(item, await ledger.ListWritesAsync(id, ct), ct));
    }

    private async Task SyncCoreAsync(LibraryItemRecord item, bool force, CancellationToken ct)
    {
        var writes = (await ledger.ListWritesAsync(item.Id, ct)).ToList();
        var targets = ResourceText.ParseTargets(item.TargetsJson);
        var wanted = new List<Want>();
        if (item.Enabled)
            foreach (var t in targets)
            {
                if (t.Project.Length > 0 && !Directory.Exists(t.Project)) continue;   // reported by the state, never a failure
                wanted.AddRange(Desired(item, t, out _));
            }
        // take out what is no longer wanted
        var stale = writes.Where(w => !wanted.Any(x => Same(x, w))).ToList();
        await RemoveWritesAsync(item, stale, force, ct);
        foreach (var w in wanted)
        {
            try
            {
                await WriteAsync(item, w, writes.FirstOrDefault(r => Same(w, r)), force, ct);
            }
            catch (Exception e) when (e is ResourceLibraryException or IOException or UnauthorizedAccessException or EditorException)
            {
                // one target failing must not stop the others; the state view shows what is missing
                _lastErrors[(item.Id, w.Client, w.Project)] = e.Message;
            }
        }
    }

    private readonly Dictionary<(string Item, string Client, string Project), string> _lastErrors = [];

    /// <summary>Why the last write of an item into a target failed (kept in memory only), or null.</summary>
    public string? LastError(string itemId, string client, string project) => _lastErrors.GetValueOrDefault((itemId, client, project));

    private static bool Same(Want w, LibraryWriteRecord r) => w.Client == r.Client && w.Project == r.Project && w.Path == r.Path && w.Locator == r.Locator && w.Mode == r.Mode;

    // ---------------------------------------------------------------- writing

    private async Task WriteAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, bool force, CancellationToken ct)
    {
        _lastErrors.Remove((item.Id, w.Client, w.Project));
        switch (w.Mode)
        {
            case WriteModes.File: await WriteFileAsync(item, w, have, force, ct); break;
            case WriteModes.Block: await WriteBlockAsync(item, w, have, force, ct); break;
            case WriteModes.Hook: await WriteHookAsync(item, w, have, ct); break;
            case WriteModes.Config: await WriteConfigAsync(item, w, have, force, ct); break;
        }
    }

    private LibraryWriteRecord NewWrite(LibraryItemRecord item, Want w, string hash) => new()
    {
        Id = Ulid.NewUlid(), ItemId = item.Id, Client = w.Client, Project = w.Project, Path = w.Path, Mode = w.Mode, Locator = w.Locator,
        ContentHash = hash, Entry = w.Entry, WrittenAt = DateTimeOffset.UtcNow,
    };

    private async Task WriteFileAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, bool force, CancellationToken ct)
    {
        var contentHash = ResourceText.Hash(w.Content);
        if (have is not null)
        {
            switch (Inspect(have))
            {
                case "ours" when have.ContentHash == contentHash:
                    return;
                case "drifted" when !force:
                    throw new ResourceLibraryException($"{w.Path} was changed since Astra wrote it", ResourceFailure.Conflict);
                case "drifted":
                    BackupFile(w.Path);
                    break;
            }
        }
        else if (env.FileExists(w.Path) || Directory.Exists(w.Path))
        {
            throw new ResourceLibraryException($"{w.Path} already exists and is not Astra's; rename or remove it first", ResourceFailure.Conflict);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(w.Path)!);
        env.WriteTextAtomic(w.Path, w.Content, bom: false);
        var record = have ?? NewWrite(item, w, contentHash);
        record.ContentHash = contentHash;
        record.WrittenAt = DateTimeOffset.UtcNow;
        await ledger.UpsertWriteAsync(record, ct);
    }

    private async Task WriteBlockAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, bool force, CancellationToken ct)
    {
        var existed = env.FileExists(w.Path);
        var text = env.ReadTextOrNull(w.Path) ?? "";
        var current = ResourceText.GetBlock(text, w.Locator);
        var hash = ResourceText.Hash(w.Content);
        if (have is not null)
        {
            if (current is not null && ResourceText.Hash(current) == have.ContentHash)
            {
                if (have.ContentHash == hash) return;
            }
            else if (current is not null && !force)
            {
                throw new ResourceLibraryException($"The section \"{w.Locator}\" in {w.Path} was changed since Astra wrote it", ResourceFailure.Conflict);
            }
        }
        else if (current is not null && !force)
        {
            throw new ResourceLibraryException($"{w.Path} already has a section \"{w.Locator}\" that Astra did not write", ResourceFailure.Conflict);
        }
        if (existed) BackupFile(w.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(w.Path)!);
        env.WriteTextAtomic(w.Path, ResourceText.SetBlock(text, w.Locator, w.Content), bom: false);
        var record = have ?? NewWrite(item, w, hash);
        record.ContentHash = hash;
        record.Entry = have?.Entry ?? (existed ? "existed" : "created");   // whether the file was the user's before Astra
        record.WrittenAt = DateTimeOffset.UtcNow;
        await ledger.UpsertWriteAsync(record, ct);
    }

    private async Task WriteHookAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, CancellationToken ct)
    {
        var entry = JsonNode.Parse(w.Entry!)!;
        var array = ReadHookArray(w.Path, w.Locator);
        if (array is null && ReadRaw(w.Path, "hooks." + w.Locator) is not null)
            throw new ResourceLibraryException($"hooks.{w.Locator} in {w.Path} is not a list; Astra leaves it alone", ResourceFailure.Conflict);
        var next = array is null ? [] : (JsonArray)array.DeepClone();
        // the entry Astra wrote before, if it still stands exactly as written, is replaced; a changed one is the user's now
        if (have?.Entry is { } old && JsonNode.Parse(old) is { } oldNode)
        {
            var index = IndexOf(next, oldNode);
            if (index >= 0) next.RemoveAt(index);
        }
        if (IndexOf(next, entry) < 0) next.Add(entry.DeepClone());
        await ApplyHookArrayAsync(w, array, next, ct);
        var record = have ?? NewWrite(item, w, "");
        record.Entry = w.Entry;
        record.ContentHash = ResourceText.Hash(w.Entry!);
        record.WrittenAt = DateTimeOffset.UtcNow;
        await ledger.UpsertWriteAsync(record, ct);
    }

    private string? ReadRaw(string file, string keyPath) =>
        ConfigEditorOps.Create(ConfigFileFormat.Json, env.ReadTextOrNull(file) ?? "").Read(new ConfigChange(file, ConfigFileFormat.Json, keyPath, null, null));

    private static int IndexOf(JsonArray array, JsonNode node)
    {
        for (var i = 0; i < array.Count; i++)
            if (JsonNode.DeepEquals(array[i], node)) return i;
        return -1;
    }

    /// <summary>Writes <c>hooks.&lt;event&gt;</c> through the applier: the array's original (or its absence) is recorded once, so everything Astra added goes with the last entry.</summary>
    private Task ApplyHookArrayAsync(Want w, JsonArray? before, JsonArray after, CancellationToken ct)
    {
        _ = ct;
        var keyPath = "hooks." + w.Locator;
        var stateKind = w.Client + "-hooks";
        var change = new ConfigChange(w.Path, ConfigFileFormat.Json, keyPath, before?.ToJsonString(), after.Count == 0 && before is null ? null : after.ToJsonString());
        if (change.Before == change.After) return Task.CompletedTask;
        applier.Apply(applier.Preview(stateKind, [change]));
        return Task.CompletedTask;
    }

    private async Task WriteConfigAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, bool force, CancellationToken ct)
    {
        var place = ConfigPlace(w.Client)!;
        var stateKind = w.Client + "-env";
        var ops = ConfigEditorOps.Create(place.Format, env.ReadTextOrNull(w.Path) ?? "");
        var current = ops.Read(new ConfigChange(w.Path, place.Format, w.Locator, null, null));
        var source = place.Format == ConfigFileFormat.Json ? ResourceText.Quote(w.Content) : TomlValueText.ForString(w.Content);
        var after = ConfigValueCodec.Encode(place.Format, source);
        var recorded = state.Get(stateKind, w.Path, w.Locator);
        if (recorded is null)
        {
            if (current is not null && !force)
                throw new ResourceLibraryException($"{w.Path} already sets {w.Locator.Split('.')[^1]}; the user's value is not overwritten", ResourceFailure.Conflict);
        }
        else if (!ConfigValueCodec.EqualsValue(place.Format, current, recorded.AppliedValueJson))
        {
            if (current is not null && !force)
                throw new ResourceLibraryException($"{w.Locator.Split('.')[^1]} in {w.Path} was changed since Astra wrote it", ResourceFailure.Conflict);
            // removed by the user, or forced: written again below
        }
        var before = current;
        if (before is not null && ConfigValueCodec.EqualsValue(place.Format, before, after) && recorded is not null) { await EnsureEnvWriteAsync(item, w, have, ct); return; }
        applier.Apply(applier.Preview(stateKind, [new ConfigChange(w.Path, place.Format, w.Locator, current, after)]));
        await EnsureEnvWriteAsync(item, w, have, ct);
    }

    private async Task EnsureEnvWriteAsync(LibraryItemRecord item, Want w, LibraryWriteRecord? have, CancellationToken ct)
    {
        var record = have ?? NewWrite(item, w, "");
        record.ContentHash = ResourceText.Hash(w.Content);
        record.WrittenAt = DateTimeOffset.UtcNow;
        await ledger.UpsertWriteAsync(record, ct);
    }

    // ---------------------------------------------------------------- removing

    /// <summary>Takes writes out of the clients: only what is still exactly as Astra wrote it. A changed one is left (the user's now) unless forced.</summary>
    private async Task RemoveWritesAsync(LibraryItemRecord item, IReadOnlyList<LibraryWriteRecord> writes, bool force, CancellationToken ct)
    {
        _ = item;
        var all = (await ledger.ListWritesAsync(null, ct)).ToList();
        foreach (var w in writes)
        {
            try
            {
                RemoveOne(w, force, all);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EditorException)
            {
                throw new ResourceLibraryException($"{w.Path}: {e.Message}", ResourceFailure.Invalid);
            }
            await ledger.DeleteWriteAsync(w.Id, ct);
            all.RemoveAll(x => x.Id == w.Id);
        }
    }

    private void RemoveOne(LibraryWriteRecord w, bool force, List<LibraryWriteRecord> all)
    {
        var held = Inspect(w);
        switch (w.Mode)
        {
            case WriteModes.File:
                if (held == "ours" || (held == "drifted" && force))
                {
                    if (held == "drifted") BackupFile(w.Path);
                    env.DeleteFile(w.Path);
                    PruneEmptyDirs(w.Path);
                }
                break;
            case WriteModes.Block:
                if (held == "ours" || (held == "drifted" && force))
                {
                    var text = env.ReadTextOrNull(w.Path) ?? "";
                    BackupFile(w.Path);
                    var next = ResourceText.RemoveBlock(text, w.Locator);
                    if (string.IsNullOrWhiteSpace(next) && w.Entry == "created") env.DeleteFile(w.Path);
                    else env.WriteTextAtomic(w.Path, next, bom: false);
                }
                break;
            case WriteModes.Hook:
                RemoveHook(w, all.Any(x => x.Id != w.Id && x.Mode == WriteModes.Hook && x.Client == w.Client && x.Path == w.Path && x.Locator == w.Locator));
                break;
            case WriteModes.Config:
                RemoveConfig(w, force);
                break;
            case WriteModes.Router:
                break;
        }
    }

    private void RemoveHook(LibraryWriteRecord w, bool othersRemain)
    {
        var array = ReadHookArray(w.Path, w.Locator);
        if (array is null || w.Entry is null) return;
        var next = (JsonArray)array.DeepClone();
        var index = IndexOf(next, JsonNode.Parse(w.Entry)!);
        if (index < 0) return;   // the user changed or removed it: theirs from now on
        next.RemoveAt(index);
        var keyPath = "hooks." + w.Locator;
        var stateKind = w.Client + "-hooks";
        var recorded = state.Get(stateKind, w.Path, keyPath);
        // the array Astra created goes with its last entry; one the user had keeps their entries
        var after = next.Count == 0 && recorded is { OriginalAbsent: true } ? null : next.ToJsonString();
        applier.Apply(applier.Preview(stateKind, [new ConfigChange(w.Path, ConfigFileFormat.Json, keyPath, array.ToJsonString(), after)]));
        // nothing of Astra's is left in this array: its recorded original is no longer needed
        if (after is not null && !othersRemain) state.Delete(stateKind, w.Path, keyPath);
    }

    private void RemoveConfig(LibraryWriteRecord w, bool force)
    {
        var place = ConfigPlace(w.Client);
        if (place is null) return;
        var stateKind = w.Client + "-env";
        var held = Inspect(w);
        if (held == "missing") { state.Delete(stateKind, w.Path, w.Locator); return; }
        if (held == "drifted" && !force) { state.Delete(stateKind, w.Path, w.Locator); return; }   // theirs now
        var ops = ConfigEditorOps.Create(place.Format, env.ReadTextOrNull(w.Path) ?? "");
        var current = ops.Read(new ConfigChange(w.Path, place.Format, w.Locator, null, null));
        var plan = new ConfigChangePlan(stateKind, [new ConfigChange(w.Path, place.Format, w.Locator, current, null)], []);
        if (force) applier.ForceRestore(plan);
        else applier.Disable(plan);
    }

    private void PruneEmptyDirs(string file)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()
                && Path.GetFileName(dir) is "agents" or "commands" or "droids" or "prompts")
                Directory.Delete(dir);
        }
        catch (IOException)
        {
            // an empty folder left behind is harmless
        }
    }

    /// <summary>The offline <c>restore-all</c> step: takes every written item out of the clients (changed content is left). Returns the writes it could not take back.</summary>
    public Task<IReadOnlyList<string>> RemoveAllAsync(CancellationToken ct = default) => LockedAsync<IReadOnlyList<string>>(async () =>
    {
        var left = new List<string>();
        var all = (await ledger.ListWritesAsync(null, ct)).ToList();
        foreach (var w in all.ToList())
        {
            try
            {
                var held = Inspect(w);
                if (held == "drifted") left.Add($"{w.Path} ({w.Locator})");
                RemoveOne(w, force: false, all);
                await ledger.DeleteWriteAsync(w.Id, ct);
                all.RemoveAll(x => x.Id == w.Id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or EditorException)
            {
                left.Add($"{w.Path}: {e.Message}");
            }
        }
        return left;
    }, ct);

    // ---------------------------------------------------------------- projects

    public Task<ResourceProjectInfo> AddProjectAsync(string path, string? name, CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (!Path.IsPathRooted(path)) throw new ResourceLibraryException("A project is a full directory path", ResourceFailure.Invalid);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(full)) throw new ResourceLibraryException("That directory does not exist", ResourceFailure.NotFound);
        await ledger.UpsertProjectAsync(new LibraryProjectRecord { Path = full, Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim(), AddedAt = DateTimeOffset.UtcNow }, ct);
        return new ResourceProjectInfo(full, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) : name.Trim(), true);
    }, ct);

    /// <summary>Forgets a project; refused while an item or a Skill still places something in it.</summary>
    public Task RemoveProjectAsync(string path, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        if ((await ledger.ListWritesAsync(null, ct)).Any(w => w.Project == path))
            throw new ResourceLibraryException("Something of Astra's is still placed in this project; take it out first", ResourceFailure.Conflict);
        if ((await ledger.ListItemsAsync(null, ct)).Any(i => ResourceText.ParseTargets(i.TargetsJson).Any(t => t.Project == path)))
            throw new ResourceLibraryException("An item still targets this project; remove it from the item first", ResourceFailure.Conflict);
        await ledger.DeleteProjectAsync(path, ct);
        return true;
    }, ct);

    // ---------------------------------------------------------------- backups

    private void BackupFile(string path)
    {
        if (!env.FileExists(path)) return;
        _backupRoot ??= NewBackupRoot();
        var safe = path.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_').Replace(':', '_');
        var dest = Path.Combine(_backupRoot, "files", safe);
        for (var n = 2; File.Exists(dest); n++) dest = Path.Combine(_backupRoot, "files", $"{safe}-{n}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        env.CopyFile(path, dest);
    }

    private string NewBackupRoot()
    {
        Directory.CreateDirectory(paths.LibraryBackupsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(paths.LibraryBackupsDir, stamp + "-res");
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(paths.LibraryBackupsDir, $"{stamp}-res-{n}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var old in new DirectoryInfo(paths.LibraryBackupsDir).EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(KeepBackups))
                old.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // pruning is housekeeping; never let it fail a change
        }
        return dir;
    }
}
