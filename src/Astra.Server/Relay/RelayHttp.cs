using System.Buffers;
using System.Net.Http.Headers;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.WebUtilities;

namespace Astra.Server.Relay;

/// <summary>What the relay passes between the upstream and its caller, and how long it waits for each side.</summary>
internal static class RelayHttp
{
    /// <summary>
    /// <paramref name="url"/> with the caller's query parameters added to any it already has — all but the one a query-key
    /// credential travels in (case-insensitively), which only <see cref="RelayCaller.Request"/> sets.
    /// </summary>
    public static string WithClientQuery(string url, IQueryCollection query, string? credentialParameter) =>
        QueryHelpers.AddQueryString(url, query.Where(parameter =>
            credentialParameter is null || !parameter.Key.Equals(credentialParameter, StringComparison.OrdinalIgnoreCase)));

    /// <summary>A hop-by-hop header (RFC 9110 §7.6.1), or one the message's <c>Connection</c> header names: never relayed.</summary>
    public static bool IsHopHeader(string name, IEnumerable<string?> connection)
    {
        if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            return true;
        return connection.Any(value => value?.Split(',', StringSplitOptions.TrimEntries)
            .Contains(name, StringComparer.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// The upstream's diagnostics a caller can act on — when to retry, its rate limits and usage, the request ids to quote
    /// — copied onto the caller's response. Cookies and everything else stay behind.
    /// </summary>
    public static void CopyDiagnostics(HttpResponseHeaders headers, HttpResponse target)
    {
        foreach (var (name, values) in headers)
            if (!IsHopHeader(name, headers.Connection)
                && (name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("x-usage-", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("request-id", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-github-request-id", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("x-goog-request-id", StringComparison.OrdinalIgnoreCase)))
                target.Headers[name] = values.ToArray();
    }

    /// <summary>Reads a whole upstream body of at most <paramref name="maxBytes"/>, each read within <paramref name="idleSeconds"/>.</summary>
    /// <exception cref="GatewayException">502 when it is larger, 504 when the upstream stops sending.</exception>
    public static async Task<PooledBuffer> ReadLimitedAsync(Stream source, int maxBytes, int idleSeconds, string what, CancellationToken ct)
    {
        var buffer = new PooledBuffer(16 * 1024);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                var room = Math.Min(16 * 1024, maxBytes - buffer.Length + 1);
                var count = await source.ReadAsync(buffer.GetMemory(room)[..room], deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0) return buffer;
                buffer.Advance(count);
                if (buffer.Length > maxBytes)
                    throw new GatewayException(502, "api_error", $"上游{what}超过 {maxBytes / (1024 * 1024)} MiB。");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            buffer.Release();
            throw new GatewayException(504, "timeout", $"读取上游{what}超时。");
        }
        catch
        {
            buffer.Release();
            throw;
        }
    }

    /// <summary>Relays an upstream body as it arrives: each read within the idle timeout, each write within the write timeout.</summary>
    /// <exception cref="GatewayException">504 when either side stalls.</exception>
    public static async Task CopyAsync(Stream source, Stream destination, RelayOptions options, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (true)
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(options.UpstreamIdleTimeoutSeconds));
                var count = await source.ReadAsync(buffer, deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0) return;
                deadline.CancelAfter(TimeSpan.FromSeconds(options.DownstreamWriteTimeoutSeconds));
                await destination.WriteAsync(buffer.AsMemory(0, count), deadline.Token);
                await destination.FlushAsync(deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GatewayException(504, "timeout", "读取上游或写给客户端超时。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
