using System.Text;
using System.Text.Json;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.RoutingGroups;

/// <summary>
/// The group editor's fields and what they become (web <c>RoutingGroups.tsx</c>: <c>slugFrom</c>, <c>draftOf</c>, <c>draftValid</c>,
/// <c>writeOf</c>, <c>problemMessages</c>, <c>reasonText</c>). A member is <c>provider/model</c> (the provider's id) or another group's slug.
/// </summary>
public sealed class RoutingGroupDraft
{
    /// <summary>The modes in picker order.</summary>
    public static readonly IReadOnlyList<string> Modes = ["smart", "order", "rotate", "usage", "pace", "weight"];

    /// <summary>How long a conversation stays with the member that answered it.</summary>
    public static readonly IReadOnlyList<string> StaysOptions = ["auto", "session", "turn", "off", "strict"];

    /// <summary>A new group's draft: smart, auto stays, listed, no members.</summary>
    public RoutingGroupDraft()
    {
    }

    /// <summary>A draft of an existing group.</summary>
    public RoutingGroupDraft(RoutingGroupDto g)
    {
        Existing = g;
        Name = g.Name;
        Slug = g.Slug;
        Mode = g.Mode;
        Stays = g.Stays;
        Enabled = g.Enabled;
        Listed = g.Listed;
        Members = g.Members.Select(m => new RoutingGroupMemberDto(m.Provider, m.Model, m.Group, m.Weight)).ToList();
    }

    /// <summary>The group being edited; null = a new one.</summary>
    public RoutingGroupDto? Existing { get; }

    /// <summary>The display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The model id's suffix; follows the name until the user edits it (never editable once saved).</summary>
    public string Slug { get; set; } = "";

    /// <summary>Whether the user typed the slug themselves.</summary>
    public bool SlugTouched { get; set; }

    /// <summary>One of <see cref="Modes"/>.</summary>
    public string Mode { get; set; } = "smart";

    /// <summary>One of <see cref="StaysOptions"/>.</summary>
    public string Stays { get; set; } = "auto";

    /// <summary>Whether the group is served at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether the group is offered in model lists.</summary>
    public bool Listed { get; set; } = true;

    /// <summary>The members in order.</summary>
    public List<RoutingGroupMemberDto> Members { get; set; } = [];

    /// <summary>The key of a member inside its group: <c>provider/model</c> or <c>group/slug</c> (the server keys it the same way).</summary>
    public static string KeyOf(RoutingGroupMemberDto m) => !string.IsNullOrEmpty(m.Group) ? "group/" + m.Group : m.Provider + "/" + m.Model;

    /// <summary>A slug from a display name: lower-case letters and digits joined by single dashes, at most 48 characters.</summary>
    public static string SlugFrom(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }

    /// <summary>Whether the slug is one the server accepts.</summary>
    public static bool ValidSlug(string slug)
        => slug.Length is >= 1 and <= 48 && (char.IsAsciiLetterLower(slug[0]) || char.IsAsciiDigit(slug[0]))
           && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A name, a slug the server accepts and at least one member.</summary>
    public bool IsValid => Name.Trim().Length > 0 && ValidSlug(Slug) && Members.Count > 0;

    /// <summary>The name changed: a slug nobody typed follows it (a new group only).</summary>
    public void SetName(string name)
    {
        Name = name;
        if (Existing is null && !SlugTouched) Slug = SlugFrom(name);
    }

    /// <summary>The POST / PATCH body: the slug only on create, weights clamped to 1–1000 (only the weight mode reads them).</summary>
    public RoutingGroupWrite ToBody() => new(
        Name.Trim(), Existing is null ? Slug : null, Mode, Stays, Enabled, Listed,
        Members.Select(m => new RoutingGroupMemberDto(m.Provider, m.Model, m.Group, Math.Clamp(m.Weight, 1, 1000))).ToList());

    /// <summary>One refusal in the UI's language (<c>routingGroups.problem.&lt;code&gt;</c>); the server's English sentence for a code this build does not know.</summary>
    public static string ProblemText(RoutingProblemDto problem)
    {
        var key = "routingGroups.problem." + problem.Code;
        return Loc.Has(key) ? Loc.T(key) : problem.Message;
    }

    /// <summary>What went wrong, line by line: every problem of a refused save, or the error's own words.</summary>
    public static List<string> Explain(Exception error)
    {
        if (error is ApiException { Details: { } details })
        {
            RoutingProblemsDetails? parsed = null;
            try { parsed = details.Deserialize(JsonContexts.Info<RoutingProblemsDetails>(Json.Api)); }
            catch (JsonException) { /* not a problems list: fall through to the message */ }
            if (parsed is { Problems.Count: > 0 }) return parsed.Problems.Select(ProblemText).ToList();
        }
        return [error.Message];
    }

    /// <summary>Why a request went to a member (a <c>RankReasons</c> code) in the UI's language; the code itself for one this build does not know.</summary>
    public static string ReasonText(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "—";
        var key = "routingGroups.reason." + reason;
        return Loc.Has(key) ? Loc.T(key) : reason;
    }

    /// <summary>A member's health in the UI's language; a cooling or paused one says until when.</summary>
    public static string HealthText(RoutingGroupMemberStatusDto s, string language, TimeZoneInfo? zone = null)
    {
        var key = "routingGroups.health." + s.Health;
        if (!Loc.Has(key)) return s.Health;
        return s.Health is "cooling" or "open"
            ? Loc.T(key, ("time", s.CoolingUntil is { } until ? Settings.SettingsFormat.DateTime(until, language, zone) : "—"))
            : Loc.T(key);
    }
}
