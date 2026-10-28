using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Providers;

/// <summary>The state pill of a provider card (web <c>state</c>).</summary>
public enum CardState
{
    /// <summary>Switched off (neutral dot).</summary>
    Disabled,

    /// <summary>Enabled but a key is required and missing (orange dot).</summary>
    NoKey,

    /// <summary>Enabled and usable (green dot).</summary>
    Active,
}

/// <summary>
/// What one provider card shows, computed from the DTO (ProvidersPage.tsx <c>ProviderCard</c>): title, protocol line, credential text,
/// state pill, price line and bound-client slots. Pure and testable; the view only draws it.
/// </summary>
public sealed record ProviderCardModel(
    ProviderDto Provider, string Title, string Description, bool MissingKey, string Credential, bool CredentialMono, CardState State,
    string StateLabel, string? Pricing, string? Host, string ModelCountText, int MaxClients, IReadOnlyList<string> VisibleClients,
    IReadOnlyList<string> OverflowClients, long TotalRequests, int[] Requests)
{
    /// <summary>Builds the card model for <paramref name="p"/> with its (possibly missing) last-week usage.</summary>
    public static ProviderCardModel From(ProviderDto p, ProviderUsage? usage, int days = ProviderUsageBuckets.Days)
    {
        var protocols = p.Endpoints.Select(e => e.Protocol.ToId()).Distinct().ToList();
        var auth = p.AuthScheme;
        var missingKey = !p.HasApiKey && auth is not ("none" or "oauth-subscription" or "plugin");
        var pricing = string.Join(" · ", new[]
        {
            string.IsNullOrEmpty(p.PriceKey) ? null : Loc.T("providers.priceKeyShort", ("key", p.PriceKey)),
            p.PriceMultiplier != 1 ? "×" + p.PriceMultiplier.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture) : null,
        }.Where(x => x is not null));
        var credential = auth switch
        {
            "plugin" => Loc.T("providers.card.plugin"),
            "oauth-subscription" => Loc.T("providers.card.subscription"),
            "none" => Loc.T("providers.card.noAuth"),
            _ => p.HasApiKey ? (p.ApiKeyMasked ?? "••••") : Loc.T("providers.noKey"),
        };
        var (state, label) = !p.Enabled ? (CardState.Disabled, Loc.T("providers.card.disabled"))
            : missingKey ? (CardState.NoKey, Loc.T("providers.noKey"))
            : (CardState.Active, Loc.T("providers.card.active"));
        var max = p.TemplateUpdateAvailable ? 5 : 8;
        var requests = usage?.Requests ?? new int[days];
        return new ProviderCardModel(
            p, p.Name, string.Join(" / ", protocols.Select(ProviderProtocols.Name)), missingKey, credential,
            p.HasApiKey && auth != "oauth-subscription", state, label, pricing.Length > 0 ? pricing : null,
            ProviderFormat.HostOf(p.Endpoints.FirstOrDefault()?.BaseUrl), Loc.T("providers.modelCount", ("count", p.ModelCount)), max,
            p.BoundClients.Take(max).ToList(), p.BoundClients.Skip(max).ToList(), requests.Sum(r => (long)r), requests);
    }
}
