using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Why an upstream call failed, in words (issue #39). <see cref="HttpRequestException"/>'s own message is usually the
/// generic "An error occurred while sending the request." and the cause — a reset connection, a response that ended early,
/// TLS, an HTTP/2 GOAWAY or RST_STREAM — is in its inner exceptions, so this names those instead.
/// </summary>
public static class UpstreamFailure
{
    private const int MaxDepth = 4;

    /// <summary>
    /// The inner exceptions' messages (deduplicated, at most four deep), then the <see cref="HttpRequestError"/> when the
    /// text does not already name it; the outer message only when there is no inner one. The outer message is left out
    /// otherwise because it is either the generic text or carries the upstream's host and port ("Connection refused
    /// (host:443)"), which the inner exception states without them — so the result never names the address it came from.
    /// </summary>
    public static string Describe(HttpRequestException e)
    {
        var parts = new List<string>();
        for (Exception? inner = e.InnerException; inner is not null && parts.Count < MaxDepth; inner = inner.InnerException)
        {
            var text = inner switch
            {
                SocketException socket when string.IsNullOrWhiteSpace(socket.Message) => socket.SocketErrorCode.ToString(),
                _ => inner.Message.Trim(),
            };
            if (text.Length > 0 && !parts.Any(p => p.Contains(text, StringComparison.Ordinal))) parts.Add(text);
        }
        var builder = new StringBuilder(parts.Count == 0 ? e.Message.Trim() : string.Join(" ← ", parts));
        if (e.HttpRequestError != HttpRequestError.Unknown && !builder.ToString().Contains(e.HttpRequestError.ToString(), StringComparison.Ordinal))
            builder.Append(" (").Append(e.HttpRequestError).Append(')');
        return builder.ToString();
    }
}
