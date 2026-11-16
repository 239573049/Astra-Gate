using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Data.Backup;
using Astra.Server.Api;

namespace Astra.Server.Backup;

/// <summary>One document inside a bundle. <see cref="Ts"/> (Unix ms) and <see cref="Device"/> are sync's: when and where it last changed; a removed document is a tombstone (<see cref="Deleted"/>, no data).</summary>
public sealed class BundleEntry
{
    public BackupDoc Doc { get; set; } = new();
    public long Ts { get; set; }
    public string? Device { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>
/// The content of a backup or sync file, before sealing: documents grouped by section, plus who made it and whether secrets are in it.
/// <c>kind</c> is <c>backup</c> or <c>sync</c>. Untrusted when read from a file: <see cref="FromJson"/> bounds everything.
/// </summary>
public sealed class Bundle
{
    public const int CurrentVersion = 1;
    public const int MaxDocs = 200_000;

    public string Kind { get; set; } = "backup";
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public string AstraVersion { get; set; } = "";
    public string? Device { get; set; }

    /// <summary>Secrets are inside (decrypted, so the file must be encrypted).</summary>
    public bool Keys { get; set; }

    /// <summary>Whether usage totals were asked for.</summary>
    public List<string> Sections { get; set; } = [];

    public Dictionary<string, List<BundleEntry>> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>What reading the sources reported as unreadable (secrets this machine could not decrypt).</summary>
    public List<string> Warnings { get; } = [];

    public IEnumerable<BundleEntry> All => Entries.Values.SelectMany(v => v);

    public JsonObject ToJson()
    {
        var sections = new JsonObject();
        foreach (var section in BackupSections.All)
        {
            if (!Entries.TryGetValue(section, out var list)) continue;
            var array = new JsonArray();
            foreach (var e in list.OrderBy(e => e.Doc.Id, StringComparer.Ordinal))
            {
                var item = new JsonObject { ["id"] = e.Doc.Id, ["label"] = e.Doc.Label };
                if (e.Ts != 0) item["ts"] = e.Ts;
                if (e.Device is not null) item["dev"] = e.Device;
                if (e.Deleted) item["deleted"] = true;
                else item["data"] = e.Doc.Data.DeepClone();
                array.Add((JsonNode)item);
            }
            sections[section] = array;
        }
        var o = new JsonObject
        {
            ["bundle"] = CurrentVersion, ["kind"] = Kind, ["created_at"] = CreatedAt, ["astra_version"] = AstraVersion, ["keys"] = Keys,
            ["sections"] = new JsonArray(Sections.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["docs"] = sections,
        };
        if (Device is not null) o["device"] = Device;
        return o;
    }

    public static Bundle FromJson(JsonObject o)
    {
        if (o["bundle"] is not JsonValue bv || !bv.TryGetValue<int>(out var version) || version < 1) throw new AdminApiException(400, "This file is not a backup bundle");
        if (version > CurrentVersion) throw new AdminApiException(400, "The backup comes from a newer Astra; update Astra to read it");
        var b = new Bundle
        {
            Kind = Text(o["kind"]) ?? "backup", CreatedAt = Text(o["created_at"]) ?? "", AstraVersion = Text(o["astra_version"]) ?? "", Device = Text(o["device"]),
            Keys = o["keys"] is JsonValue kv && kv.TryGetValue<bool>(out var keys) && keys,
        };
        if (o["sections"] is JsonArray listed)
            foreach (var s in listed)
                if (Text(s) is { } id && BackupSections.IsKnown(id)) b.Sections.Add(id);
        var count = 0;
        if (o["docs"] is JsonObject docs)
            foreach (var (section, node) in docs)
            {
                if (!BackupSections.IsKnown(section) || node is not JsonArray array) continue;
                var list = new List<BundleEntry>();
                foreach (var item in array.OfType<JsonObject>())
                {
                    if (++count > MaxDocs) throw new AdminApiException(400, "The backup holds too many documents");
                    if (Text(item["id"]) is not { Length: > 0 } id || id.Length > 512) continue;
                    var deleted = item["deleted"] is JsonValue dv && dv.TryGetValue<bool>(out var d) && d;
                    if (!deleted && item["data"] is not JsonObject) continue;
                    list.Add(new BundleEntry
                    {
                        Doc = new BackupDoc { Section = section, Id = id, Label = Text(item["label"]) ?? id, Data = deleted ? new JsonObject() : (JsonObject)item["data"]!.DeepClone() },
                        Ts = item["ts"] is JsonValue tv && tv.TryGetValue<long>(out var ts) ? ts : 0,
                        Device = Text(item["dev"]), Deleted = deleted,
                    });
                }
                b.Entries[section] = list;
                if (!b.Sections.Contains(section)) b.Sections.Add(section);
            }
        return b;
    }

    public static Bundle FromDocs(IEnumerable<BackupDoc> docs, IEnumerable<string> sections, bool keys, string kind, string version, string? device)
    {
        var b = new Bundle { Kind = kind, AstraVersion = version, Device = device, Keys = keys, Sections = [.. sections] };
        foreach (var d in docs)
        {
            if (!b.Entries.TryGetValue(d.Section, out var list)) b.Entries[d.Section] = list = [];
            list.Add(new BundleEntry { Doc = d });
        }
        foreach (var s in b.Sections) b.Entries.TryAdd(s, []);
        return b;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
