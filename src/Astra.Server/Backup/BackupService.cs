using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Data;
using Astra.Data.Backup;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server.Backup;

public static class RestoreModes
{
    /// <summary>Adds what is missing and updates what differs; removes nothing.</summary>
    public const string Merge = "merge";

    /// <summary>Makes the chosen sections equal to the file: what the file lacks is removed (clients are never removed).</summary>
    public const string Replace = "replace";
}

/// <summary>One document's fate in a restore: <c>added</c>, <c>changed</c>, <c>removed</c> or <c>unchanged</c>.</summary>
public sealed record PlanItem(string Section, string Id, string Label, string Status);

public sealed class RestorePlan
{
    public required Bundle Bundle { get; init; }
    public required string Mode { get; init; }
    public List<string> Sections { get; } = [];
    public List<PlanItem> Items { get; } = [];
    public List<DocChange> Changes { get; } = [];
    public List<string> Notes { get; } = [];
}

/// <summary>
/// Whole-installation backup: collects every configuration document (<see cref="ConfigDocs"/>) into a <see cref="Bundle"/>, seals it
/// (<see cref="BackupEnvelope"/>) and, the other way round, opens a file, previews what restoring it would change and works out the
/// changes to apply. Secrets are DataProtection-bound to this machine, so they travel decrypted <em>inside</em> an encrypted bundle only and are
/// protected again by the machine that restores; a file without a passphrase never holds one. Design after magpie's internal/backup (MIT).
/// </summary>
public sealed class BackupService(ConfigDocs docs, DeviceIdentity device)
{
    public const string FileExtension = ".astra-backup";

    /// <summary>The sections a request names, validated; none named means <see cref="BackupSections.Default"/> (plus usage when asked).</summary>
    public static List<string> ResolveSections(IEnumerable<string>? requested, bool includeUsage = false)
    {
        if (requested is null || !requested.Any())
        {
            var d = BackupSections.Default.ToList();
            if (includeUsage) d.Add(BackupSections.Usage);
            return d;
        }
        var list = requested.Distinct().ToList();
        if (list.FirstOrDefault(s => !BackupSections.IsKnown(s)) is { } bad) throw new AdminApiException(400, $"Unknown backup section: {bad}");
        return [.. BackupSections.All.Where(list.Contains)];
    }

    public async Task<Bundle> CollectAsync(IEnumerable<string> sections, bool keys, string kind, bool forSync, CancellationToken ct)
    {
        var list = sections.ToList();
        var report = new ReadReport();
        var found = await docs.ReadAsync(list, new ReadOptions(keys, forSync), report, ct);
        var bundle = Bundle.FromDocs(found, list, keys, kind, ServerOptions.Version, await device.GetAsync(ct));
        bundle.Warnings.AddRange(report.Unreadable);
        return bundle;
    }

    /// <summary>How many documents each section would hold, for the "what is in a backup" view.</summary>
    public async Task<Dictionary<string, int>> CountAsync(CancellationToken ct)
    {
        var found = await docs.ReadAsync(BackupSections.All, new ReadOptions(false), null, ct);
        var counts = BackupSections.All.ToDictionary(s => s, _ => 0);
        foreach (var d in found) counts[d.Section]++;
        return counts;
    }

    /// <summary>The bundle as a file: encrypted with <paramref name="passphrase"/>, or plain when it holds no secrets and none is given.</summary>
    public static byte[] Seal(Bundle bundle, string? passphrase, int iterations = BackupEnvelope.DefaultIterations)
    {
        return BackupEnvelope.Seal(bundle.ToJson(), passphrase, bundle.Keys, iterations, content: ContentHash(bundle));
    }

    /// <summary>A fingerprint of the configuration a bundle holds, secrets and times left out: two bundles of the same setup have the same one.</summary>
    public static string ContentHash(Bundle b)
    {
        var sb = new StringBuilder();
        foreach (var e in b.All.Where(e => !e.Deleted).OrderBy(e => e.Doc.Section, StringComparer.Ordinal).ThenBy(e => e.Doc.Id, StringComparer.Ordinal))
            sb.Append(e.Doc.Section).Append('/').Append(e.Doc.Id).Append('=').Append(e.Doc.Hash(ignoreSecrets: true)).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    public static Bundle Open(byte[] file, string? passphrase) => Bundle.FromJson(BackupEnvelope.Open(file, passphrase));

    /// <summary>Whether the file is the older providers-only backup (<c>astra-providers</c>), which the provider importer reads.</summary>
    public static bool IsLegacyProviders(byte[] file)
    {
        try { return JsonNode.Parse(file) is JsonObject o && o["format"] is JsonValue v && v.TryGetValue<string>(out var f) && f == ProviderBackupService.FormatId; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    // ---------------------------------------------------------------- preview

    /// <summary>What restoring <paramref name="bundle"/> would do to this installation, section by section, and the changes that would do it.</summary>
    public async Task<RestorePlan> PlanAsync(Bundle bundle, IEnumerable<string>? sections, string mode, CancellationToken ct)
    {
        if (mode is not (RestoreModes.Merge or RestoreModes.Replace)) throw new AdminApiException(400, "mode must be merge or replace");
        var chosen = (sections is null || !sections.Any() ? bundle.Sections.Where(s => s != BackupSections.Usage) : sections)
            .Where(s => BackupSections.IsKnown(s) && bundle.Sections.Contains(s)).Distinct().ToList();
        chosen = [.. BackupSections.All.Where(chosen.Contains)];
        var plan = new RestorePlan { Bundle = bundle, Mode = mode };
        plan.Sections.AddRange(chosen);
        if (!bundle.Keys) plan.Notes.Add("nokeys");

        var report = new ReadReport();
        var local = (await docs.ReadAsync(chosen, new ReadOptions(bundle.Keys), report, ct)).ToDictionary(d => d.Section + "\n" + d.Id);
        var seen = new HashSet<string>();
        foreach (var section in chosen)
        {
            foreach (var e in bundle.Entries.GetValueOrDefault(section, []).Where(e => !e.Deleted))
            {
                var key = section + "\n" + e.Doc.Id;
                seen.Add(key);
                if (!local.TryGetValue(key, out var mine))
                {
                    plan.Items.Add(new PlanItem(section, e.Doc.Id, e.Doc.Label, "added"));
                    plan.Changes.Add(new DocChange(section, e.Doc.Id, e.Doc.Data));
                }
                else if (mine.Hash(ignoreSecrets: !bundle.Keys) != e.Doc.Hash(ignoreSecrets: !bundle.Keys))
                {
                    plan.Items.Add(new PlanItem(section, e.Doc.Id, e.Doc.Label, "changed"));
                    plan.Changes.Add(new DocChange(section, e.Doc.Id, e.Doc.Data));
                }
                else plan.Items.Add(new PlanItem(section, e.Doc.Id, e.Doc.Label, "unchanged"));
            }
            if (mode == RestoreModes.Replace && !ConfigDocs.NoDelete.Contains(section))
                foreach (var mine in local.Values.Where(d => d.Section == section && !seen.Contains(d.Section + "\n" + d.Id)))
                {
                    plan.Items.Add(new PlanItem(section, mine.Id, mine.Label, "removed"));
                    plan.Changes.Add(new DocChange(section, mine.Id, null));
                }
        }
        return plan;
    }
}
