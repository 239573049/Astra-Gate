using System.Text.Json.Nodes;
using Astra.Gateway.Http;

namespace Astra.Server.Api;

/// <summary>
/// <c>/api/tools</c>: Astra's built-in tools (web search for any model, web fetch, image generation, plugin tools). Whatever makes Astra
/// connect to an address of the caller's choosing (a search API's address, "allow private addresses", running a tool) is limited to
/// this machine, like the MCP server admin.
/// </summary>
public static class ToolsEndpoints
{
    public static void MapToolsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools").AddEndpointFilter<AdminApiErrorFilter>();
        group.MapGet("/", async (ToolsService tools, CancellationToken ct) => Results.Ok(await tools.ListToolsAsync(ct)));
        group.MapGet("/settings", async (ToolsService tools, CancellationToken ct) => Results.Ok(await tools.GetAsync(ct)));
        group.MapPut("/settings", async (HttpContext ctx, JsonObject body, ToolsService tools, CancellationToken ct) =>
        {
            if (body["fetch"] is JsonObject fetch && fetch.ContainsKey("allowPrivate")) RequireLocal(ctx);
            return Results.Ok(await tools.UpdateAsync(body, ct));
        });
        group.MapPost("/search/apis", async (HttpContext ctx, JsonObject body, ToolsService tools, CancellationToken ct) =>
        {
            if (body.ContainsKey("url")) RequireLocal(ctx);
            return Results.Ok(await tools.AddApiAsync(body, ct));
        });
        group.MapPut("/search/apis", async (JsonObject body, ToolsService tools, CancellationToken ct) => Results.Ok(await tools.ReorderApisAsync(body, ct)));
        group.MapPatch("/search/apis/{vendor}", async (HttpContext ctx, string vendor, JsonObject body, ToolsService tools, CancellationToken ct) =>
        {
            if (body.ContainsKey("url")) RequireLocal(ctx);
            return Results.Ok(await tools.UpdateApiAsync(vendor, body, ct));
        });
        group.MapDelete("/search/apis/{vendor}", async (string vendor, ToolsService tools, CancellationToken ct) => Results.Ok(await tools.DeleteApiAsync(vendor, ct)));
        group.MapPost("/search/apis/{vendor}/keys", async (string vendor, JsonObject body, ToolsService tools, CancellationToken ct) =>
            Results.Ok(await tools.AddKeyAsync(vendor, body, ct)));
        group.MapPut("/search/apis/{vendor}/keys", async (string vendor, JsonObject body, ToolsService tools, CancellationToken ct) =>
            Results.Ok(await tools.ReorderKeysAsync(vendor, body, ct)));
        group.MapPatch("/search/apis/{vendor}/keys/{id}", async (string vendor, string id, JsonObject body, ToolsService tools, CancellationToken ct) =>
            Results.Ok(await tools.UpdateKeyAsync(vendor, id, body, ct)));
        group.MapDelete("/search/apis/{vendor}/keys/{id}", async (string vendor, string id, ToolsService tools, CancellationToken ct) =>
            Results.Ok(await tools.DeleteKeyAsync(vendor, id, ct)));
        group.MapGet("/search/apis/{vendor}/quota", async (string vendor, bool? fresh, ToolsService tools, CancellationToken ct) =>
            Results.Ok(await tools.QuotasAsync(vendor, fresh == true, ct)));
        group.MapPost("/search/apis/{vendor}/test", async (HttpContext ctx, string vendor, JsonObject? body, ToolsService tools, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            return Results.Ok(await tools.TestAsync(vendor, body?["query"]?.GetValue<string>(), ct));
        });
        // Runs a tool for the CLI (`astra tool`): the web fetch and image tools reach out, so this machine only.
        group.MapPost("/run", async (HttpContext ctx, JsonObject body, ToolsService tools, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            var name = body["name"]?.GetValue<string>() ?? throw new AdminApiException(400, "name is required");
            return Results.Ok(await tools.RunAsync(ctx, name, body["args"] as JsonObject, body["token"]?.GetValue<string>(), ct));
        });
    }

    private static void RequireLocal(HttpContext ctx)
    {
        if (!RequestOrigin.IsLocal(ctx)) throw new AdminApiException(403, "This can only be done on the machine Astra runs on");
    }
}
