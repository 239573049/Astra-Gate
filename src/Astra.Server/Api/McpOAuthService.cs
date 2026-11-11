using System.Collections.Concurrent;
using Astra.Api.Contracts;
using Astra.Core.Mcp;
using Astra.Data;
using Astra.Gateway.Mcp;
using Astra.Server.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;

namespace Astra.Server.Api;

/// <summary>
/// The interactive half of OAuth for an upstream MCP server (plan mcp-gateway §4.1), started only by an admin's "authorize" click. The SDK
/// runs the protocol (discovery, client registration, PKCE, <c>resource</c>, <c>iss</c> validation, the token exchange) and calls back once
/// it has an authorization address; this class hands that address to the admin, waits for the browser to come back to
/// <c>/api/mcp/oauth/callback</c>, and gives the SDK the code. Nothing sensitive is kept between the two requests but the one-time
/// <c>state</c> in memory; the tokens end up in <c>mcp_oauth</c> through <see cref="McpTokenCache"/>.
/// </summary>
public sealed class McpOAuthService(AstraDatabase db, McpUpstreamPool pool, McpRuntime runtime, ServerOptions server, ILogger<McpOAuthService> logger)
{
    /// <summary>Discovery and registration happen before the address is known.</summary>
    private static readonly TimeSpan AddressWait = TimeSpan.FromSeconds(30);

    /// <summary>How long the admin has to finish in the browser.</summary>
    private static readonly TimeSpan ConsentWait = TimeSpan.FromMinutes(5);

    /// <summary>How long the browser's callback waits to learn how the token exchange ended.</summary>
    private static readonly TimeSpan OutcomeWait = TimeSpan.FromSeconds(30);

    private sealed class Pending
    {
        public readonly TaskCompletionSource<AuthorizationResult?> Code = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>null = authorized; otherwise a sentence for the admin.</summary>
        public readonly TaskCompletionSource<string?> Outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Pending> _byState = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _flows = new(StringComparer.Ordinal);

    /// <summary>
    /// Starts authorizing <paramref name="s"/> and returns where the admin goes. A flow already running for the server is abandoned.
    /// </summary>
    public async Task<McpOAuthStartDto> StartAsync(McpServerRecord s, CancellationToken ct)
    {
        if (s.AuthType != McpAuthTypes.OAuth) throw new AdminApiException(409, "This MCP server does not use OAuth");
        runtime.GatewayBaseUrl = server.GatewayBaseUrl;
        var flow = new CancellationTokenSource();
        if (_flows.TryGetValue(s.Id, out var previous)) previous.Cancel();
        _flows[s.Id] = flow;
        var address = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => FlowAsync(s, address, flow), CancellationToken.None);
        try
        {
            var uri = await address.Task.WaitAsync(AddressWait, ct);
            return uri is null
                ? new McpOAuthStartDto(null, "该 MCP 服务器无需再次授权（已有可用的凭据）")
                : new McpOAuthStartDto(uri.ToString(), null);
        }
        catch (TimeoutException)
        {
            flow.Cancel();
            throw new AdminApiException(504, "连接上游 MCP 服务器超时，没有拿到授权地址");
        }
        catch (McpUpstreamException failure)
        {
            throw new AdminApiException(502, failure.Message);
        }
    }

    private async Task FlowAsync(McpServerRecord s, TaskCompletionSource<Uri?> address, CancellationTokenSource flow)
    {
        string? state = null;
        Pending? pending = null;
        try
        {
            var transport = pool.CreateTransport(s, async (context, token) =>
            {
                state = System.Web.HttpUtility.ParseQueryString(context.AuthorizationUri.Query)["state"];
                if (string.IsNullOrEmpty(state)) throw new McpException("The authorization address carries no state");
                pending = new Pending();
                if (!_byState.TryAdd(state, pending)) throw new McpException("Duplicate authorization state");
                address.TrySetResult(context.AuthorizationUri);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, flow.Token);
                wait.CancelAfter(ConsentWait);
                return await pending.Code.Task.WaitAsync(wait.Token);
            });
            await using var client = await McpClient.CreateAsync(transport, new McpClientOptions
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation { Name = "astra-gateway", Version = McpUpstreamPool.ServerVersion },
            }, cancellationToken: flow.Token);
            // Prove the tokens work (and learn the protocol version) before calling it done.
            await client.ListToolsAsync(cancellationToken: flow.Token);
            await db.Mcp.UpdateStatusAsync(s.Id, McpServerStatuses.Ok, null, client.NegotiatedProtocolVersion, DateTimeOffset.UtcNow);
            pool.Invalidate(s.Id); // the data plane connects again with the new tokens
            address.TrySetResult(null);
            pending?.Outcome.TrySetResult(null);
        }
        catch (Exception ex)
        {
            var failure = McpUpstreamErrors.Classify(ex, timedOut: false);
            logger.LogWarning("MCP 授权失败（{Slug}）：{Type}", s.Slug, ex.GetType().Name);
            // The admin sees the SDK's own sentence for a rejected authorization (an issuer mismatch, a refused registration): it names
            // addresses and parameters, never a token.
            var detail = ex is McpException { Message.Length: > 0 } m ? m.Message : failure.Message;
            address.TrySetException(failure);
            pending?.Outcome.TrySetResult(detail.Length > 300 ? detail[..300] : detail);
            try
            {
                await db.Mcp.UpdateStatusAsync(s.Id, failure.Status, failure.Message, s.ProtocolVersion, DateTimeOffset.UtcNow);
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException) { }
        }
        finally
        {
            if (state is not null) _byState.TryRemove(state, out _);
            if (_flows.TryGetValue(s.Id, out var current) && ReferenceEquals(current, flow)) _flows.TryRemove(s.Id, out _);
            flow.Dispose();
        }
    }

    /// <summary>
    /// The browser came back. The state is single-use: whoever presents it first owns the flow. Returns whether it named a flow and, when it did,
    /// how the authorization ended (null = fine, else a sentence). An error from the authorization server is never shown: it comes from
    /// the address bar, and a mismatching issuer would make it untrusted (RFC 9207).
    /// </summary>
    public async Task<(bool Found, string? Problem)> CompleteAsync(string? state, string? code, string? iss, string? error)
    {
        if (string.IsNullOrEmpty(state) || !_byState.TryRemove(state, out var pending)) return (false, null);
        pending.Code.TrySetResult(error is not null || string.IsNullOrEmpty(code) ? null : new AuthorizationResult { Code = code, State = state, Iss = iss });
        try
        {
            return (true, await pending.Outcome.Task.WaitAsync(OutcomeWait));
        }
        catch (TimeoutException)
        {
            return (true, "授权仍在处理中，请回到 Astra 查看结果");
        }
    }
}
