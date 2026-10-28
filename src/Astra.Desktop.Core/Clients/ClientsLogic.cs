using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Core.Clients;

/// <summary>Where a client sits in the list (web <c>ClientGroup</c>): found on this machine, not found, or not offered yet.</summary>
public enum ClientGroup
{
    /// <summary>Detected, probed, or managed by Astra.</summary>
    Installed,

    /// <summary>Nothing found.</summary>
    NotInstalled,

    /// <summary><c>availability == "coming_soon"</c>.</summary>
    ComingSoon,
}

/// <summary>The status badge of a client's header row (web <c>statusBadge</c>).</summary>
public enum ClientStatusBadge
{
    /// <summary>"coming soon" (neutral).</summary>
    ComingSoon,

    /// <summary>"drifted" (orange): the user changed a value Astra wrote.</summary>
    Drifted,

    /// <summary>"enabled" (green).</summary>
    Enabled,

    /// <summary>"disabled" (neutral).</summary>
    Disabled,
}

/// <summary>One group of the client list with its rows in display order.</summary>
public sealed record ClientListGroup(ClientGroup Group, IReadOnlyList<ClientInfoDto> Items)
{
    /// <summary>i18n key of the heading (<c>clients.group.*</c>).</summary>
    public string TitleKey => Group switch
    {
        ClientGroup.Installed => "clients.group.installed",
        ClientGroup.NotInstalled => "clients.group.notInstalled",
        _ => "clients.group.comingSoon",
    };
}

/// <summary>
/// The list rules of web/src/pages/ClientsPage.tsx (<c>orderOf</c>, <c>groupOf</c>, <c>versionOf</c>, <c>statusBadge</c>, the selected kind,
/// the token / provider rows' texts and <c>MODEL_LIST_CLIENTS</c>). Pure functions over the Contracts DTOs, so they are unit tested.
/// </summary>
public static class ClientsLogic
{
    /// <summary>The kind shown when the route names none (or one the list does not hold): the web falls back to Codex.</summary>
    public const string DefaultKind = "codex";

    /// <summary>Clients whose config lists the bound provider's models (<c>ClientKinds.WithModelList</c> on the server; web <c>MODEL_LIST_CLIENTS</c>).</summary>
    public static readonly IReadOnlySet<string> ModelListClients = new HashSet<string>(StringComparer.Ordinal)
    {
        "opencode", "pi", "minimax-code", "copilot-cli", "vscode-copilot",
        "crush", "qwen-code", "droid", "kimi-code", "zed", "vscode-insiders", "vscodium", "omp", "mimo-code", "deepseek-harness", "workbuddy", "workbuddy-ai", "nextcowork",
        "paseo", "zcode", "tkcode", "muse-code", "codex", "grok-build", "hermes-agent",
    };

    /// <summary>The built-in kinds in their order (<see cref="ClientCatalog.Order"/>), then the agent plugins' clients by name (web <c>orderOf</c>).</summary>
    public static List<string> OrderOf(IEnumerable<ClientInfoDto>? clients)
    {
        var plugins = (clients ?? [])
            .Where(c => !ClientCatalog.Order.Contains(c.Kind))
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .Select(c => c.Kind);
        return [.. ClientCatalog.Order, .. plugins];
    }

    /// <summary>The server words this warning in English (<c>ClientService</c>); it is shown in the UI language.</summary>
    public static string WarningText(string warning, Translate t)
        => warning.StartsWith("Client configuration differs from the recorded Astra configuration", StringComparison.Ordinal) ? t("clients.warning.drifted") : warning;

    /// <summary>
    /// The group of a client (web <c>groupOf</c>): an enabled client has a config Astra wrote, so it counts as installed even when
    /// detection missed the binary.
    /// </summary>
    public static ClientGroup GroupOf(ClientInfoDto c)
    {
        if (c.Availability == "coming_soon") return ClientGroup.ComingSoon;
        return c.Detection.Installed || c.Enabled || c.Install?.Installed == true ? ClientGroup.Installed : ClientGroup.NotInstalled;
    }

    /// <summary>The installed version: probed from the client itself, else what detection reported (web <c>versionOf</c>).</summary>
    public static string? VersionOf(ClientInfoDto c) => c.Install?.Version ?? c.Detection.Version;

    /// <summary>The list's groups (installed, not installed, coming soon), each in <see cref="OrderOf"/>; empty groups are left out.</summary>
    public static List<ClientListGroup> Groups(IReadOnlyList<ClientInfoDto>? clients)
    {
        var byKind = (clients ?? []).GroupBy(c => c.Kind).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var order = OrderOf(clients);
        var result = new List<ClientListGroup>();
        foreach (var group in new[] { ClientGroup.Installed, ClientGroup.NotInstalled, ClientGroup.ComingSoon })
        {
            var items = order.Select(k => byKind.GetValueOrDefault(k)).Where(c => c is not null && GroupOf(c) == group).Select(c => c!).ToList();
            if (items.Count > 0) result.Add(new ClientListGroup(group, items));
        }
        return result;
    }

    /// <summary>The kind the page shows for the route parameter: the parameter when the list holds it, else <see cref="DefaultKind"/>.</summary>
    public static string SelectedKind(string? param, IEnumerable<ClientInfoDto>? clients)
        => param is not null && OrderOf(clients).Contains(param) ? param : DefaultKind;

    /// <summary>Shadowed: the latest version is already installed behind the copy that runs (PATH needs fixing, not an update).</summary>
    public static bool IsShadowed(ClientInfoDto c) => c.Install?.Blocker == "shadowed";

