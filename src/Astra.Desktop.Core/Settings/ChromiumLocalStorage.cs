using System.Buffers.Binary;
using System.Text;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// Reads one origin's <c>localStorage</c> out of a Chromium profile (<c>&lt;userData&gt;/Local Storage/leveldb</c>) without Chromium:
/// the Electron-era web renderer kept the theme, accent, language and sidebar state there (web/src/shell/appearance.tsx, i18n/index.tsx,
/// components/layout/AppShell.tsx), and the first Avalonia launch carries them over (plan avalonia-client §5.4).
/// <para>
/// This is a small read-only LevelDB reader: the write-ahead logs (<c>*.log</c>, record framing + write batches) and the sorted tables
/// (<c>*.ldb</c> / <c>*.sst</c>: footer → index block → data blocks, Snappy or uncompressed) are scanned and the newest entry per key
/// (highest sequence number, deletions included) wins. Chromium stores a localStorage item under <c>_&lt;origin&gt;\0&lt;key&gt;</c>, where the key
/// and the value each start with a format byte (0 = UTF-16LE, 1 = Latin-1). Everything is best effort: a locked, damaged or
/// unknown file yields what could be read, never an exception — the migration is a convenience.
/// </para>
/// </summary>
public static class ChromiumLocalStorage
{
    /// <summary>The leveldb directory inside an Electron <c>userData</c> directory.</summary>
    public static string LevelDbDir(string userData) => Path.Combine(userData, "Local Storage", "leveldb");

