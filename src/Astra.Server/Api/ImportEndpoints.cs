namespace Astra.Server.Api;

public static class ImportEndpoints
{
    /// <summary>
    /// Mapped onto the <c>/api/providers</c> group (see <c>ProviderEndpoints</c>) so the import shares that group's
    /// error filter and write lock. Both calls need the <c>X-Astra-Admin</c> header like every other admin call.
    /// </summary>
    public static void MapImportEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/import/sources", async (string? source, ImportService import, CancellationToken ct) =>
            Results.Ok(await import.ScanAsync(source, ct)));
        group.MapPost("/import", async (List<ImportSelectionDto> selections, ImportService import, CancellationToken ct) =>
            Results.Ok(await import.ImportAsync(selections, ct)));
    }
}
