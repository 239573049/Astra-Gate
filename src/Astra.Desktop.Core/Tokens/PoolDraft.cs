using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Core.Tokens;

/// <summary>A pool member being edited (web <c>PoolDraftMember</c>).</summary>
public sealed record PoolDraftMember(string ProviderId, string? AccountId, int Weight, int Tier);

/// <summary>The provider pool of a token's direct calls as the settings sheet edits it (web <c>PoolDraft</c>, issue #2).</summary>
public sealed record PoolDraft(string Strategy, string Sticky, bool Failover, int MaxAttempts, IReadOnlyList<PoolDraftMember> Members);

/// <summary>The pool strategies the editor offers, in order (web <c>POOL_STRATEGIES</c>; the ids are <c>TokenPoolStrategies</c> on the server).</summary>
public static class PoolStrategies
{
    /// <summary>The primary first, the others only when it fails.</summary>
    public const string Failover = "failover";

    /// <summary>Each in turn.</summary>
    public const string RoundRobin = "round_robin";

    /// <summary>Weighted random.</summary>
    public const string Weighted = "weighted";

    /// <summary>The lowest tier first, weighted inside it.</summary>
    public const string Tiered = "tiered";

    /// <summary>The member with the fewest requests in flight.</summary>
    public const string LeastBusy = "least_busy";

    /// <summary>All strategies in editor order.</summary>
    public static readonly IReadOnlyList<string> All = [Failover, RoundRobin, Weighted, Tiered, LeastBusy];

    /// <summary>The i18n key of a strategy's name (<c>tokens.strategy.&lt;id&gt;</c>).</summary>
    public static string NameKey(string strategy) => "tokens.strategy." + strategy;

    /// <summary>The i18n key of a strategy's hint.</summary>
    public static string HintKey(string strategy) => "tokens.strategy." + strategy + ".hint";
}

/// <summary>
/// The pool editor's logic, ported from <c>web/src/components/TokenPoolEditor.tsx</c> (everything but the markup): drafts from
/// and to the API shape, the expected traffic share per member (which mirrors the server's <c>ProviderPoolScheduler</c>, linked
/// update 18 — keep them in step) and the health badge of a member.
/// </summary>
public static class PoolDrafts
{
    /// <summary>The auth schemes whose providers have accounts to choose from (web <c>ACCOUNT_SCHEMES</c>).</summary>
    public static bool IsAccountBased(string authScheme) => authScheme is "oauth-subscription" or "plugin";

    /// <summary>The token's pool, or its single direct provider as a pool of one (new pools start as failover with sticky sessions) (<c>poolDraftOf</c>).</summary>
    public static PoolDraft Of(TokenDto token)
    {
        if (token.Pool is { } pool)
            return new PoolDraft(pool.Strategy, pool.Sticky, pool.Failover, pool.MaxAttempts,
                pool.Members.Select(m => new PoolDraftMember(m.ProviderId, m.AccountId, m.Weight, m.Tier)).ToList());
        return new PoolDraft(PoolStrategies.Failover, "session", true, 3,
            token.ProviderId is { Length: > 0 } id ? [new PoolDraftMember(id, token.AccountId, 1, 1)] : []);
    }

    /// <summary>What the API takes: members whose provider no longer exists are dropped, counts clamped (<c>poolInputOf</c>).</summary>
    public static JsonObject ToJson(PoolDraft draft, IEnumerable<string> knownProviderIds)
    {
        var known = knownProviderIds.ToHashSet(StringComparer.Ordinal);
        var members = new JsonArray();
        foreach (var m in draft.Members.Where(m => known.Contains(m.ProviderId)))
        {
            members.Add((JsonNode)new JsonObject
            {
                ["providerId"] = m.ProviderId,
                ["accountId"] = m.AccountId,
                ["weight"] = Math.Max(1, m.Weight),
                ["tier"] = m.Tier,
            });
        }
        return new JsonObject
        {
            ["strategy"] = draft.Strategy,
            ["sticky"] = draft.Sticky,
            ["failover"] = draft.Failover,
            ["maxAttempts"] = Math.Min(5, Math.Max(1, draft.MaxAttempts)),
            ["members"] = members,
        };
    }

    /// <summary>
    /// Share of new requests each member gets when all are healthy and serve the model (<c>expectedShares</c>; mirrors
    /// <c>ProviderPoolScheduler</c>): failover sends all to the first, weighted by weight, tiered by weight inside the lowest tier,
    /// the rest evenly.
    /// </summary>
    public static double[] ExpectedShares(PoolDraft draft)
    {
        var members = draft.Members;
        if (members.Count == 0) return [];
        static int Weights(IEnumerable<PoolDraftMember> list) => list.Sum(m => Math.Max(1, m.Weight));
        switch (draft.Strategy)
        {
            case PoolStrategies.Failover:
                return members.Select((_, i) => i == 0 ? 1.0 : 0.0).ToArray();
            case PoolStrategies.Weighted:
            {
                var total = Weights(members);
                return members.Select(m => (double)Math.Max(1, m.Weight) / total).ToArray();
            }
            case PoolStrategies.Tiered:
            {
                var top = members.Min(m => m.Tier);
                var total = Weights(members.Where(m => m.Tier == top));
                return members.Select(m => m.Tier == top ? (double)Math.Max(1, m.Weight) / total : 0.0).ToArray();
            }
            default:
                return members.Select(_ => 1.0 / members.Count).ToArray();
        }
    }

    /// <summary>A member's health as a badge / dot tone name: healthy green, cooling or circuit-open orange, everything else red (<c>healthTone</c>).</summary>
    public static HealthTone ToneOf(string state) => state switch
    {
        "healthy" => HealthTone.Green,
        "cooling" or "open" or "half_open" => HealthTone.Orange,
        _ => HealthTone.Red,
    };

    /// <summary>The health text: cooling / open say until when (<c>useHealthLabel</c>).</summary>
    public static string HealthLabel(string state, DateTimeOffset? until, DateTimeOffset now, TimeSpan zone, string language, Translate t)
        => state is "cooling" or "open"
            ? t("tokens.health." + state, ("time", until is { } u ? StatsFormat.Time(u, now, zone, language) : StatsFormat.Dash))
            : t("tokens.health." + state);

    /// <summary>
    /// The state a member shows in the editor: a missing provider is "missing", a disabled one "disabled", else the live health of the
    /// saved pool (null when the member is new and has none).
    /// </summary>
    public static string? EffectiveState(ProviderDto? provider, PoolMemberHealthDto? health)
        => provider is null ? "missing" : !provider.Enabled ? "disabled" : health?.State;
}

/// <summary>Tone of a health indicator.</summary>
public enum HealthTone
{
    /// <summary>Can take requests.</summary>
    Green,

    /// <summary>Resting.</summary>
    Orange,

    /// <summary>Not usable.</summary>
    Red,
}
