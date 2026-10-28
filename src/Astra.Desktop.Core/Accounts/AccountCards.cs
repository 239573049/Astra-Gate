using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Accounts;

/// <summary>The status filter of the account list (web <c>Filter</c>: all or one <see cref="AccountState"/>).</summary>
public enum AccountFilter
{
    /// <summary>Every account.</summary>
    All,

    /// <summary>Usable accounts.</summary>
    Usable,

    /// <summary>Rate-limited accounts.</summary>
    Cooling,

    /// <summary>Lapsed logins.</summary>
    Dead,

    /// <summary>Switched-off accounts.</summary>
    Disabled,
}

/// <summary>An entry of a card's or a detail's action menu (web <c>MenuItem</c>); the views map <see cref="Id"/> to the action and icon.</summary>
/// <param name="Id">activate, toggle, up, down, quota, refresh, relogin, remove.</param>
/// <param name="Label">The text.</param>
/// <param name="Enabled">False greys the entry out.</param>
/// <param name="SeparatorBefore">A line above it.</param>
/// <param name="Destructive">Shown in the danger color.</param>
public sealed record AccountMenuItem(string Id, string Label, bool Enabled = true, bool SeparatorBefore = false, bool Destructive = false);

/// <summary>Everything a subscription account card shows (web <c>SubscriptionAccountCard</c>), computed once per account and state.</summary>
public sealed record AccountCardModel(
    ProviderAccountDto Account, int Index, AccountState State, string Name, bool IsKey, string Headline, string StatusText, string KindText,
    string? SourceText, string? Meta, TextTone MetaTone, bool ShowMeta, IReadOnlyList<QuotaBar> Quota, IReadOnlyList<AccountMenuItem> Menu,
    (byte R, byte G, byte B) PlanRgb, string OrderText)
{
    /// <summary>Cooling and lapsed accounts get a 3 px edge of their state's color in the top band.</summary>
    public bool HasEdge => State is AccountState.Cooling or AccountState.Dead;
}

/// <summary>Builds account cards and the list's derived view (web SubscriptionAccounts.tsx "derived view" + SubscriptionAccountCard.tsx).</summary>
public static class AccountCards
{
    /// <summary>Web <c>FILTERS</c> with their labels.</summary>
    public static readonly IReadOnlyList<(AccountFilter Filter, string Key)> Filters =
    [
        (AccountFilter.All, "providers.accounts.filter.all"),
        (AccountFilter.Usable, "providers.accounts.filter.usable"),
        (AccountFilter.Cooling, "providers.accounts.filter.cooling"),
        (AccountFilter.Dead, "providers.accounts.filter.dead"),
        (AccountFilter.Disabled, "providers.accounts.filter.disabled"),
    ];

    /// <summary>Whether an account in <paramref name="state"/> passes <paramref name="filter"/>.</summary>
    public static bool Matches(AccountFilter filter, AccountState state) => filter switch
    {
        AccountFilter.All => true,
        AccountFilter.Usable => state == AccountState.Usable,
        AccountFilter.Cooling => state == AccountState.Cooling,
        AccountFilter.Dead => state == AccountState.Dead,
        _ => state == AccountState.Disabled,
    };

    /// <summary>How many accounts pass each filter (web <c>countOf</c>).</summary>
    public static int CountOf(IReadOnlyList<ProviderAccountDto> list, AccountFilter filter, DateTimeOffset now)
        => list.Count(a => Matches(filter, AccountRules.StateOf(a, now)));

    /// <summary>The filter chips shown: "all", plus every state some account is in.</summary>
    public static IReadOnlyList<(AccountFilter Filter, string Label, int Count)> Chips(IReadOnlyList<ProviderAccountDto> list, DateTimeOffset now)
        => Filters.Select(f => (f.Filter, Loc.T(f.Key), CountOf(list, f.Filter, now)))
            .Where(f => f.Item1 == AccountFilter.All || f.Item3 > 0)
            .ToList();

    /// <summary>
    /// The accounts shown, with their place in the failover order: those passing the filter whose name, e-mail, masked key or plan
    /// contains the search text (case-insensitive).
    /// </summary>
    public static IReadOnlyList<(ProviderAccountDto Account, int Index, AccountState State)> Visible(
        IReadOnlyList<ProviderAccountDto> list, AccountFilter filter, string? search, DateTimeOffset now)
    {
        var needle = (search ?? "").Trim().ToLowerInvariant();
        return list
            .Select((a, i) => (a, i, AccountRules.StateOf(a, now)))
            .Where(x => Matches(filter, x.Item3)
                && (needle.Length == 0 || new[] { x.a.DisplayName, x.a.AccountEmail, x.a.KeyMasked, x.a.Plan }
                    .Any(v => v?.ToLowerInvariant().Contains(needle, StringComparison.Ordinal) == true)))
            .ToList();
    }