    /// <summary>The version an update would bring, or null (none available, or shadowed).</summary>
    public static string? UpdateOf(ClientInfoDto c)
        => c.Install is { UpdateAvailable: true } inst && !IsShadowed(c) ? inst.LatestVersion : null;

    /// <summary>The header badge (web <c>statusBadge</c>).</summary>
    public static ClientStatusBadge StatusOf(ClientInfoDto c)
    {
        if (c.Availability == "coming_soon") return ClientStatusBadge.ComingSoon;
        if (c.Status == "drifted") return ClientStatusBadge.Drifted;
        return c.Enabled ? ClientStatusBadge.Enabled : ClientStatusBadge.Disabled;
    }

    /// <summary>How many clients an update check found (the toast after "check for updates"); shadowed ones do not count.</summary>
    public static int UpdatesFound(IEnumerable<ClientInfoDto> list) => list.Count(c => c.Install?.UpdateAvailable == true && !IsShadowed(c));

    /// <summary>The header row's detail line: "detected v · config path", "not detected", or why it is not offered yet.</summary>
    public static string HeaderDetail(ClientInfoDto c, bool directMode, Translate t)
    {
        if (c.Availability == "coming_soon") return c.AvailabilityReason ?? t("clients.comingSoonDetail");
        var parts = new List<string>
        {
            GroupOf(c) == ClientGroup.Installed ? t("clients.detected", ("version", VersionOf(c) ?? "")) : t("clients.notDetected"),
        };
        var path = c.Detection.ConfigPaths.Count > 0 ? c.Detection.ConfigPaths[0] : null;
        if (!directMode && !string.IsNullOrEmpty(path)) parts.Add(path);
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    /// <summary>A provider row's detail: price key (else template), model count, and the price multiplier when not 1 (web <c>providerDetail</c>).</summary>
    public static string ProviderDetail(ProviderDto p, Translate t)
    {
        var parts = new List<string>();
        var key = p.PriceKey ?? p.TemplateId;
        if (!string.IsNullOrEmpty(key)) parts.Add(key);
        parts.Add(t("providers.modelCount", ("count", p.ModelCount)));
        if (p.PriceMultiplier != 1) parts.Add("×" + FormatNumber(p.PriceMultiplier));
        return string.Join(" · ", parts);
    }

    private static string FormatNumber(decimal v) => v.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The token select's options (web <c>tokenOptions</c>): enabled tokens, plus the client's current one even when disabled (shown with
    /// the "disabled" label and not pickable) so the select never shows blank.
    /// </summary>
    public static List<(string Value, string Label, bool Disabled)> TokenOptions(IEnumerable<TokenDto>? tokens, string saved, Translate t)
        => (tokens ?? [])
            .Where(tk => tk.Enabled || tk.Id == saved)
            .Select(tk => (tk.Id, tk.Enabled ? tk.Name : t("clients.tokenDisabled", ("name", tk.Name)), !tk.Enabled))
            .ToList();

    /// <summary>Providers with accounts (subscriptions, plugin sign-ins) let a client pin one (web <c>isSubscriptionBinding</c>).</summary>
    public static bool IsAccountBinding(ProviderDto? p) => p?.AuthScheme is "oauth-subscription" or "plugin";

    /// <summary>The account select's options: "default", then each account, with its status when not active.</summary>
    public static List<(string Value, string Label)> AccountOptions(IEnumerable<ProviderAccountDto>? accounts, Translate t)
    {
        var list = new List<(string, string)> { ("", t("clients.accountDefault")) };
        foreach (var a in accounts ?? [])
        {
            var name = string.IsNullOrEmpty(a.DisplayName) ? a.Id : a.DisplayName;
            list.Add((a.Id, a.Status != "active" ? name + " · " + t("providers.subscription.status." + a.Status) : name));
        }
        return list;
    }

    /// <summary>
    /// The client's provider bindings in routing order (web <c>saved</c> in <c>ProviderBindings</c>): its list, else its single primary
    /// binding, else none.
    /// </summary>
    public static List<ClientBindingDto> SavedBindings(ClientInfoDto c)
    {
        if (c.Bindings is { } b) return [.. b];
        return c.ProviderId is { } p ? [new ClientBindingDto(p, c.AccountId)] : [];
    }

    /// <summary>The bound rows that still name a known provider, and the enabled providers not yet bound (web <c>bound</c> / <c>available</c>).</summary>
    public static (List<(ClientBindingDto Binding, ProviderDto Provider)> Bound, List<ProviderDto> Available) Split(IReadOnlyList<ClientBindingDto> order, IReadOnlyList<ProviderDto> providers)
    {
        var byId = providers.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bound = order.Where(b => byId.ContainsKey(b.ProviderId)).Select(b => (b, byId[b.ProviderId])).ToList();
        var available = providers.Where(p => p.Enabled && !order.Any(b => b.ProviderId == p.Id)).ToList();
        return (bound, available);
    }

    /// <summary>Moves the binding at <paramref name="from"/> to <paramref name="to"/> (dnd-kit <c>arrayMove</c>).</summary>
    public static List<T> Move<T>(IReadOnlyList<T> list, int from, int to)
    {
        var copy = list.ToList();
        if (from < 0 || from >= copy.Count || to < 0 || to >= copy.Count || from == to) return copy;
        var item = copy[from];
        copy.RemoveAt(from);
        copy.Insert(to, item);
        return copy;
    }

    /// <summary>The directory of a path (web <c>dirnameOf</c>: up to the last '/' or '\'; a path without one, or only a leading one, stays itself).</summary>
    public static string DirnameOf(string path)
    {
        var i = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return i > 0 ? path[..i] : path;
    }
}
