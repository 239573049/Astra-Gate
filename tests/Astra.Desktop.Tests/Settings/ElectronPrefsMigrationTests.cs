using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Tests.Settings;

/// <summary>
/// The first-launch migration of the Electron app's state (desktop/src/main.ts prefsPath + the renderer's localStorage) over fake
/// userData directories: hand-built LevelDB logs and tables in Chromium's localStorage key / value encoding.
/// </summary>
public sealed class ElectronPrefsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-electron-" + Guid.NewGuid().ToString("N"));

    public ElectronPrefsMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ---------- LevelDB fixtures ----------

    private static byte[] Key(string item, string origin = "app://astra") => [.. Encoding.Latin1.GetBytes("_" + origin + "\0"), 1, .. Encoding.Latin1.GetBytes(item)];

    private static byte[] Latin1Value(string v) => [1, .. Encoding.Latin1.GetBytes(v)];

    private static byte[] Utf16Value(string v) => [0, .. Encoding.Unicode.GetBytes(v)];

    private static void Varint(List<byte> b, ulong v)
    {
        while (v >= 0x80)
        {
            b.Add((byte)(v | 0x80));
            v >>= 7;
        }
        b.Add((byte)v);
    }

    /// <summary>A write batch (seq, count, puts / deletes).</summary>
    private static byte[] Batch(ulong seq, params (byte[] Key, byte[]? Value)[] ops)
    {
        var b = new List<byte>();
        var head = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(head, seq);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8), (uint)ops.Length);
        b.AddRange(head);
        foreach (var (k, v) in ops)
        {
            b.Add(v is null ? (byte)0 : (byte)1);
            Varint(b, (ulong)k.Length);
            b.AddRange(k);
            if (v is null) continue;
            Varint(b, (ulong)v.Length);
            b.AddRange(v);
        }
        return [.. b];
    }

    /// <summary>A log file: each batch in one FULL record (checksum not verified by the reader, so zero).</summary>
    private static byte[] Log(params byte[][] batches)
    {
        var b = new List<byte>();
        foreach (var batch in batches)
        {
            b.AddRange(new byte[4]);
            b.Add((byte)(batch.Length & 0xff));
            b.Add((byte)(batch.Length >> 8));
            b.Add(1);
            b.AddRange(batch);
        }
        return [.. b];
    }

    /// <summary>A block with full keys (no prefix sharing) and one restart point.</summary>
    private static byte[] Block(params (byte[] Key, byte[] Value)[] entries)
    {
        var b = new List<byte>();
        foreach (var (k, v) in entries)
        {
            Varint(b, 0);
            Varint(b, (ulong)k.Length);
            Varint(b, (ulong)v.Length);
            b.AddRange(k);
            b.AddRange(v);
        }
        b.AddRange(new byte[4]); // restart[0] = 0
        b.AddRange(BitConverter.GetBytes(1u));
        return [.. b];
    }

    private static byte[] Snappy(byte[] raw)
    {
        var b = new List<byte>();
        Varint(b, (ulong)raw.Length);
        for (var i = 0; i < raw.Length; i += 60)
        {
            var n = Math.Min(60, raw.Length - i);
            b.Add((byte)((n - 1) << 2));
            b.AddRange(raw.AsSpan(i, n).ToArray());
        }
        return [.. b];
    }

    private static byte[] InternalKey(byte[] user, ulong seq, byte type)
    {
        var trailer = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(trailer, (seq << 8) | type);
        return [.. user, .. trailer];
    }

    /// <summary>A table with one data block (optionally Snappy-compressed) and its index block.</summary>
    private static byte[] Table(bool snappy, params (byte[] Key, byte[] Value)[] entries)
    {
        var file = new List<byte>();
        var data = Block(entries);
        var stored = snappy ? Snappy(data) : data;
        file.AddRange(stored);
        file.Add(snappy ? (byte)1 : (byte)0);
        file.AddRange(new byte[4]);
        var handle = new List<byte>();
        Varint(handle, 0);
        Varint(handle, (ulong)stored.Length);
        var indexOffset = file.Count;
        var index = Block((entries[^1].Key, [.. handle]));
        file.AddRange(index);
        file.Add(0);
        file.AddRange(new byte[4]);
        var footer = new List<byte>();
        Varint(footer, 0);
        Varint(footer, 0);
        Varint(footer, (ulong)indexOffset);
        Varint(footer, (ulong)index.Length);
        while (footer.Count < 40) footer.Add(0);
        footer.AddRange(BitConverter.GetBytes(0xdb4775248b80fb57UL));
        file.AddRange(footer);
        return [.. file];
    }

    private string LevelDb()
    {
        var dir = ChromiumLocalStorage.LevelDbDir(_dir);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------- tests ----------

    [Fact]
    public void Snappy_literals_and_overlapping_copies_decode()
    {
        // "abcd" literal, then copy 8 bytes from offset 4 (overlaps its own output).
        byte[] compressed = [12, 0x0C, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0x11, 0x04];
        Assert.Equal("abcdabcdabcd", Encoding.ASCII.GetString(ChromiumLocalStorage.Snappy.Decompress(compressed)));
    }

    [Fact]
    public void Log_entries_newest_wins_deletions_drop_and_other_origins_are_ignored()
    {
        var dir = LevelDb();
        File.WriteAllBytes(Path.Combine(dir, "000003.log"), Log(
            Batch(1, (Key("astra.theme"), Latin1Value("light")), (Key("astra.accent"), Latin1Value("#f74f9e"))),
            Batch(3, (Key("astra.theme"), Latin1Value("dark")), (Key("astra.locale"), Utf16Value("en"))),
            Batch(5, (Key("astra.accent"), null)),
            Batch(6, (Key("astra.theme", "https://evil.example"), Latin1Value("light")))));

        var items = ChromiumLocalStorage.Read(dir, "app://astra");

        Assert.Equal("dark", items["astra.theme"]);
        Assert.Equal("en", items["astra.locale"]);
        Assert.False(items.ContainsKey("astra.accent"));
        Assert.Equal(2, items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Table_entries_are_read_and_a_newer_log_entry_overrides_them(bool snappy)
    {
        var dir = LevelDb();
        File.WriteAllBytes(Path.Combine(dir, "000005.ldb"), Table(snappy,
            (InternalKey(Key("astra.accent"), 2, 1), Latin1Value("#62ba46")),
            (InternalKey(Key("astra.sidebar"), 2, 1), Latin1Value("collapsed")),
            (InternalKey(Key("astra.theme"), 2, 1), Latin1Value("light"))));
        File.WriteAllBytes(Path.Combine(dir, "000007.log"), Log(Batch(10, (Key("astra.theme"), Latin1Value("dark")))));

        var items = ChromiumLocalStorage.Read(dir, "app://astra");

        Assert.Equal("#62ba46", items["astra.accent"]);
        Assert.Equal("collapsed", items["astra.sidebar"]);
        Assert.Equal("dark", items["astra.theme"]);
    }

    [Fact]
    public void Damaged_or_missing_files_yield_what_could_be_read()
    {
        Assert.Empty(ChromiumLocalStorage.Read(Path.Combine(_dir, "nope"), "app://astra"));
        var dir = LevelDb();
        File.WriteAllBytes(Path.Combine(dir, "000001.ldb"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "000002.log"), [.. Log(Batch(1, (Key("astra.locale"), Latin1Value("zh")))), 9, 9, 9, 9, 200, 0, 1]);
        Assert.Equal("zh", ChromiumLocalStorage.Read(dir, "app://astra")["astra.locale"]);
    }

    [Fact]
    public void Read_carries_over_the_prefs_file_and_local_storage_in_canonical_shape()
    {
        File.WriteAllText(Path.Combine(_dir, "desktop-prefs.json"), """
            {
              "closeHintShown": true,
              "tray": { "title": "cost", "sections": { "chart": false, "clients": true, "bogus": 1 } },
              "updateReminder": { "version": "0.11.6", "count": 2.7, "lastRemindedAt": 1761480000000, "skippedVersion": "" }
            }
            """);
        File.WriteAllBytes(Path.Combine(LevelDb(), "000003.log"), Log(Batch(1,
            (Key("astra.theme"), Latin1Value("dark")),
            (Key("astra.accent"), Latin1Value("#A550A7")),
            (Key("astra.sidebar"), Latin1Value("expanded")),
            (Key("astra.locale"), Latin1Value("en")),
            (Key("astra.remote.acknowledged"), Latin1Value("1")))));

        var e = ElectronPrefsMigration.Read(_dir)!;

        Assert.True(e.CloseHintShown);
        Assert.Equal(
            """{"title":"cost","sections":{"overview":true,"cost":true,"chart":false,"topModel":true,"compareYesterday":false,"cacheHitRate":false,"quota":true,"clients":true}}""",
            e.Tray!.ToJsonString());
        Assert.Equal("""{"version":"0.11.6","count":2,"lastRemindedAt":1761480000000,"skippedVersion":null}""", e.UpdateReminder!.ToJsonString());
        Assert.Equal("dark", e.Theme);
        Assert.Equal("#a550a7", e.Accent);
        Assert.False(e.SidebarCollapsed);
        Assert.Equal("en", e.Language);
        Assert.True(e.RemoteAcknowledged);
    }

    [Fact]
    public void Absent_values_stay_null_and_invalid_ones_are_dropped()
    {
        File.WriteAllText(Path.Combine(_dir, "desktop-prefs.json"), "{ not json");
        File.WriteAllBytes(Path.Combine(LevelDb(), "000003.log"), Log(Batch(1,
            (Key("astra.theme"), Latin1Value("sepia")), (Key("astra.accent"), Latin1Value("red")), (Key("astra.locale"), Latin1Value("fr")))));

        var e = ElectronPrefsMigration.Read(_dir)!;

        Assert.True(e.IsEmpty);
        Assert.Null(ElectronPrefsMigration.Find([_dir, Path.Combine(_dir, "missing")]));
        Assert.Null(ElectronPrefsMigration.Read(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Find_takes_the_first_candidate_with_state()
    {
        var empty = Path.Combine(_dir, "a");
        var full = Path.Combine(_dir, "b");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "desktop-prefs.json"), """{ "closeHintShown": false }""");

        var e = ElectronPrefsMigration.Find([Path.Combine(_dir, "none"), empty, full]);

        Assert.Equal(full, e!.Source);
        Assert.False(e.CloseHintShown);
    }

    [Fact]
    public void User_data_candidates_follow_electrons_app_data_per_platform()
    {
        string? NoEnv(string _) => null;
        Assert.Equal(Path.Combine("/h", "Library", "Application Support", "@aidotnet", "desktop"), ElectronPrefsMigration.UserDataCandidates("darwin", "/h", NoEnv)[0]);
        Assert.Equal(Path.Combine("/h", ".config", "@aidotnet", "desktop"), ElectronPrefsMigration.UserDataCandidates("linux", "/h", NoEnv)[0]);
        Assert.Equal(Path.Combine("/x", "@aidotnet", "desktop"), ElectronPrefsMigration.UserDataCandidates("linux", "/h", k => k == "XDG_CONFIG_HOME" ? "/x" : null)[0]);
        Assert.Equal(Path.Combine("/r", "@aidotnet", "desktop"), ElectronPrefsMigration.UserDataCandidates("win32", "/h", k => k == "APPDATA" ? "/r" : null)[0]);
        Assert.Equal(Path.Combine("/h", "Library", "Application Support", "Astra"), ElectronPrefsMigration.UserDataCandidates("darwin", "/h", NoEnv)[1]);
    }

    [Fact]
    public void Reminder_parse_matches_parseReminderState()
    {
        Assert.Equal("""{"version":null,"count":0,"lastRemindedAt":null,"skippedVersion":null}""", ElectronPrefsMigration.ParseReminder(null).ToJsonString());
        Assert.Equal("""{"version":"1.0.0","count":0,"lastRemindedAt":null,"skippedVersion":"1.0.0"}""",
            ElectronPrefsMigration.ParseReminder(JsonNode.Parse("""{"version":"1.0.0","count":-3,"lastRemindedAt":"x","skippedVersion":"1.0.0"}""")).ToJsonString());
    }
}
