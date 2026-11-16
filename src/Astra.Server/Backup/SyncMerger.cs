using System.Globalization;
using Astra.Data.Backup;

namespace Astra.Server.Backup;

/// <summary>The outcome of one three-way merge between this machine, the remote file and the last agreed state.</summary>
public sealed class MergeResult
{
    /// <summary>The remote file's entries after this sync (documents and tombstones, every section the file had).</summary>
    public List<BundleEntry> Remote { get; } = [];

    /// <summary>Documents to write here (and removals: <see cref="DocChange.Data"/> null).</summary>
    public List<DocChange> Pull { get; } = [];

    public List<PlanItem> PullItems { get; } = [];
    public int Pushed { get; set; }
    public bool RemoteChanged { get; set; }

    /// <summary>The agreed state per document after this sync (what the next one starts from).</summary>
    public Dictionary<string, SyncBaseline> Baseline { get; } = [];

    public List<SyncConflict> Conflicts { get; } = [];
}

/// <summary>
/// Multi-machine sync's merge, per document and last-writer-wins (more granular than magpie's per-part merge, which this takes the idea of
/// "compare each part with the hashes of the previous sync" from; MIT). For each document, with <c>L</c> local, <c>R</c> remote and <c>B</c> the state
/// the last sync agreed on:
/// <list type="bullet">
/// <item>only one side differs from <c>B</c>: that side's change (an edit or a delete) goes to the other;</item>
/// <item>both differ and end up equal: nothing to do;</item>
/// <item>both differ and disagree: a conflict — the later change wins (a local change counts from when it was first seen), a tie goes to the larger device id,
/// and the overwritten side is reported;</item>
/// <item>a deletion is a tombstone with a time, so it spreads and can be overruled by a later edit; a document missing from the remote with no tombstone is
/// re-added from here, never read as a deletion.</item>
/// </list>
/// Pure: no I/O, so two simulated devices can be run against one remote in a test.
/// </summary>
public static class SyncMerger
{
    public static string KeyOf(string section, string id) => section + "\n" + id;

