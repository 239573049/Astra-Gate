namespace Astra.Server.Api;

/// <summary>POST /api/models/sync/preview and /api/models/sync/apply (models.dev catalog sync).</summary>
public static class ModelSyncEndpoints
{
    public static void MapModelSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/models/sync").AddEndpointFilter<AdminApiErrorFilter>();

        g.MapPost("/preview", async (ModelSyncService sync, CancellationToken ct) =>
            Results.Ok(await sync.PreviewAsync(ct)));

        g.MapPost("/apply", async (HttpRequest request, ModelSyncService sync, CancellationToken ct) =>
        {
            var canHaveBody = request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>()?.CanHaveBody;
            if (canHaveBody == false || request.ContentLength == 0) return await sync.ApplyAsync(null, ct);
            if (!request.HasJsonContentType()) throw new AdminApiException(400, "A JSON request body with changeIds is required");
            return await sync.ApplyAsync(await request.ReadFromJsonAsync<SyncApplyRequest>(ct), ct);
        });
    }
}
