using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Core.Models;

/// <summary>
/// One row of the model catalog table (<c>ModelRow</c> of web/src/pages/ModelsPage.tsx): the sortable values plus the model they
/// came from. The base input price is a decimal, so the "Input / output" column sorts by the exact price.
/// </summary>
/// <param name="Id">Catalog id (the route parameter of the detail page).</param>
/// <param name="Name">Display name.</param>
/// <param name="Vendor">Vendor key.</param>
/// <param name="Context">Context window in tokens (0 when unknown, like the web's <c>?? 0</c>).</param>
/// <param name="Input">Base input price per 1M tokens (0 when unpriced).</param>
/// <param name="ProviderPrices">How many AI providers carry a price of their own for the model.</param>
/// <param name="Model">The API object.</param>
public sealed record ModelRow(string Id, string Name, string Vendor, long Context, decimal Input, int ProviderPrices, ModelDto Model)
{
    /// <summary>Builds the row for one catalog entry.</summary>
    public static ModelRow From(ModelDto m) => new(
        m.Id, m.DisplayName, m.Vendor, m.ContextWindow ?? 0,
        PriceNumber.FromNode((m.Pricing as JsonObject)?["base"] is JsonObject b ? b["input"] : null) ?? 0,
        m.ProviderPriceKeys.Count, m);

    /// <summary>The rows of a list, in the server's order.</summary>
    public static List<ModelRow> From(IEnumerable<ModelDto> models) => models.Select(From).ToList();

    /// <summary>The model is disabled (its name is drawn muted).</summary>
    public bool IsDisabled => !Model.Enabled;

    /// <summary>A user-created model (shows the "custom" badge).</summary>
    public bool IsUserModel => Model.Source == "user";

    /// <summary>The user changed fields of a seeded model (shows the "modified" badge).</summary>
    public bool IsModified => Model.UserModifiedFields.Count > 0;

    /// <summary>"1.05M" / "200K" / "—".</summary>
    public string ContextText => PriceFormat.Context(Model.ContextWindow);

    /// <summary>"$3 / $15" of the base prices, or a dash.</summary>
    public string PriceText => Model.Pricing is JsonObject doc && doc["base"] is JsonObject b ? $"{PriceFormat.UnitPrice(b["input"])} / {PriceFormat.UnitPrice(b["output"])}" : PriceFormat.Dash;

    /// <summary>"3 providers" or a dash.</summary>
    public string ProviderPricesText => ProviderPrices > 0 ? Loc.T("models.providerPriceCount", ("count", ProviderPrices)) : PriceFormat.Dash;
}

/// <summary>Catalog helpers of the models page.</summary>
public static class ModelCatalog
{
    /// <summary>The vendor filter's options: every vendor of the unfiltered list, once, in code-unit order (JS <c>[...new Set(...)].sort()</c>).</summary>
    public static List<string> Vendors(IEnumerable<ModelDto>? all)
        => (all ?? []).Select(m => m.Vendor).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The query path of the list (web <c>useModels</c>: <c>search</c> and <c>vendor</c> only when set).</summary>
    public static string ListPath(string? search, string? vendor)
        => "/api/models" + Astra.Desktop.Core.Api.ApiClient.Qs(("search", search), ("vendor", vendor));

    /// <summary>The detail path of a model id.</summary>
    public static string DetailPath(string id) => "/api/models/" + Astra.Desktop.Core.Api.ApiClient.Enc(id);
}