    public static MergeResult Merge(
        IReadOnlyCollection<BackupDoc> local, IReadOnlyDictionary<string, SyncBaseline> baseline, IReadOnlyCollection<BundleEntry> remote,
        IReadOnlySet<string> sections, IReadOnlyDictionary<string, long> pending, string device, long now)
    {
        var result = new MergeResult();
        var localMap = local.Where(d => sections.Contains(d.Section)).ToDictionary(d => KeyOf(d.Section, d.Id));
        var remoteMap = remote.ToDictionary(e => KeyOf(e.Doc.Section, e.Doc.Id));
        var keys = localMap.Keys.Concat(remoteMap.Keys.Where(k => sections.Contains(remoteMap[k].Doc.Section))).Concat(baseline.Keys.Where(k => sections.Contains(k.Split('\n')[0]))).Distinct().ToList();
        var cutoff = now - (long)TimeSpan.FromDays(SyncState.TombstoneDays).TotalMilliseconds;

        // What is out of scope or not mentioned here stays as the remote has it.
        foreach (var e in remote.Where(e => !sections.Contains(e.Doc.Section))) result.Remote.Add(e);

        foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            localMap.TryGetValue(key, out var l);
            remoteMap.TryGetValue(key, out var r);
            baseline.TryGetValue(key, out var b);
            var section = key.Split('\n')[0];
            var id = key[(section.Length + 1)..];
            var noDelete = ConfigDocs.NoDelete.Contains(section);

            var hL = l?.Hash();
            var hR = r is { Deleted: false } ? r.Doc.Hash() : null;
            var hB = b is { Deleted: false } ? b.Hash : null;
            var localChanged = b is null ? hL is not null : hL != hB && !(b.Deleted && hL is null);
            var remoteChanged = b is null ? r is not null : b.Deleted ? hR is not null : r is not null && hR != hB;
            if (noDelete && hL is null) localChanged = false; // a client cannot be deleted: only ever added or changed

            var label = l?.Label ?? r?.Doc.Label ?? id;

            void KeepRemote()
            {
                if (r is not null && !(r.Deleted && r.Ts < cutoff)) result.Remote.Add(r);
            }

            void PushLocal(long ts)
            {
                if (l is not null)
                {
                    result.Remote.Add(new BundleEntry { Doc = l.Clone(), Ts = ts, Device = device });
                    result.Baseline[key] = new SyncBaseline { Hash = hL!, Ts = ts };
                }
                else
                {
                    result.Remote.Add(new BundleEntry { Doc = new BackupDoc { Section = section, Id = id, Label = label }, Ts = ts, Device = device, Deleted = true });
                    result.Baseline[key] = new SyncBaseline { Ts = ts, Deleted = true };
                }
                result.Pushed++;
                result.RemoteChanged = true;
            }

            void PullRemote()
            {
                KeepRemote();
                if (r is null) return;
                if (r.Deleted)
                {
                    if (l is not null && !noDelete)
                    {
                        result.Pull.Add(new DocChange(section, id, null));
                        result.PullItems.Add(new PlanItem(section, id, label, "removed"));
                    }
                    result.Baseline[key] = new SyncBaseline { Ts = r.Ts, Deleted = true };
                }
                else
                {
                    result.Pull.Add(new DocChange(section, id, r.Doc.Data));
                    result.PullItems.Add(new PlanItem(section, id, label, l is null ? "added" : "changed"));
                    result.Baseline[key] = new SyncBaseline { Hash = hR!, Ts = r.Ts };
                }
            }

            if (!localChanged && !remoteChanged)
            {
                if (r is null && l is not null) PushLocal(b?.Ts ?? now); // the remote lost it without a tombstone: put it back
                else
                {
                    KeepRemote();
                    if (r is not null) result.Baseline[key] = r.Deleted ? new SyncBaseline { Ts = r.Ts, Deleted = true } : new SyncBaseline { Hash = hR!, Ts = r.Ts };
                    else if (b is not null) result.Baseline[key] = b;
                }
                continue;
            }

            var tsL = pending.TryGetValue(key, out var p) ? p : l is not null ? DocTime(l) ?? (b is null ? 0 : now) : now;
            // A pushed change carries the time it was made (first seen), not the time it was sent: a delete made at 10:00 must not beat an edit made at 11:00 on another machine.
            var stamp = tsL == 0 ? now : tsL;
            if (localChanged && !remoteChanged)
            {
                PushLocal(stamp);
            }
            else if (!localChanged)
            {
                PullRemote();
            }
            else if (hL == hR)
            {
                KeepRemote();
                result.Baseline[key] = hL is null ? new SyncBaseline { Ts = r?.Ts ?? now, Deleted = true } : new SyncBaseline { Hash = hL, Ts = r?.Ts ?? now };
            }
            else
            {
                var tsR = r?.Ts ?? 0;
                var localWins = tsL != tsR ? tsL > tsR : string.CompareOrdinal(device, r?.Device ?? "") > 0;
                result.Conflicts.Add(new SyncConflict
                {
                    Section = section, Id = id, Label = label, Winner = localWins ? "local" : "remote",
                    At = DateTimeOffset.FromUnixTimeMilliseconds(now).ToString("O", CultureInfo.InvariantCulture), Device = r?.Device ?? "",
                });
                if (localWins) PushLocal(stamp); else PullRemote();
            }
        }
        return result;
    }

    /// <summary>When a document was last changed according to its own <c>updated_at</c> column, if it has one.</summary>
    public static long? DocTime(BackupDoc d)
    {
        foreach (var col in new[] { "updated_at", "updated_at_utc" })
            if (d.Data[col] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)
                && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                return t.ToUnixTimeMilliseconds();
        return null;
    }
}