    /// <summary>Every item stored for <paramref name="origin"/> (e.g. <c>app://astra</c>) in the leveldb at <paramref name="dir"/>; empty when there is none.</summary>
    public static IReadOnlyDictionary<string, string> Read(string dir, string origin)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return result;
        var entries = new Dictionary<string, (ulong Seq, byte[]? Value)>(StringComparer.Ordinal);
        string[] files;
        try
        {
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            byte[] bytes;
            try
            {
                // FileShare.ReadWrite: a running Electron app keeps the log open.
                using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bytes = new byte[s.Length];
                s.ReadExactly(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            try
            {
                if (name.EndsWith(".log", StringComparison.Ordinal)) ReadLog(bytes, entries);
                else if (name.EndsWith(".ldb", StringComparison.Ordinal) || name.EndsWith(".sst", StringComparison.Ordinal)) ReadTable(bytes, entries);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            {
                // A damaged file: keep what the others hold.
            }
        }

        var prefix = Latin1("_" + origin + "\0");
        foreach (var (key, (_, value)) in entries)
        {
            if (value is null) continue;
            var raw = Encoding.Latin1.GetBytes(key);
            if (!raw.AsSpan().StartsWith(prefix)) continue;
            var itemKey = DecodeString(raw.AsSpan(prefix.Length));
            var itemValue = DecodeString(value);
            if (itemKey is not null && itemValue is not null) result[itemKey] = itemValue;
        }
        return result;
    }

    // ---------- strings ----------

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Chromium's localStorage string encoding: a format byte, then UTF-16LE (0) or Latin-1 (1).</summary>
    internal static string? DecodeString(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return null;
        return b[0] switch
        {
            0 when (b.Length - 1) % 2 == 0 => Encoding.Unicode.GetString(b[1..]),
            1 => Encoding.Latin1.GetString(b[1..]),
            _ => null,
        };
    }

    // ---------- log files ----------

    private const int BlockSize = 32768;

    /// <summary>Reassembles the log's records (FULL / FIRST / MIDDLE / LAST, 32 KiB blocks) and applies each write batch.</summary>
    internal static void ReadLog(byte[] log, Dictionary<string, (ulong Seq, byte[]? Value)> entries)
    {
        var pos = 0;
        var pending = new List<byte>();
        while (pos + 7 <= log.Length)
        {
            var blockLeft = BlockSize - pos % BlockSize;
            if (blockLeft < 7)
            {
                pos += blockLeft; // trailer padding
                continue;
            }
            var length = BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(pos + 4));
            var type = log[pos + 6];
            if (type == 0 && length == 0)
            {
                pos += blockLeft; // zero-filled (preallocated) rest of the block
                continue;
            }
            if (pos + 7 + length > log.Length) break;
            var data = log.AsSpan(pos + 7, length);
            pos += 7 + length;
            switch (type)
            {
                case 1: // FULL
                    pending.Clear();
                    ApplyBatch(data, entries);
                    break;
                case 2: // FIRST
                    pending.Clear();
                    pending.AddRange(data.ToArray());
                    break;
                case 3: // MIDDLE
                    pending.AddRange(data.ToArray());
                    break;
                case 4: // LAST
                    pending.AddRange(data.ToArray());
                    ApplyBatch(pending.ToArray(), entries);
                    pending.Clear();
                    break;
            }
        }
    }

    /// <summary>A write batch: sequence (8), count (4), then <c>count</c> records of tag 1 (put key value) or 0 (delete key).</summary>
    private static void ApplyBatch(ReadOnlySpan<byte> batch, Dictionary<string, (ulong Seq, byte[]? Value)> entries)
    {
        if (batch.Length < 12) return;
        var seq = BinaryPrimitives.ReadUInt64LittleEndian(batch);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(batch[8..]);
        var pos = 12;
        for (var i = 0u; i < count && pos < batch.Length; i++, seq++)
        {
            var tag = batch[pos++];
            var key = ReadSlice(batch, ref pos);
            byte[]? value = null;
            if (tag == 1) value = ReadSlice(batch, ref pos).ToArray();
            else if (tag != 0) return;
            Put(entries, key, seq, value);
        }
    }

    private static void Put(Dictionary<string, (ulong Seq, byte[]? Value)> entries, ReadOnlySpan<byte> key, ulong seq, byte[]? value)
    {
        // Keys are compared as bytes; Latin-1 maps every byte to one char, so a string key is lossless.
        var k = Encoding.Latin1.GetString(key);
        if (!entries.TryGetValue(k, out var old) || old.Seq <= seq) entries[k] = (seq, value);
    }

    private static ReadOnlySpan<byte> ReadSlice(ReadOnlySpan<byte> b, ref int pos)
    {
        var len = (int)ReadVarint(b, ref pos);
        if (len < 0 || pos + len > b.Length) throw new FormatException("slice out of range");
        var s = b.Slice(pos, len);
        pos += len;
        return s;
    }

    internal static ulong ReadVarint(ReadOnlySpan<byte> b, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (pos >= b.Length) throw new FormatException("truncated varint");
            var c = b[pos++];
            result |= (ulong)(c & 0x7f) << shift;
            if ((c & 0x80) == 0) return result;
        }
        throw new FormatException("varint too long");
    }

    // ---------- table files ----------

    private const ulong TableMagic = 0xdb4775248b80fb57UL;

    /// <summary>A sorted table: the 48-byte footer names the index block, whose values are the data blocks' handles.</summary>
    internal static void ReadTable(byte[] table, Dictionary<string, (ulong Seq, byte[]? Value)> entries)
    {
        if (table.Length < 48) return;
        var footer = table.AsSpan(table.Length - 48);
        if (BinaryPrimitives.ReadUInt64LittleEndian(footer[40..]) != TableMagic) return;
        var pos = 0;
        _ = ReadVarint(footer, ref pos); // metaindex offset
        _ = ReadVarint(footer, ref pos); // metaindex size
        var indexOffset = ReadVarint(footer, ref pos);
        var indexSize = ReadVarint(footer, ref pos);
        foreach (var (_, handle) in BlockEntries(ReadBlock(table, indexOffset, indexSize)))
        {
            var hp = 0;
            var offset = ReadVarint(handle, ref hp);
            var size = ReadVarint(handle, ref hp);
            foreach (var (internalKey, value) in BlockEntries(ReadBlock(table, offset, size)))
            {
                if (internalKey.Length < 8) continue;
                var trailer = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
                var type = (byte)(trailer & 0xff);
                Put(entries, internalKey.AsSpan(0, internalKey.Length - 8), trailer >> 8, type == 1 ? value : null);
            }
        }
    }

