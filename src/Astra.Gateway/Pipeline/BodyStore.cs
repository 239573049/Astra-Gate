using System.Text;
using Astra.Core;

namespace Astra.Gateway.Pipeline;

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

    private const int MaxBytes = 8 * 1024 * 1024;

    public static string RefFor(string requestId, DateTimeOffset startedAtUtc) => $"{startedAtUtc:yyyy-MM-dd}/{requestId}";

    public async Task WriteAsync(string bodyRef, string part, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var file = PathFor(bodyRef, part);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var slice = body.Length > MaxBytes ? body[..MaxBytes] : body;
        await File.WriteAllBytesAsync(file, slice.ToArray(), ct);
    }

    public Task WriteAsync(string bodyRef, string part, string body, CancellationToken ct = default) =>
        WriteAsync(bodyRef, part, Encoding.UTF8.GetBytes(body), ct);

    public string? Read(string bodyRef, string part)
    {
        var file = PathFor(bodyRef, part);
        return File.Exists(file) ? File.ReadAllText(file) : null;
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
