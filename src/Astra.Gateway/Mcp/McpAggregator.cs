using System.Diagnostics;
using System.Text.Json.Nodes;
using Astra.Core.Mcp;
using Astra.Core.Tokens;
using Astra.Data;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Astra.Gateway.Mcp;

/// <summary>
/// What one token sees of the MCP gateway: the enabled servers bound to it, and whether their tool names carry the server's slug
/// (<c>/mcp</c>, the aggregate view) or not (<c>/mcp/{slug}</c>, one server's own view).
/// </summary>
public sealed record McpView(TokenRecord Token, IReadOnlyList<McpServerRecord> Servers, bool Prefixed);

/// <summary>The tool a call named does not exist in the view — unknown, or a server the token is not bound to; the two look the same on purpose.</summary>
public sealed class McpToolNotFoundException(string name) : Exception($"Unknown tool: {name}");

/// <summary>
/// The MCP gateway's engine between the front end and the upstream pool: per-token visibility, aggregate naming, ordering and routing
/// (plan mcp-gateway §4). It knows nothing of HTTP and nothing of the protocol eras on either side.
/// </summary>
public sealed class McpAggregator(AstraDatabase db, McpUpstreamPool pool, ILogger<McpAggregator> logger)
{
    private long _calls;

    /// <summary>
    /// The view of <paramref name="token"/>: <paramref name="slug"/> null = all its bound servers, prefixed. A slug it is not bound to
    /// (or that is disabled, or does not exist) gives null, exactly like an unknown one.
    /// </summary>
    public async Task<McpView?> ViewAsync(TokenRecord token, string? slug, CancellationToken ct)
    {
        var bound = await db.Mcp.ListByIdsAsync(token.McpServerIds(), ct);
        var enabled = bound.Where(s => s.Enabled).ToList();
        if (slug is null) return new McpView(token, enabled, Prefixed: true);
        var one = enabled.FirstOrDefault(s => s.Slug == slug);
        return one is null ? null : new McpView(token, [one], Prefixed: false);
    }

    /// <summary>
    /// The tools of every server in the view, in a fixed order (slug, then tool name) so clients and prompt caches see the same list
    /// each time. A server that fails is left out and its status is updated by the pool: one broken upstream must not take the rest down.
    /// </summary>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(McpView view, CancellationToken ct)
    {
        var lists = await Task.WhenAll(view.Servers.Select(async server =>
        {
            try
            {
                return (server, tools: await pool.ListToolsAsync(server, refresh: false, ct));
            }
            catch (McpUpstreamException)
            {
                return (server, tools: (IReadOnlyList<Tool>)[]);
            }
        }));
        var result = new List<Tool>();
        foreach (var (server, tools) in lists.OrderBy(l => l.server.Slug, StringComparer.Ordinal))
            foreach (var tool in tools.OrderBy(t => t.Name, StringComparer.Ordinal))
                result.Add(view.Prefixed ? Renamed(tool, McpNames.Prefixed(server.Slug, tool.Name)) : tool);
        return result;
    }

    private static Tool Renamed(Tool tool, string name) => new()
    {
        Name = name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.InputSchema,
        OutputSchema = tool.OutputSchema,
        Annotations = tool.Annotations,
        Icons = tool.Icons,
        Meta = tool.Meta,
    };

    /// <summary>The server and the upstream's own tool name that <paramref name="name"/> means in the view, or null.</summary>
    public (McpServerRecord Server, string Tool)? Resolve(McpView view, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!view.Prefixed) return view.Servers.Count == 1 ? (view.Servers[0], name) : null;
        if (!McpNames.TrySplit(name, out var slug, out var tool)) return null;
        var server = view.Servers.FirstOrDefault(s => s.Slug == slug);
        return server is null ? null : (server, tool);
    }

    /// <summary>
    /// Calls a tool and records the call. A failure of the upstream (unreachable, needs authorization, timed out) comes back as a tool
    /// error result — the MCP convention for "the tool did not run" — with a message that never names the upstream; a tool that does not
    /// exist in the view throws <see cref="McpToolNotFoundException"/>.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(McpView view, string? name, JsonObject? arguments, string frontEra, CancellationToken ct)
    {
        var (server, tool) = Resolve(view, name) ?? throw new McpToolNotFoundException(name ?? "");
        var watch = Stopwatch.StartNew();
        CallToolResult result;
        string? category = null;
        try
        {
            result = await pool.CallToolAsync(server, tool, arguments, ct);
            if (result.IsError == true) category = McpErrorCategories.Tool;
        }
        catch (McpUpstreamException failure)
        {
            category = failure.Category;
            result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = failure.Message }] };
        }
        catch (OperationCanceledException)
        {
            await RecordAsync(view, server, tool, watch, McpErrorCategories.Timeout, frontEra);
            throw;
        }
        await RecordAsync(view, server, tool, watch, category, frontEra);
        return result;
    }

    private async Task RecordAsync(McpView view, McpServerRecord server, string tool, Stopwatch watch, string? category, string frontEra)
    {
        try
        {
            await db.Mcp.InsertCallAsync(new McpCallRecord
            {
                At = DateTimeOffset.UtcNow, TokenId = view.Token.Id, ServerId = server.Id, ServerSlug = server.Slug, Tool = tool,
                DurationMs = watch.ElapsedMilliseconds, Ok = category is null, ErrorCategory = category, FrontEra = frontEra,
            }, CancellationToken.None);
            // Housekeeping now and then, not on a timer: a gateway nobody calls has nothing to prune.
            if (Interlocked.Increment(ref _calls) % 500 == 0)
                await db.Mcp.PruneCallsAsync(DateTimeOffset.UtcNow.AddDays(-McpLimits.CallRetentionDays), CancellationToken.None);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or ObjectDisposedException)
        {
            logger.LogDebug(ex, "MCP 调用记录写入失败");
        }
    }
}
