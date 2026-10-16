using System.Buffers;
using System.Text;
using Astra.Core;

namespace Astra.Gateway.Pipeline;

/// <summary>The start of a captured part and the size of the whole part, in bytes.</summary>
public readonly record struct BodyPreview(string Text, long Size);

/// <summary>
/// Debug body capture: bodies/&lt;yyyy-MM-dd&gt;/&lt;requestId&gt;.&lt;part&gt;.txt. The body ref stored on the request is
/// "&lt;yyyy-MM-dd&gt;/&lt;requestId&gt;". Secrets are never written: callers pass bodies; the one header part
/// (<see cref="UpstreamRequestHeaders"/>) is redacted by its writer (credentials keep scheme + last 4 only).
/// </summary>
public sealed class BodyStore(AstraPaths paths)
{
    public const string ClientRequest = "client-request";
    public const string UpstreamRequest = "upstream-request";
    /// <summary>JSON: the outbound URL + final request headers (credentials redacted to scheme + last 4).</summary>
    public const string UpstreamRequestHeaders = "upstream-request-headers";
    public const string UpstreamResponse = "upstream-response";
    public const string ClientResponse = "client-response";

    /// <summary>The most of one part that is written (the rest is cut).</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public static string RefFor(string requestId, DateTimeOffset startedAtUtc) => $"{startedAtUtc:yyyy-MM-dd}/{requestId}";

    public async Task WriteAsync(string bodyRef, string part, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var file = PathFor(bodyRef, part);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, body.Length > MaxBytes ? body[..MaxBytes] : body, ct);
    }

    public Task WriteAsync(string bodyRef, string part, string body, CancellationToken ct = default) =>
        WriteTextAsync(bodyRef, part, [body.AsMemory()], ct);

    /// <summary>Writes the builder's chunks as they are: a captured stream never becomes one string.</summary>
    public Task WriteAsync(string bodyRef, string part, StringBuilder body, CancellationToken ct = default)
    {
        var chunks = new List<ReadOnlyMemory<char>>();
        foreach (var chunk in body.GetChunks()) chunks.Add(chunk);
        return WriteTextAsync(bodyRef, part, chunks, ct);
    }

    // Encoded in small pieces into one small pooled buffer: a multi-MB body never rents a multi-MB array, which the
    // shared pool would keep for the life of the process.
    private async Task WriteTextAsync(string bodyRef, string part, List<ReadOnlyMemory<char>> chunks, CancellationToken ct)
    {
        const int Piece = 16 * 1024;
        var file = PathFor(bodyRef, part);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var encoder = Encoding.UTF8.GetEncoder();
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(Piece));
        try
        {
            await using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            long written = 0;
            for (var i = 0; i < chunks.Count && written < MaxBytes; i++)
            {
                var rest = chunks[i];
                var last = i == chunks.Count - 1;
                while (written < MaxBytes)
                {
                    var take = Math.Min(rest.Length, Piece);
                    encoder.Convert(rest.Span[..take], buffer, last && take == rest.Length, out var used, out var bytes, out _);
                    var keep = (int)Math.Min(bytes, MaxBytes - written);
                    await stream.WriteAsync(buffer.AsMemory(0, keep), ct);
                    written += keep;
                    rest = rest[used..];
                    if (rest.IsEmpty) break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public string? Read(string bodyRef, string part)
    {
        var file = PathFor(bodyRef, part);
        return File.Exists(file) ? File.ReadAllText(file) : null;
    }

    /// <summary>
    /// At most the first <paramref name="maxBytes"/> of a part (cut at a character boundary) and the part's full size,
    /// or null when it was not captured. The request detail shows this; the whole part is served from <see cref="FileFor"/>.
    /// </summary>
    public BodyPreview? ReadPreview(string bodyRef, string part, int maxBytes)
    {
        var info = new FileInfo(PathFor(bodyRef, part));
        if (!info.Exists) return null;
        if (info.Length <= maxBytes) return new BodyPreview(File.ReadAllText(info.FullName), info.Length);
        var bytes = new byte[maxBytes];
        using (var stream = info.OpenRead()) stream.ReadExactly(bytes);
        return new BodyPreview(Encoding.UTF8.GetString(bytes, 0, Utf8Boundary(bytes)), info.Length);
    }

    /// <summary>The captured file of a part, or null when it was not captured.</summary>
    public string? FileFor(string bodyRef, string part)
    {
        var file = PathFor(bodyRef, part);
        return File.Exists(file) ? file : null;
    }

    /// <summary>The parts a request can have, in display order.</summary>
    public static IReadOnlyList<string> Parts { get; } = [ClientRequest, UpstreamRequest, UpstreamRequestHeaders, UpstreamResponse, ClientResponse];

    /// <summary>Length of <paramref name="bytes"/> without a UTF-8 sequence cut off at its end.</summary>
    internal static int Utf8Boundary(ReadOnlySpan<byte> bytes)
    {
        var start = bytes.Length - 1;
        // Back up over at most three continuation bytes to the lead byte of the last sequence.
        while (start > 0 && bytes.Length - start < 4 && (bytes[start] & 0xC0) == 0x80) start--;
        if (start < 0) return 0;
        var lead = bytes[start];
        var need = lead < 0x80 ? 1 : (lead & 0xE0) == 0xC0 ? 2 : (lead & 0xF0) == 0xE0 ? 3 : (lead & 0xF8) == 0xF0 ? 4 : 1;
        return start + need <= bytes.Length ? bytes.Length : start;
    }

    /// <summary>Deletes day folders older than <paramref name="days"/>.</summary>
    public int Cleanup(int days)
    {
        if (!Directory.Exists(paths.BodiesDir)) return 0;
        var cutoff = DateTime.UtcNow.Date.AddDays(-days);
        var removed = 0;
        foreach (var dir in Directory.GetDirectories(paths.BodiesDir))
        {
            if (DateTime.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var day)
                && day < cutoff)
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
        }
        return removed;
    }

    private string PathFor(string bodyRef, string part)
    {
        // bodyRef comes from our own DB, but never allow it to escape the bodies dir.
        var safe = bodyRef.Replace('\\', '/');
        if (safe.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(safe)) throw new ArgumentException("Invalid body ref");
        return Path.Combine(paths.BodiesDir, $"{safe}.{part}.txt");
    }
}
