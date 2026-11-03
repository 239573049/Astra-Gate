using System.Buffers.Binary;
using System.IO.Compression;

namespace Astra.Server.Api;

/// <summary>
/// The picture an image-edit connection test uploads: a red circle on a white background, drawn here so a test needs no
/// asset on disk and sends a few hundred bytes (every edit is billed by its image tokens too).
/// </summary>
internal static class TestImages
{
    /// <summary>Side of the square picture, in pixels.</summary>
    public const int Size = 128;

    /// <summary>What the edit test asks for by default: change what <see cref="RedCircle"/> shows.</summary>
    public const string RedCircleDescription = "a red circle on a white background";

    private static readonly Lazy<byte[]> Circle = new(() => Encode(Size, Size, (x, y) =>
    {
        var dx = x - (Size - 1) / 2.0;
        var dy = y - (Size - 1) / 2.0;
        return dx * dx + dy * dy <= 40 * 40 ? (byte)1 : (byte)0;
    }));

    /// <summary>A 128 × 128 PNG of a red circle on white.</summary>
    public static byte[] RedCircle() => Circle.Value;

    /// <summary>Largest picture a test may upload (the bytes travel base64-encoded inside the test request).</summary>
    public const int MaxUploadBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The media type of an uploaded picture, read from its first bytes (a client's own claim is not trusted): PNG, JPEG or WebP,
    /// the formats the Images API edits accept; null for anything else.
    /// </summary>
    public static string? MediaTypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>The file name extension of a media type <see cref="MediaTypeOf"/> returned.</summary>
    public static string ExtensionOf(string mediaType) => mediaType switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };

    // 8-bit RGB, no filtering, one IDAT: the smallest valid PNG a decoder will not argue about.
    private static byte[] Encode(int width, int height, Func<int, int, byte> inside)
    {
        var raw = new byte[height * (1 + width * 3)];
        var at = 0;
        for (var y = 0; y < height; y++)
        {
            raw[at++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                var red = inside(x, y) == 1;
                raw[at++] = 0xFF;
                raw[at++] = red ? (byte)0x20 : (byte)0xFF;
                raw[at++] = red ? (byte)0x20 : (byte)0xFF;
            }
        }

        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; // bit depth
        header[9] = 2; // colour type: RGB
        WriteChunk(output, "IHDR"u8, header);
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(raw);
            WriteChunk(output, "IDAT"u8, compressed.ToArray());
        }
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