    /// <summary>
    /// A card. <paramref name="listCount"/> is the length of the whole list (more than one adds "move up / down"); <paramref name="zone"/>
    /// is the time zone dates show in (local by default).
    /// </summary>
    public static AccountCardModel Card(ProviderAccountDto a, int index, int listCount, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var state = AccountRules.StateOf(a, now);
        var isKey = AccountRules.IsKey(a);
        var plan = a.Plan?.Trim();
        var kind = AccountRules.KindText(a);
        var quota = AccountQuota.Parse(a.Quota);

        // The one-line status: problems first (cooling / lapsed), then the token's expiry or the key's mask.
        (string Text, TextTone Tone)? meta = state switch
        {
            AccountState.Cooling => (Loc.T("providers.subscription.cooldown", ("when", AccountRules.When(a.CooldownUntilUtc, zone))), TextTone.Warning),
            AccountState.Dead => (string.IsNullOrEmpty(a.LastError) ? Loc.T(AccountRules.StatusKey(a.Status)) : a.LastError, TextTone.Danger),
            _ when isKey => (a.KeyMasked ?? Loc.T("providers.accounts.neverExpires"), TextTone.Muted),
            _ when a.ExpiresAtUtc is { } exp => (Loc.T("providers.accounts.expiresAt", ("when", AccountRules.When(exp, zone))), TextTone.Muted),
            _ => null,
        };
        var hasQuota = quota?.HasUsage == true;

        return new AccountCardModel(
            a, index, state, AccountRules.NameOf(a), isKey, string.IsNullOrEmpty(plan) ? kind : plan, AccountRules.StatusText(a, state), kind,
            AccountRules.SourceText(a), meta?.Text, meta?.Tone ?? TextTone.Muted,
            meta is not null && (state is AccountState.Cooling or AccountState.Dead || !hasQuota),
            state == AccountState.Dead ? [] : QuotaViews.Strip(quota, zone),
            Menu(a, state, index, listCount),
            AccountRules.PlanRgb(plan, isKey),
            Loc.T("providers.accounts.order", ("n", index + 1)));
    }

    /// <summary>The card's "more" menu (web <c>menu</c>): its entries and which are available.</summary>
    public static IReadOnlyList<AccountMenuItem> Menu(ProviderAccountDto a, AccountState state, int index, int listCount)
    {
        var isKey = AccountRules.IsKey(a);
        var items = new List<AccountMenuItem>
        {
            new("activate", Loc.T("providers.subscription.activate"), !a.IsCurrent && state != AccountState.Dead),
            new("toggle", Loc.T(a.Enabled ? "providers.subscription.disable" : "providers.subscription.enable")),
        };
        if (listCount > 1)
        {
            items.Add(new("up", Loc.T("providers.subscription.moveUp"), index > 0));
            items.Add(new("down", Loc.T("providers.subscription.moveDown"), index < listCount - 1));
        }
        items.Add(new("quota", Loc.T("providers.subscription.quotaRefresh"), state != AccountState.Dead, SeparatorBefore: true));
        if (!isKey)
        {
            items.Add(new("refresh", Loc.T("providers.subscription.refreshToken"), a.Status != "revoked"));
            items.Add(new("relogin", Loc.T("providers.subscription.relogin"), a.Status != "active"));
        }
        items.Add(new("remove", Loc.T("providers.subscription.logout"), SeparatorBefore: true, Destructive: true));
        return items;
    }

    /// <summary>
    /// The new failover order after swapping the account at <paramref name="index"/> with its neighbour (web <c>move</c>); null at an edge.
    /// </summary>
    public static List<string>? Swap(IReadOnlyList<ProviderAccountDto> list, int index, int delta)
    {
        var ids = list.Select(a => a.Id).ToList();
        var other = index + delta;
        if (index < 0 || index >= ids.Count || other < 0 || other >= ids.Count) return null;
        (ids[index], ids[other]) = (ids[other], ids[index]);
        return ids;
    }
}
