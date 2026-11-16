using System.Text.Json.Nodes;
using Astra.Data.Backup;
using Astra.Server.Backup;

namespace Astra.Server.IntegrationTests;

/// <summary>The sync merge with two simulated machines and one shared remote: edits, deletes (tombstones), conflicts (last writer wins), joins.</summary>
public sealed class SyncMergerTests
{
    private static readonly HashSet<string> Scope = [BackupSections.Providers, BackupSections.Clients];

    private sealed class Remote
    {
        public List<BundleEntry> Entries { get; set; } = [];
    }

    private sealed class Device(string id)
    {
        public string Id { get; } = id;
        public Dictionary<string, BackupDoc> Docs { get; } = [];
        public Dictionary<string, SyncBaseline> Baseline { get; } = [];
        public Dictionary<string, long> Pending { get; } = [];
        public List<SyncConflict> Conflicts { get; } = [];
        public long LastNow { get; private set; }

        public void Set(string section, string id, string name, string? updatedAt = null)
        {
            var data = new JsonObject { ["name"] = name };
            if (updatedAt is not null) data["updated_at"] = updatedAt;
            Docs[SyncMerger.KeyOf(section, id)] = new BackupDoc { Section = section, Id = id, Label = name, Data = data };
        }

        public void Remove(string section, string id) => Docs.Remove(SyncMerger.KeyOf(section, id));

        public string? Name(string section, string id) => Docs.TryGetValue(SyncMerger.KeyOf(section, id), out var d) ? d.Data["name"]!.GetValue<string>() : null;

        /// <summary>A change is first seen at <paramref name="seenAt"/> (what the service records before it touches the network).</summary>
        public void See(long seenAt)
        {
            foreach (var (key, d) in Docs)
                if (!Baseline.TryGetValue(key, out var b) || b.Deleted || b.Hash != d.Hash()) Pending.TryAdd(key, seenAt);
            foreach (var (key, b) in Baseline)
                if (!b.Deleted && !Docs.ContainsKey(key)) Pending.TryAdd(key, seenAt);
        }

        public MergeResult Sync(Remote remote, long now)
        {
            LastNow = now;
            var result = SyncMerger.Merge(Docs.Values.ToList(), Baseline, remote.Entries, Scope, Pending, Id, now);
            remote.Entries = result.Remote;
            foreach (var c in result.Pull)
            {
                var key = SyncMerger.KeyOf(c.Section, c.Id);
                if (c.Data is null) Docs.Remove(key);
                else Docs[key] = new BackupDoc { Section = c.Section, Id = c.Id, Label = c.Id, Data = (JsonObject)c.Data.DeepClone() };
            }
            foreach (var (k, b) in result.Baseline) Baseline[k] = b;
            Pending.Clear();
            Conflicts.AddRange(result.Conflicts);
            return result;
        }
    }

    [Fact]
    public void A_new_document_goes_to_the_other_machine_and_an_edit_comes_back()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        Assert.Equal(1, a.Sync(remote, 1000).Pushed);
        var r = b.Sync(remote, 2000);
        Assert.Single(r.Pull);
        Assert.Equal("Alpha", b.Name("providers", "p1"));

        b.Set("providers", "p1", "Alpha 2");
        b.See(3000);
        Assert.Equal(1, b.Sync(remote, 3000).Pushed);
        a.Sync(remote, 4000);
        Assert.Equal("Alpha 2", a.Name("providers", "p1"));
        Assert.Empty(a.Conflicts);

