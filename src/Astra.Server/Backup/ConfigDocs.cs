using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Library;
using Astra.Data;
using Astra.Data.Backup;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Routing;
using Astra.Server.Api;

namespace Astra.Server.Backup;

/// <summary>What <see cref="ConfigDocs.ApplyAsync"/> did, beyond the database part.</summary>
public sealed class ConfigApplyResult
{
    public int Written { get; set; }
    public int Removed { get; set; }
    public List<string> Skipped { get; } = [];

    /// <summary>Things that failed after the database part was committed (a client that could not be enabled, a Skill that could not be installed).</summary>
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Every configuration document of the installation, in one place: the generic tables and settings (<see cref="BackupStore"/>), the clients
/// (read as profile entries, written back through <see cref="ClientService.ApplyProfileAsync"/> — the normal enable flow, never a client file), the Skill
/// library (rows and files, written through the <see cref="SkillLibrary"/> engine — never a link) and the plugin sign-in file. The sections a document
/// belongs to are <see cref="BackupSections"/>.
/// </summary>
public sealed class ConfigDocs(
    AstraDatabase db, ISecretProtector secrets, ClientService clients, LibraryService library, SettingsService settings,
    SmartRoutingService smartRouting, PrivacyGuardService privacy, ILogger<ConfigDocs> log)
{
    public const string PluginAuthId = "file/plugin-auth";
    private const long MaxSkillFileBytes = 16L * 1024 * 1024;
    private const long MaxSkillBytes = 32L * 1024 * 1024;

    private readonly BackupStore _store = new(db.Factory, secrets);

    /// <summary>The sections whose documents never disappear because another machine lacks them (a client cannot be "deleted").</summary>
    public static readonly IReadOnlySet<string> NoDelete = new HashSet<string> { BackupSections.Clients };

    public async Task<List<BackupDoc>> ReadAsync(IEnumerable<string> sections, ReadOptions options, ReadReport? report, CancellationToken ct)
    {
        var wanted = sections.ToList();
        var docs = await _store.ReadAsync(wanted, options, report, ct);
        if (wanted.Contains(BackupSections.Signins) && options.Keys && ReadPluginAuth() is { } auth) docs.Add(auth);
        if (wanted.Contains(BackupSections.Clients)) docs.AddRange(await ReadClientsAsync(options, ct));
        if (wanted.Contains(BackupSections.Library)) docs.AddRange(await ReadSkillsAsync(report, ct));
        return docs;
    }

    // ---------------------------------------------------------------- plugin sign-ins

    private BackupDoc? ReadPluginAuth()
    {
        var path = db.Paths.PluginAuthFile;
        if (!File.Exists(path)) return null;
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return null; }
        if (JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true }) is not JsonObject { Count: > 0 } o) return null;
        return new BackupDoc
        {
            Section = BackupSections.Signins, Id = PluginAuthId, Label = "plugin-auth.json",
            Data = new JsonObject { [BackupDoc.SecretsField] = new JsonObject { ["content"] = o.ToJsonString() } },
        };
    }

    private void WritePluginAuth(JsonObject incoming)
    {
        var path = db.Paths.PluginAuthFile;
        JsonObject merged = [];
        if (File.Exists(path))
        {
            try { merged = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? []; } catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { }
        }
        foreach (var (k, v) in incoming) merged[k] = v?.DeepClone();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, merged.ToJsonString());
        AstraPaths.RestrictToOwner(tmp);
        File.Move(tmp, path, overwrite: true);
    }

    // ---------------------------------------------------------------- clients

    private async Task<List<BackupDoc>> ReadClientsAsync(ReadOptions options, CancellationToken ct)
    {
        var docs = new List<BackupDoc>();
        foreach (var e in await clients.CaptureProfileAsync(ct))
        {
            var data = new JsonObject();
            if (!options.ForSync) data["enabled"] = e.Enabled;
            if (e.TokenId is not null) data["token_id"] = e.TokenId;
            if (e.Model is not null) data["model"] = e.Model;
            var bindings = new JsonArray();
            foreach (var b in e.Bindings)
            {
                var o = new JsonObject { ["provider_id"] = b.ProviderId };
                if (b.AccountId is not null) o["account_id"] = b.AccountId;
                bindings.Add((JsonNode)o);
            }
            data["bindings"] = bindings;
            if (e.Extras is not null) data["extras"] = e.Extras.DeepClone();
            docs.Add(new BackupDoc { Section = BackupSections.Clients, Id = "client/" + e.Kind, Label = e.Kind, Data = data });
        }
        return docs;
    }

    private async Task ApplyClientsAsync(List<DocChange> changes, ConfigApplyResult result, CancellationToken ct)
    {
        var current = (await clients.CaptureProfileAsync(ct)).ToDictionary(e => e.Kind);
        var profile = new ClientProfile { Id = "backup", Name = "backup" };
        foreach (var c in changes.Where(c => c.Data is not null))
        {
            var d = c.Data!;
            var kind = c.Id["client/".Length..];
            var entry = new ClientProfileEntry
            {
                Kind = kind,
                // A document without "enabled" (sync's) keeps this machine's state: whether a client is switched on is this machine's.
                Enabled = d["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) ? en : current.TryGetValue(kind, out var cur) && cur.Enabled,
                TokenId = Text(d["token_id"]), Model = Text(d["model"]), Extras = d["extras"] as JsonObject,
            };
            if (d["bindings"] is JsonArray bs)
                foreach (var b in bs.OfType<JsonObject>())
                    if (Text(b["provider_id"]) is { } pid) entry.Bindings.Add(new ClientProfileBinding { ProviderId = pid, AccountId = Text(b["account_id"]) });
            profile.Clients.Add(entry);
        }
        if (profile.Clients.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var plan = await clients.ApplyProfileAsync(profile, new ProfileDto("backup", "backup", now, now, [], false, false), ct);
        foreach (var step in plan.Steps)
        {
            if (step.Action == "failed") result.Errors.Add($"clients/{step.Kind}: {step.Reason}");
            else if (step.Action == "skipped") result.Skipped.Add($"clients/{step.Kind}: {step.Reason}");
            else result.Written++;
        }
    }

    // ---------------------------------------------------------------- skills

    private async Task<List<BackupDoc>> ReadSkillsAsync(ReadReport? report, CancellationToken ct)
    {
        var docs = new List<BackupDoc>();
        foreach (var s in await db.Library.ListSkillsAsync(ct))
        {
            var dir = library.Engine.SkillDir(s.Name);
            var data = new JsonObject
            {
                ["name"] = s.Name, ["description"] = s.Description, ["source_kind"] = s.SourceKind, ["created_at"] = s.CreatedAt.ToString("O"),
            };
            if (s.SourceJson is not null) data["source_json"] = s.SourceJson;
            var files = new JsonObject();
            long total = 0;
            var tooBig = false;
            if (Directory.Exists(dir))
                foreach (var rel in FsLinks.Files(dir))
                {
                    var full = Path.Combine(dir, rel);
                    var len = new FileInfo(full).Length;
                    if (len > MaxSkillFileBytes || (total += len) > MaxSkillBytes) { tooBig = true; break; }
                    files[rel] = Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct));
                }
            if (tooBig) { report?.Unreadable.Add($"skill {s.Name}: too large for a backup, left out"); continue; }
            data["files"] = files;
            docs.Add(new BackupDoc { Section = BackupSections.Library, Id = "skill/" + s.Name, Label = s.Name, Data = data });
        }
        return docs;
    }

    private async Task ApplySkillsAsync(List<DocChange> changes, ConfigApplyResult result, CancellationToken ct)
    {
        var engine = library.Engine;
        foreach (var c in changes)
        {
            var name = c.Id["skill/".Length..];
            try
            {
                if (!SkillLibrary.IsValidName(name)) { result.Skipped.Add($"library/{c.Id}: not a valid Skill name"); continue; }
                if (c.Data is null)
                {
                    await engine.DeleteAsync(name, ct);
                    result.Removed++;
                    continue;
                }
                var temp = Path.Combine(Path.GetTempPath(), "astra-skill-" + Guid.NewGuid().ToString("N"));
                try
                {
                    var root = Path.Combine(temp, name);
                    Directory.CreateDirectory(root);
                    if (c.Data["files"] is JsonObject files)
                        foreach (var (rel, node) in files)
                        {
                            if (Text(node) is not { } b64 || rel.Contains("..") || Path.IsPathRooted(rel) || rel.StartsWith('/')) continue;
                            var target = Path.GetFullPath(Path.Combine(root, rel));
                            if (!target.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            await File.WriteAllBytesAsync(target, Convert.FromBase64String(b64), ct);
                        }
                    var candidates = await engine.ProbeAsync(temp, ct);
                    foreach (var cand in candidates.Where(x => x.Name == name))
                    {
                        if (cand.Status == "exists") continue;
                        var enabledFor = new List<string>();
                        if (cand.Status == "differs")
                        {
                            // The backup's version wins (the caller chose to restore it): the old one is saved by the engine, then put back where it was on.
                            var state = await engine.StateAsync(ct);
                            enabledFor = state.Skills.FirstOrDefault(s => s.Name == name)?.Clients.Where(x => x.State == "own").Select(x => x.Kind).ToList() ?? [];
                            await engine.DeleteAsync(name, ct);
                        }
                        var outcome = await engine.InstallAsync([cand with { Status = "new" }], "backup", Text(c.Data["source_json"]), [], ct);
                        if (outcome.Any(o => !o.Ok)) { result.Errors.Add($"library/{c.Id}: {outcome.First(o => !o.Ok).Reason}"); continue; }
                        foreach (var kind in enabledFor) await engine.SetAsync(name, kind, true, confirm: true, ct);
                        result.Written++;
                    }
                }
                finally
                {
                    try { Directory.Delete(temp, true); } catch (IOException) { }
                }
            }
            catch (Exception e) when (e is SkillLibraryException or IOException or UnauthorizedAccessException or FormatException)
            {
                result.Errors.Add($"library/{c.Id}: {e.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- apply

    /// <summary>
    /// Writes the changes: the database part first, in one transaction (nothing changes when it fails), then the sign-in file, the clients and the Skills,
    /// each reporting its own failures. Services that keep a copy of a setting are told to reload.
    /// </summary>
    public async Task<ConfigApplyResult> ApplyAsync(IReadOnlyList<DocChange> changes, CancellationToken ct)
    {
        var result = new ConfigApplyResult();
        bool Special(DocChange c) => c.Id == PluginAuthId || c.Id.StartsWith("client/", StringComparison.Ordinal) || c.Id.StartsWith("skill/", StringComparison.Ordinal);
        var plain = changes.Where(c => !Special(c)).ToList();
        if (plain.Count > 0)
        {
            var report = await _store.ApplyAsync(plain, ct);
            result.Written += report.Written;
            result.Removed += report.Removed;
            result.Skipped.AddRange(report.Skipped);
            await ReloadAsync(report.SettingsKeys, ct);
        }
        foreach (var c in changes.Where(c => c.Id == PluginAuthId && c.Data is not null))
        {
            try
            {
                if (c.Data!["$secrets"] is JsonObject box && Text(box["content"]) is { } text && JsonNode.Parse(text) is JsonObject o) { WritePluginAuth(o); result.Written++; }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { result.Errors.Add($"signins/{c.Id}: {e.Message}"); }
        }
        var clientChanges = changes.Where(c => c.Id.StartsWith("client/", StringComparison.Ordinal)).ToList();
        if (clientChanges.Count > 0)
            try { await ApplyClientsAsync(clientChanges, result, ct); }
            catch (Exception e) when (e is AdminApiException or IOException or UnauthorizedAccessException)
            {
                result.Errors.Add("clients: " + e.Message);
            }
        var skillChanges = changes.Where(c => c.Id.StartsWith("skill/", StringComparison.Ordinal)).ToList();
        if (skillChanges.Count > 0) await ApplySkillsAsync(skillChanges, result, ct);
        log.LogInformation("Configuration applied: {Written} written, {Removed} removed, {Skipped} skipped, {Errors} failed", result.Written, result.Removed, result.Skipped.Count, result.Errors.Count);
        return result;
    }

    private async Task ReloadAsync(HashSet<string> keys, CancellationToken ct)
    {
        if (keys.Contains("app")) await settings.LoadAsync(ct);
        if (keys.Contains("smart_routing")) await smartRouting.LoadAsync(ct);
        if (keys.Contains("privacy")) await privacy.LoadAsync(ct);
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
