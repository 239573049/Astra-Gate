using System.Security.Cryptography;
using System.Text;
using Astra.Clients.Config;
using Astra.Clients.Import;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Import;
using Astra.Core.Models;
using Astra.Data;
using Astra.Data.Foreign;
using Astra.Server.Hosting;
using Astra.Providers.Templates;

namespace Astra.Server.Api;

public sealed record ImportEndpointDto(ApiProtocol Protocol, string BaseUrl);
public sealed record ImportExistingDto(string Id, string Name);

/// <summary>A candidate as the UI sees it. Never carries the API key: only a mask and a short fingerprint.</summary>
public sealed record ImportCandidateDto(
    string Ref, string Source, string Name, string? FromApp, List<ImportEndpointDto> Endpoints, string AuthScheme,
    bool HasKey, string? KeyMasked, string? KeyFingerprint, List<string> Models, List<string> Headers, string? TemplateId,
    string Status, ImportExistingDto? Existing, bool Off, string? SkipReason, List<string> IgnoredFields);

public sealed record ImportSourceDto(string Id, string Name, string? Path, bool Found, string? Error, List<ImportCandidateDto> Items);
public sealed record ImportSelectionDto(string Source, string Ref);
public sealed record ImportedProviderDto(string Id, string Name);
public sealed record ImportSkippedDto(string Ref, string Reason);
public sealed record ImportResultDto(List<ImportedProviderDto> Added, List<ImportSkippedDto> Skipped);

