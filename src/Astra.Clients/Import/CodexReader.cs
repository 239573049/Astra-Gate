using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// Codex: custom <c>[model_providers.*]</c> tables of <c>$CODEX_HOME/config.toml</c> that carry an inline
/// <c>experimental_bearer_token</c>. A table that only names an <c>env_key</c> is reported as skipped (environment
/// variables are never read), and so is Astra's own table. <c>auth.json</c> (the ChatGPT login) is never read.
/// </summary>
public sealed class CodexReader(ImportContext ctx) : IImportSource
{
    public string Id => ImportSourceIds.Codex;
    public string Name => "Codex";

    public ImportSourceResult Read()
    {
        var dir = ctx.Env.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } d ? d : ctx.Env.Combine(".codex");
        var file = Path.Combine(dir, "config.toml");
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = file };
        var text = ctx.Env.ReadTextOrNull(file);
        if (text is null) return result;
        result.Found = true;
        var config = CodexConfig.Parse(text);
        if (config is null)
        {
            result.Error = "config.toml is not valid TOML";
            return result;
        }
        var catalogModels = CatalogModels(config.ModelCatalogPath(), dir);
        foreach (var p in config.Providers())
        {
            var c = new ImportCandidate { Ref = $"{Id}:{p.Key}", Source = Id, Name = p.Name ?? p.Key, FromApp = "codex", ApiKey = p.BearerToken };
            if (p.BaseUrl is not null)
                c.Endpoints.Add(new ImportEndpoint(p.WireApi == "chat" ? ApiProtocol.OpenAIChat : ApiProtocol.OpenAIResponses, p.BaseUrl));
            foreach (var (k, v) in p.Headers) c.Headers[k] = v;
            c.Models.AddRange(config.ModelsFor(p.Key));
            c.Models.AddRange(catalogModels);
            if (p.Key == CodexClientAdapterProviderId) c.SkipReason = ImportSkipReasons.PointsToAstra;
            else if (p.BaseUrl is null) c.SkipReason = ImportSkipReasons.NoBaseUrl;
            else if (p.BearerToken is null) c.SkipReason = ImportSkipReasons.NoInlineKey;
            result.Items.Add(c);
        }
        return result;
    }

    /// <summary>The table Astra writes into Codex's config (<c>CodexClientAdapter.ProviderId</c>).</summary>
    private const string CodexClientAdapterProviderId = Adapters.CodexClientAdapter.ProviderId;

    /// <summary>Model ids listed in the JSON file <c>model_catalog_json</c> points at, when there is one.</summary>
    private List<string> CatalogModels(string? path, string codexDir)
    {
        if (path is null) return [];
        var full = Path.IsPathRooted(path) ? path : Path.Combine(codexDir, path);
        var node = ImportJson.Parse(ctx.Env.ReadTextOrNull(full));
        return ImportJson.ModelIds(node is JsonObject o && o["models"] is JsonArray models ? models : node);
    }
}
