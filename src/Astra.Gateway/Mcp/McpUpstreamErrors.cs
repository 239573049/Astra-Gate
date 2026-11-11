using System.Net;
using System.Net.Sockets;
using Astra.Core.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Astra.Gateway.Mcp;

/// <summary>
/// The upstream needs OAuth and the gateway holds no usable token. Thrown from the OAuth callback of every data-plane connection: a
/// request must never start an interactive flow (only an admin's "authorize" click does, plan mcp-gateway §4.1).
/// </summary>
public sealed class McpNeedsAuthException(string message) : Exception(message);

/// <summary>An upstream call that failed, with a category that is safe to show to the token's holder (<see cref="McpErrorCategories"/>).</summary>
public sealed class McpUpstreamException(string category, string status, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Category { get; } = category;

    /// <summary>The <see cref="McpServerStatuses"/> value this failure implies for the server.</summary>
    public string Status { get; } = status;
}

/// <summary>Turns what the SDK and the network throw into a category, a server status and a message that never carries the upstream's address or a credential.</summary>
public static class McpUpstreamErrors
{
    public static McpUpstreamException Classify(Exception ex, bool timedOut)
    {
        if (ex is McpUpstreamException already) return already;
        if (timedOut) return new(McpErrorCategories.Timeout, McpServerStatuses.Unreachable, "上游 MCP 服务器响应超时", ex);
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case McpNeedsAuthException:
                    return new(McpErrorCategories.NeedsAuth, McpServerStatuses.NeedsAuth, "上游 MCP 服务器需要授权，请管理员在 Astra 中重新授权", ex);
                case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }:
                    return new(McpErrorCategories.NeedsAuth, McpServerStatuses.AuthError, "上游 MCP 服务器拒绝了 Astra 保存的凭据", ex);
                case McpProtocolException { ErrorCode: McpErrorCode.UnsupportedProtocolVersion }:
                    return new(McpErrorCategories.Protocol, McpServerStatuses.VersionUnsupported, "上游 MCP 服务器使用了 Astra 不支持的协议版本", ex);
                case HttpRequestException or SocketException or IOException or ObjectDisposedException or ClientTransportClosedException:
                    return new(McpErrorCategories.Unreachable, McpServerStatuses.Unreachable, "无法连接上游 MCP 服务器", ex);
            }
        }
        // The SDK reports an authorization failure it could not recover from as an McpException whose text names the scheme.
        if (ex is McpException { Message: { } text } && (text.Contains("unauthorized response", StringComparison.OrdinalIgnoreCase)
                                                        || text.Contains("401", StringComparison.Ordinal)))
            return new(McpErrorCategories.NeedsAuth, McpServerStatuses.NeedsAuth, "上游 MCP 服务器需要授权，请管理员在 Astra 中重新授权", ex);
        return new(McpErrorCategories.Protocol, McpServerStatuses.Unreachable, "上游 MCP 服务器返回了无法处理的响应", ex);
    }
}