    /// <summary>One block's contents, decompressed: <c>size</c> bytes then a type byte (0 none, 1 Snappy) and a CRC.</summary>
    private static byte[] ReadBlock(byte[] table, ulong offset, ulong size)
    {
        if (offset + size + 5 > (ulong)table.Length) throw new FormatException("block out of range");
        var raw = table.AsSpan((int)offset, (int)size);
        return table[(int)(offset + size)] switch
        {
            0 => raw.ToArray(),
            1 => Snappy.Decompress(raw),
            _ => throw new FormatException("unsupported block compression"),
        };
    }

    /// <summary>A block's key / value pairs (prefix-compressed keys; the restart array at the end is skipped).</summary>
    private static List<(byte[] Key, byte[] Value)> BlockEntries(byte[] block)
    {
        var list = new List<(byte[], byte[])>();
        if (block.Length < 4) return list;
        var restarts = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
        var end = block.Length - 4 - (int)restarts * 4;
        if (end < 0) throw new FormatException("bad restart array");
        var span = block.AsSpan(0, end);
        var pos = 0;
        var key = Array.Empty<byte>();
        while (pos < span.Length)
        {
            var shared = (int)ReadVarint(span, ref pos);
            var nonShared = (int)ReadVarint(span, ref pos);
            var valueLen = (int)ReadVarint(span, ref pos);
            if (shared > key.Length || pos + nonShared + valueLen > span.Length) throw new FormatException("bad block entry");
            var next = new byte[shared + nonShared];
            key.AsSpan(0, shared).CopyTo(next);
            span.Slice(pos, nonShared).CopyTo(next.AsSpan(shared));
            pos += nonShared;
            key = next;
            list.Add((key, span.Slice(pos, valueLen).ToArray()));
            pos += valueLen;
        }
        return list;
    }

    /// <summary>The Snappy block format (raw, no framing), as LevelDB uses it.</summary>
    public static class Snappy
    {
        /// <summary>Decompresses one raw Snappy block; throws <see cref="FormatException"/> / <see cref="ArgumentException"/> on damaged input.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> src)
        {
            var pos = 0;
            var length = ReadVarint(src, ref pos);
            if (length > int.MaxValue) throw new FormatException("snappy block too large");
            var dst = new byte[(int)length];
            var d = 0;
            while (pos < src.Length)
            {
                var tag = src[pos++];
                int len, offset;
                switch (tag & 3)
                {
                    case 0:
                        len = tag >> 2;
                        if (len >= 60)
                        {
                            var bytes = len - 59;
                            len = 0;
                            for (var i = 0; i < bytes; i++) len |= src[pos++] << (8 * i);
                        }
                        len += 1;
                        src.Slice(pos, len).CopyTo(dst.AsSpan(d));
                        pos += len;
                        d += len;
                        continue;
                    case 1:
                        len = ((tag >> 2) & 7) + 4;
                        offset = ((tag >> 5) << 8) | src[pos++];
                        break;
                    case 2:
                        len = (tag >> 2) + 1;
                        offset = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]);
                        pos += 2;
                        break;
                    default:
                        len = (tag >> 2) + 1;
                        offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(src[pos..]);
                        pos += 4;
                        break;
                }
                if (offset <= 0 || offset > d || d + len > dst.Length) throw new FormatException("bad snappy copy");
                // Byte by byte: a copy may overlap its own output (offset < len repeats a run).
                for (var i = 0; i < len; i++, d++) dst[d] = dst[d - offset];
            }
            if (d != dst.Length) throw new FormatException("snappy length mismatch");
            return dst;
        }
    }
}
