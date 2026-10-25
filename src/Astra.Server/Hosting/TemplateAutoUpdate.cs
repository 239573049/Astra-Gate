using Astra.Data;
using Astra.Providers.Templates;
using Astra.Server.Api;

namespace Astra.Server.Hosting;

/// <summary>
/// At start, brings providers whose template is marked <c>autoUpdate</c> (GitHub Copilot) to the template's current
/// version, so a new default endpoint, protocol or model reaches existing providers without a visit to the console.
/// Same merge as <c>POST /api/providers/{id}/template-update</c>: what the user changed is kept.
/// </summary>
public sealed class TemplateAutoUpdate(AstraDatabase db, ProviderTemplateCatalog catalog, ILogger<TemplateAutoUpdate> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "模板自动更新失败");
        }
    }

    /// <summary>One pass over the providers; a provider that cannot be updated is logged and skipped.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        foreach (var provider in await db.Providers.ListAsync(ct))
        {
            if (provider.TemplateId is null || catalog.Get(provider.TemplateId) is not { AutoUpdate: true, Version: var latest }
                || latest <= (provider.TemplateVersion ?? 0))
                continue;
            try
            {
                await ProviderEndpoints.ApplyTemplateUpdateAsync(db, provider, catalog, ct);
                logger.LogInformation("提供商 {Provider} 已自动更新到模板版本 {Version}", provider.Name, latest);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "提供商 {Provider} 的模板自动更新失败", provider.Name);
            }
        }
    }
}
