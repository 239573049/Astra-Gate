using System.IO.Compression;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Compressed request bodies to a provider (settings <c>request_compression</c>: <c>zstd</c>, <c>gzip</c>, <c>br</c>
/// or <c>deflate</c>; anything else sends the body as is), which the app-wide <c>RequestCompression</c> setting can
/// re-point or turn off. codex-cli sends its ChatGPT backend requests as zstd, so the ChatGPT subscription template
/// turns it on.
/// </summary>
public static class UpstreamCompression
{
    /// <summary>Settings key of the coding.</summary>
    public const string SettingKey = "request_compression";

    /// <summary>Values of the app-wide override (<see cref="AppSettings.RequestCompression"/>).</summary>
    public static readonly string[] Overrides = ["default", "zstd", "gzip", "br", "deflate", "none"];

    /// <summary>
    /// The request coding for <paramref name="provider"/>, or null for none: the provider's own, unless the app-wide
    /// <paramref name="appOverride"/> names another coding ("none" turns it off). A provider that does not compress
    /// stays uncompressed whatever the override says.
    /// </summary>
    public static string? Of(Provider provider, string? appOverride)
    {
        if (provider.Settings[SettingKey] is not JsonValue value || !value.TryGetValue<string>(out var setting)) return null;
        var coding = setting.Trim().ToLowerInvariant();
        if (!IsCoding(coding)) return null;
        return appOverride switch
        {
            "none" => null,
            { } other when IsCoding(other) => other,
            _ => coding,
        };
    }

    private static bool IsCoding(string coding) => coding is "zstd" or "gzip" or "br" or "deflate";

    /// <summary>The body compressed with <paramref name="coding"/> (zstd at codex-cli's level 3, the rest at Fastest).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> body, string coding)
    {
        if (coding == "zstd")
        {
            using var zstd = new ZstdSharp.Compressor(3);
            return zstd.Wrap(body).ToArray();
        }
        using var output = new MemoryStream();
        using (Stream encoder = coding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            // HTTP's "deflate" is the zlib format (RFC 9110 §8.4.1.2).
            "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(coding), coding, null),
        })
            encoder.Write(body);
        return output.ToArray();
    }

    /// <summary>A JSON request content carrying already-compressed bytes and their <c>Content-Encoding</c>.</summary>
    public static PooledContent Content(byte[] compressed, string coding)
    {
        var content = new PooledContent(PooledBuffer.Wrap(compressed));
        content.Headers.ContentEncoding.Add(coding);
        return content;
    }
}