/// <summary>
/// Provider import (read other apps' provider lists, then create providers). Two steps: <see cref="ScanAsync"/> only
/// previews (no plaintext key leaves this class), and <see cref="ImportAsync"/> re-reads the sources and creates the
/// selected providers, so keys never travel through the browser. Nothing here talks to the network or writes to
/// another application's files.
/// </summary>
public sealed class ImportService(
    AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, ClientEnvironment env, ServerOptions server,
    ILogger<ImportService> log)
{
    private ImportSourceRegistry Registry() => ImportSourceRegistry.CreateDefault(
        new ImportContext(env, new ForeignSqliteOpener(), db.ClientConfigState, IsGatewayUrl));

    /// <summary>True when the URL is this Astra gateway (loopback host on the server's own port).</summary>
    private bool IsGatewayUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Port == server.Port &&
        (EndpointNormalizer.IsLoopback(url) || string.Equals(uri.Host, server.Host, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ preview

    public async Task<List<ImportSourceDto>> ScanAsync(string? only, CancellationToken ct)
    {
        var registry = Registry();
        var results = await Task.Run(() => only is null ? registry.ReadAll() : [registry.Read(only) ?? throw new AdminApiException(404, "Unknown import source")], ct);
        var existing = await LoadExistingAsync(ct);
        var dtos = new List<ImportSourceDto>();
        foreach (var r in results)
        {
            var items = r.Items.Select(c => ToDto(c, existing)).ToList();
            log.LogInformation("Import preview: source {Source} found={Found} candidates={Count}", r.Id, r.Found, items.Count);
            dtos.Add(new ImportSourceDto(r.Id, r.Name, r.Path, r.Found, r.Error, items));
        }
        return dtos;
    }

    private ImportCandidateDto ToDto(ImportCandidate c, List<ExistingProvider> existing)
    {
        var hash = KeyHash(c.ApiKey);
        var sameAddress = existing.Where(p => p.Urls.Overlaps(c.Endpoints.Select(e => e.BaseUrl.ToLowerInvariant()))).ToList();
        var same = sameAddress.FirstOrDefault(p => p.KeyHash == hash);
        var status = c.SkipReason is not null ? "skip" : same is not null ? "same" : c.Off ? "off" : sameAddress.Count > 0 ? "sameHost" : "new";
        var shown = same ?? sameAddress.FirstOrDefault();
        // What the commit would drop, so the preview can say so up front.
        var ignored = c.IgnoredFields.Concat(c.Headers.Where(h => !ProviderEndpoints.IsValidExtraHeader(h.Key, h.Value)).Select(h => $"header:{h.Key}"))
            .Concat(c.Proxy is not null && !ValidProxy(c.Proxy) ? ["proxy"] : []).Distinct().ToList();
        return new ImportCandidateDto(
            c.Ref, c.Source, c.Name, c.FromApp, c.Endpoints.Select(e => new ImportEndpointDto(e.Protocol, e.BaseUrl)).ToList(),
            c.AuthScheme, c.ApiKey is not null, Mask(c.ApiKey), hash?[..8], c.Models, c.Headers.Keys.Order().ToList(),
            MatchTemplate(c)?.Id, status, shown is null ? null : new ImportExistingDto(shown.Id, shown.Name), c.Off, c.SkipReason, ignored);
    }

    // ------------------------------------------------------------------ commit

    /// <summary>Creates providers for the selected candidates. A source that changed since the preview rejects the whole batch.</summary>
    public async Task<ImportResultDto> ImportAsync(IReadOnlyList<ImportSelectionDto> selections, CancellationToken ct)
    {
        if (selections.Count == 0) throw new AdminApiException(400, "Nothing selected");
        var registry = Registry();
        var bySource = new Dictionary<string, Dictionary<string, ImportCandidate>>(StringComparer.Ordinal);
        foreach (var source in selections.Select(s => s.Source).Distinct())
        {
            var result = await Task.Run(() => registry.Read(source), ct) ?? throw new AdminApiException(404, "Unknown import source");
            bySource[source] = result.Items.GroupBy(i => i.Ref).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        var chosen = new List<ImportCandidate>();
        foreach (var selection in selections.DistinctBy(s => (s.Source, s.Ref)))
        {
            if (!bySource[selection.Source].TryGetValue(selection.Ref, out var candidate))
                throw new AdminApiException(409, "来源已变化，请重新打开导入");
            chosen.Add(candidate);
        }

        var existing = await LoadExistingAsync(ct);
        var providers = await db.Providers.ListAsync(ct);
        var names = new HashSet<string>(providers.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var order = providers.Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1;

        var added = new List<ImportedProviderDto>();
        var skipped = new List<ImportSkippedDto>();
        foreach (var c in chosen)
        {
            if (c.SkipReason is not null) { skipped.Add(new ImportSkippedDto(c.Ref, c.SkipReason)); continue; }
            var hash = KeyHash(c.ApiKey);
            var urls = c.Endpoints.Select(e => e.BaseUrl.ToLowerInvariant());
            if (existing.Any(p => p.KeyHash == hash && p.Urls.Overlaps(urls))) { skipped.Add(new ImportSkippedDto(c.Ref, "same")); continue; }

            var provider = Build(c, UniqueName(c.Name, names), order++);
            var template = MatchTemplate(c) ?? catalog.Get(CustomTemplateId(c.Endpoints[0].Protocol));
            var modelIds = c.Models.Concat(template?.Models ?? []).Distinct(StringComparer.Ordinal).Take(2000).ToList();
            ProviderEndpoints.Validate(provider);
            ProviderEndpoints.ValidateModelIds(modelIds);
            var models = await ProviderEndpoints.PlanModelsAsync(db, provider, modelIds, ct);
            await db.Providers.InsertWithModelsAsync(provider, models, ct);
            existing.Add(new ExistingProvider(provider.Id, provider.Name, hash, [.. urls]));
            added.Add(new ImportedProviderDto(provider.Id, provider.Name));
        }
        log.LogInformation("Import: {Added} provider(s) created, {Skipped} skipped from {Sources}", added.Count, skipped.Count, string.Join(",", bySource.Keys));
        return new ImportResultDto(added, skipped);
    }

    private Provider Build(ImportCandidate c, string name, int sortOrder)
    {
        var template = MatchTemplate(c);
        var baseTemplate = template ?? catalog.Get(CustomTemplateId(c.Endpoints[0].Protocol))!;
        var protocols = c.Endpoints.Select(e => e.Protocol).ToList();
        var preferred = baseTemplate.PreferredUpstreamProtocols.Where(protocols.Contains).ToList();
        var ignored = c.IgnoredFields;

        var headers = new Dictionary<string, string>(baseTemplate.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in c.Headers)
        {
            if (ProviderEndpoints.IsValidExtraHeader(key, value)) headers[key] = value;
            else ignored.Add($"header:{key}");
        }
        var proxy = c.Proxy;
        if (proxy is not null && !ValidProxy(proxy))
        {
            proxy = null;
            ignored.Add("proxy");
        }
        var website = c.Website ?? baseTemplate.Website;
        var keyless = c.ApiKey is null && EndpointNormalizer.IsLoopback(c.Endpoints[0].BaseUrl);
        return new Provider
        {
            Id = Ulid.NewUlid(), TemplateId = baseTemplate.Id, TemplateVersion = baseTemplate.Version,
            TemplateSnapshotJson = ProviderEndpoints.Snapshot(baseTemplate, null), Name = name, Icon = baseTemplate.Icon,
            Category = baseTemplate.Category, Endpoints = c.Endpoints.Select(e => new ProviderEndpoint { Protocol = e.Protocol, BaseUrl = e.BaseUrl }).ToList(),
            PreferredUpstreamProtocols = preferred.Count > 0 ? preferred : protocols.Distinct().ToList(),
            AuthScheme = keyless ? AuthSchemes.None : c.AuthScheme, ApiKeyEnc = c.ApiKey is null ? null : secrets.Protect(c.ApiKey),
            ExtraHeaders = headers, HttpProxy = proxy, PriceKey = baseTemplate.PriceKey ?? baseTemplate.Id,
            PriceMultiplier = baseTemplate.DefaultPriceMultiplier, AdapterId = baseTemplate.Adapter, Settings = (System.Text.Json.Nodes.JsonObject)baseTemplate.Settings.DeepClone(),
            Website = website, Notes = $"导入自 {SourceName(c.Source)} · {DateTime.UtcNow:yyyy-MM-dd}", SortOrder = sortOrder, Enabled = true,
        };
    }

    private static bool ValidProxy(string proxy) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var p) && p.Scheme is "http" or "https" or "socks5";

    private static string SourceName(string id) => id switch
    {
        ImportSourceIds.CcSwitch => "CC Switch", ImportSourceIds.Alma => "Alma", ImportSourceIds.ClaudeCode => "Claude Code",
        ImportSourceIds.Codex => "Codex", ImportSourceIds.Magpie => "Magpie", _ => id,
    };

    // ------------------------------------------------------------------ helpers

    /// <summary>A catalog template whose endpoint authority (host:port) is one of the candidate's; subscription templates never match.</summary>
    private ProviderTemplate? MatchTemplate(ImportCandidate c)
    {
        var authorities = c.Endpoints.Select(e => Authority(e.BaseUrl)).Where(a => a is not null).ToHashSet();
        if (authorities.Count == 0) return null;
        return catalog.All.FirstOrDefault(t => t.Category is not ("subscription" or "custom") &&
            t.Endpoints.Any(e => authorities.Contains(Authority(e.BaseUrl))));
    }

    private static string? Authority(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host.ToLowerInvariant()}:{u.Port}" : null;

    private static string CustomTemplateId(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIResponses => "custom-responses",
        ApiProtocol.Anthropic => "custom-anthropic",
        ApiProtocol.Gemini => "custom-gemini",
        _ => "custom-chat",
    };

    private static string UniqueName(string name, HashSet<string> taken)
    {
        var candidate = name;
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{name} ({n})";
        return candidate;
    }

    private sealed record ExistingProvider(string Id, string Name, string? KeyHash, HashSet<string> Urls);

    private async Task<List<ExistingProvider>> LoadExistingAsync(CancellationToken ct)
    {
        var result = new List<ExistingProvider>();
        foreach (var p in await db.Providers.ListAsync(ct))
        {
            string? key = null;
            if (p.ApiKeyEnc is not null)
            {
                try { key = secrets.Unprotect(p.ApiKeyEnc); }
                catch (CryptographicException) { key = null; }
            }
            result.Add(new ExistingProvider(p.Id, p.Name, KeyHash(key), p.Endpoints.Select(e => e.BaseUrl.TrimEnd('/').ToLowerInvariant()).ToHashSet()));
        }
        return result;
    }

    /// <summary>SHA-256 hex of a key (null for none). Only hashes are ever compared, never plaintext.</summary>
    private static string? KeyHash(string? key) =>
        key is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string? Mask(string? key) => key is null ? null : key.Length >= 12 ? key[..3] + "…" + key[^4..] : "••••";
}
