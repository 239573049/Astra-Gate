using System.Text.Json.Nodes;
using Astra.Core.Mcp;
using Astra.Data;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;

namespace Astra.Gateway.Mcp;

/// <summary>Who a built-in tool runs for: the request that carried the call (its token is what an image generation is billed to).</summary>
/// <param name="Origin">The MCP request, or the admin request of <c>POST /api/tools/run</c>; null in tests.</param>
/// <param name="TokenKey">A gateway key to send the call as (an admin run without a token of its own); null: the origin's own credentials.</param>
public sealed record BuiltInCall(HttpContext? Origin, string? TokenKey = null);

/// <summary>
/// Astra's own tools (<c>web_search</c>, <c>web_fetch</c>, <c>generate_image</c>, the tools provider plugins give) served as one MCP server,
/// slug <see cref="McpBuiltIn.Slug"/>. It is an ordinary <c>mcp_servers</c> row (so a token binds it, the MCP page lists it and "apply to
/// clients" writes it) whose address is <see cref="McpBuiltIn.Url"/>: the aggregator answers it here instead of connecting anywhere.
/// </summary>
public interface IMcpBuiltIn
{
    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct);

    /// <summary>Runs a tool by the name <see cref="ListToolsAsync"/> gave. An unknown name is an <see cref="McpToolNotFoundException"/>; a failure of the tool is an error result.</summary>
    Task<CallToolResult> CallAsync(string tool, JsonObject? arguments, BuiltInCall call, CancellationToken ct);
}

/// <summary>The built-in server's identity.</summary>
public static class McpBuiltIn
{
    public const string Id = "builtin-astra";
    public const string Slug = "astra";
    public const string Url = "astra://builtin";

    public static bool Is(McpServerRecord server) => server.Id == Id || server.Url == Url;

    /// <summary>The row that stands for the built-in server (created once, at startup).</summary>
    public static McpServerRecord NewRecord(DateTimeOffset now) => new()
    {
        Id = Id, Slug = Slug, Name = "Astra Tools", Transport = McpTransports.Http, Url = Url, AuthType = McpAuthTypes.None, Enabled = true,
        TimeoutSeconds = 120, Status = McpServerStatuses.Ok, CreatedAt = now, UpdatedAt = now,
    };

    /// <summary>Creates the built-in server's row when it is missing (a user's own server that already took the slug leaves it out).</summary>
    public static async Task EnsureAsync(AstraDatabase db, CancellationToken ct = default)
    {
        if (await db.Mcp.GetAsync(Id, ct) is not null) return;
        if (await db.Mcp.SlugTakenAsync(Slug, null, ct)) return;
        await db.Mcp.InsertAsync(NewRecord(DateTimeOffset.UtcNow), ct);
    }
}
