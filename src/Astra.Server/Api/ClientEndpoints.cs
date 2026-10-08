using System.Text.Json.Nodes;

namespace Astra.Server.Api;

public static class ClientEndpoints
{
    /// <summary>accountId semantics: omitted = keep the current pin, "" = clear it, an id = pin that account.</summary>
    public sealed record BindingInput(string? ProviderId, string? AccountId);

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clients").AddEndpointFilter<AdminApiErrorFilter>();
        // Materialized to List<ClientInfoDto>: an interface-typed root makes the serializer probe the
        // interface graph for metadata (see ServerJsonContext).
        group.MapGet("", async (ClientService clients, CancellationToken ct) => Results.Ok((await clients.ListAsync(ct)).ToList()));
        // Plan §7.1: after the gateway address changed, rewrite every enabled client that still points at the old one.
        group.MapPost("/reapply", async (ClientService clients, CancellationToken ct) =>
            Results.Ok(new ReappliedDto(await clients.ReapplyOutdatedAsync(ct))));
        group.MapPut("/{kind}/binding", async (string kind, BindingInput body, ClientService clients, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.ProviderId)) return ModelEndpoints.Bad("providerId is required");
            return Results.Ok(await clients.SetBindingAsync(kind, body.ProviderId, body.AccountId, ct));
        });
        group.MapPost("/{kind}/preview-enable", async (string kind, JsonObject body, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.PreviewAsync(kind, body, ct)));
        group.MapPost("/{kind}/enable", async (string kind, JsonObject body, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.EnableAsync(kind, body, ct)));
        group.MapPost("/{kind}/disable", async (string kind, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.DisableAsync(kind, false, ct)));
        group.MapPost("/{kind}/force-restore", async (string kind, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.DisableAsync(kind, true, ct)));
        // Materialized to List<ClientBackupDto>: an interface-typed root makes the serializer probe the
        // interface graph for metadata (see ServerJsonContext).
        group.MapGet("/{kind}/backups", (string kind, ClientService clients) => Results.Ok(clients.Backups(kind).ToList()));
        group.MapPost("/{kind}/backups/{backupId}/restore", async (string kind, string backupId, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.RestoreBackupAsync(kind, backupId, ct)));
        group.MapGet("/{kind}/models", async (string kind, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.ModelsAsync(kind, ct)));
    }
}
