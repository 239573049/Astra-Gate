using System.Text.Json.Nodes;

namespace Astra.Server.Api;

public static class ClientEndpoints
{
    /// <summary>accountId semantics: omitted = keep the current pin, "" = clear it, an id = pin that account.</summary>
    public sealed record BindingInput(string? ProviderId, string? AccountId);

    /// <summary>The client's full provider list in routing order (first = primary).</summary>
    public sealed record BindingsInput(List<ClientBindingDto>? Bindings);

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
        group.MapPut("/{kind}/bindings", async (string kind, BindingsInput body, ClientService clients, CancellationToken ct) =>
            Results.Ok(await clients.SetBindingsAsync(kind, body.Bindings ?? [], ct)));
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
        // Materialized to List<string>: an interface-typed root makes the serializer probe the interface graph.
        group.MapGet("/{kind}/models", async (string kind, ClientService clients, CancellationToken ct) =>
            Results.Ok((await clients.ModelsAsync(kind, ct)).ToList()));

        // Versions, update checks and installs (ClientInstallService). check-updates fetches the latest versions
        // from the npm registry (cached for six hours unless forced) and returns the refreshed client list.
        group.MapPost("/check-updates", async (ClientUpdateCheckRequest? body, ClientInstallService installs, ClientService clients, CancellationToken ct) =>
        {
            await installs.CheckUpdatesAsync(body?.Force ?? false, ct);
            return Results.Ok((await clients.ListAsync(ct)).ToList());
        });
        group.MapPost("/{kind}/install", (string kind, ClientInstallRequest body, ClientInstallService installs) =>
            Results.Ok(installs.Start(kind, body.Action, body.Elevated ?? false)));
        group.MapGet("/{kind}/install", (string kind, ClientInstallService installs) => Results.Ok(installs.Get(kind)));
        group.MapPost("/{kind}/install/cancel", (string kind, ClientInstallService installs) => Results.Ok(installs.Cancel(kind)));
    }
}