        // nothing changed: a sync is a no-op and writes nothing
        var idle = a.Sync(remote, 5000);
        Assert.False(idle.RemoteChanged);
        Assert.Empty(idle.Pull);
    }

    [Fact]
    public void A_delete_spreads_as_a_tombstone_and_is_not_undone_by_the_next_sync()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        b.Sync(remote, 2000);

        a.Remove("providers", "p1");
        a.See(3000);
        a.Sync(remote, 3000);
        var tomb = Assert.Single(remote.Entries);
        Assert.True(tomb.Deleted);
        Assert.Equal("aaaa", tomb.Device);

        b.Sync(remote, 4000);
        Assert.Null(b.Name("providers", "p1"));
        a.Sync(remote, 5000);
        b.Sync(remote, 6000);
        Assert.Null(a.Name("providers", "p1"));
        Assert.Null(b.Name("providers", "p1"));
    }

    [Fact]
    public void Conflicting_edits_keep_the_later_one_and_report_it_on_the_machine_that_lost()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        b.Sync(remote, 1500);

        a.Set("providers", "p1", "from A");
        a.See(2000);
        b.Set("providers", "p1", "from B");
        b.See(2500); // B changed it later
        a.Sync(remote, 3000);   // A is first to the server: its edit goes up
        var r = b.Sync(remote, 3500); // B's was the later one: it wins, and the conflict is B's to see
        Assert.Equal("from B", b.Name("providers", "p1"));
        Assert.Equal("local", Assert.Single(r.Conflicts).Winner);
        a.Sync(remote, 4000);
        Assert.Equal("from B", a.Name("providers", "p1"));
        Assert.Empty(a.Conflicts); // A had nothing to resolve: the overwritten edit is B's report, not A's
    }

    [Fact]
    public void The_machine_whose_edit_loses_sees_the_conflict_when_it_syncs_second()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        b.Sync(remote, 1500);
        a.Set("providers", "p1", "from A");
        a.See(2000);
        b.Set("providers", "p1", "from B");
        b.See(2500);
        b.Sync(remote, 3000);
        var r = a.Sync(remote, 3500);
        Assert.Equal("remote", Assert.Single(r.Conflicts).Winner);
        Assert.Equal("from B", a.Name("providers", "p1"));
    }

    [Fact]
    public void An_equal_edit_on_both_sides_is_no_conflict()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        b.Sync(remote, 1500);
        a.Set("providers", "p1", "same");
        b.Set("providers", "p1", "same");
        a.See(2000);
        b.See(2000);
        a.Sync(remote, 2500);
        b.Sync(remote, 3000);
        Assert.Empty(a.Conflicts);
        Assert.Empty(b.Conflicts);
        Assert.Equal("same", b.Name("providers", "p1"));
    }

    [Fact]
    public void A_later_edit_beats_an_earlier_delete_and_a_later_delete_beats_an_earlier_edit()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("providers", "p1", "Alpha");
        a.Set("providers", "p2", "Beta");
        a.Sync(remote, 1000);
        b.Sync(remote, 1500);

        a.Remove("providers", "p1");     // deleted first ...
        a.See(2000);
        b.Set("providers", "p1", "edited"); // ... edited after
        b.See(2500);
        b.Set("providers", "p2", "edited"); // edited first ...
        b.See(2000);
        a.Remove("providers", "p2");     // ... deleted after
        a.Pending["providers\np2"] = 2600;
        a.Sync(remote, 3000);
        b.Sync(remote, 3500);
        a.Sync(remote, 4000);
        Assert.Equal("edited", a.Name("providers", "p1"));
        Assert.Equal("edited", b.Name("providers", "p1"));
        Assert.Null(a.Name("providers", "p2"));
        Assert.Null(b.Name("providers", "p2"));
    }

    [Fact]
    public void A_document_the_remote_lost_without_a_tombstone_is_put_back_not_read_as_a_delete()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        remote.Entries = []; // somebody replaced the file with an empty one
        var r = a.Sync(remote, 2000);
        Assert.Equal(1, r.Pushed);
        Assert.Equal("Alpha", a.Name("providers", "p1"));
        Assert.Single(remote.Entries);
        Assert.False(remote.Entries[0].Deleted);
    }

    [Fact]
    public void Joining_takes_the_remote_version_of_a_difference_and_keeps_what_only_this_machine_has()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);

        var b = new Device("bbbb");
        b.Set("providers", "p1", "Different"); // no timestamp to say it is newer
        b.Set("providers", "p9", "Only here");
        var r = b.Sync(remote, 2000);
        Assert.Equal("Alpha", b.Name("providers", "p1"));
        Assert.Equal("Only here", b.Name("providers", "p9"));
        Assert.Equal(1, r.Pushed);
        a.Sync(remote, 3000);
        Assert.Equal("Only here", a.Name("providers", "p9"));
    }

    [Fact]
    public void A_document_whose_row_says_it_is_newer_wins_a_join()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        a.Set("providers", "p1", "Alpha", "2026-11-01T00:00:00.000Z");
        a.Sync(remote, DateTimeOffset.Parse("2026-11-01T00:00:00Z").ToUnixTimeMilliseconds() + 1000);
        var b = new Device("bbbb");
        b.Set("providers", "p1", "Newer here", "2026-11-10T00:00:00.000Z");
        b.Sync(remote, DateTimeOffset.Parse("2026-11-11T00:00:00Z").ToUnixTimeMilliseconds());
        Assert.Equal("Newer here", b.Name("providers", "p1"));
        a.Sync(remote, DateTimeOffset.Parse("2026-11-12T00:00:00Z").ToUnixTimeMilliseconds());
        Assert.Equal("Newer here", a.Name("providers", "p1"));
    }

    [Fact]
    public void Clients_are_never_deleted_by_a_sync()
    {
        var remote = new Remote();
        var a = new Device("aaaa");
        var b = new Device("bbbb");
        a.Set("clients", "client/claude-code", "x");
        a.Sync(remote, 1000);
        b.Sync(remote, 2000);
        a.Remove("clients", "client/claude-code");
        a.See(3000);
        var r = a.Sync(remote, 3000);
        Assert.False(remote.Entries.Single().Deleted);
        Assert.Equal(0, r.Pushed);
    }

    [Fact]
    public void Sections_outside_the_scope_stay_as_the_remote_has_them()
    {
        var remote = new Remote();
        remote.Entries.Add(new BundleEntry { Doc = new BackupDoc { Section = "tokens", Id = "tokens/t1", Label = "t", Data = new JsonObject { ["name"] = "t" } }, Ts = 5, Device = "zzzz" });
        var a = new Device("aaaa");
        a.Set("providers", "p1", "Alpha");
        a.Sync(remote, 1000);
        Assert.Contains(remote.Entries, e => e.Doc.Section == "tokens");
        Assert.Null(a.Name("tokens", "tokens/t1"));
    }
}
