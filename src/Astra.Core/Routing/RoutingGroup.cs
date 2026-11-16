namespace Astra.Core.Routing;

/// <summary>How a routing group orders its members for a request (<c>routing_groups.mode</c>).</summary>
public static class GroupModes
{
    /// <summary>Of the members with quota to spare, the allowance renewing soonest first (see <see cref="QuotaModes.Smart"/>).</summary>
    public const string Smart = QuotaModes.Smart;

    /// <summary>The first member until it cannot answer, then the next.</summary>
    public const string Order = "order";

    /// <summary>Each turn of a conversation goes to the next member.</summary>
    public const string Rotate = "rotate";

    /// <summary>The least used member first.</summary>
    public const string Usage = QuotaModes.Usage;

    /// <summary>The member with the most of its week left per hour until it renews first.</summary>
    public const string Pace = QuotaModes.Pace;

    /// <summary>Each member takes its share of the requests by its weight, smoothly interleaved.</summary>
    public const string Weight = "weight";

    public static readonly IReadOnlyList<string> All = [Smart, Order, Rotate, Usage, Pace, Weight];
}

/// <summary>
/// One member of a routing group: a <c>provider/model</c> target (<see cref="Provider"/> is a provider id) or another group
/// (<see cref="Group"/> is its slug) — exactly one of the two.
/// </summary>
public sealed class GroupMember
{
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? Group { get; set; }

    /// <summary>Share of the requests under <see cref="GroupModes.Weight"/> (1–1000); ignored by the other modes.</summary>
    public int Weight { get; set; } = 1;

    public bool IsGroup => !string.IsNullOrEmpty(Group);

    /// <summary>A stable key of the member inside its group (also what the console shows per member).</summary>
    public string Key => IsGroup ? "group/" + Group : Provider + "/" + Model;

    public GroupMember Clone() => new() { Provider = Provider, Model = Model, Group = Group, Weight = Weight };
}

/// <summary>
/// A named virtual model clients pick like any other: <c>group/&lt;slug&gt;</c> (row of <c>routing_groups</c>, migration 0032).
/// A request for it goes to one of its members — <c>provider/model</c> targets or other groups — chosen by <see cref="Mode"/>,
/// the next member trying it when an attempt fails before anything reached the client. Ordering rules after magpie (MIT).
/// </summary>
public sealed class RoutingGroup
{
    /// <summary>The prefix of a group's model id.</summary>
    public const string Prefix = "group/";

    public const int MaxMembers = 32;

    /// <summary>How many groups may nest (a group of groups of groups); more is refused when saved.</summary>
    public const int MaxDepth = 3;

    public string Id { get; set; } = "";

    /// <summary>The id after <see cref="Prefix"/>: <c>[a-z0-9][a-z0-9-]{0,47}</c>.</summary>
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>A <see cref="GroupModes"/> value.</summary>
    public string Mode { get; set; } = GroupModes.Smart;

    /// <summary>A <see cref="Routing.Stays"/> value.</summary>
    public string Stays { get; set; } = Routing.Stays.Auto;

    /// <summary>The members as JSON (<see cref="GroupMember"/> list, Json.Storage).</summary>
    public string MembersJson { get; set; } = "[]";

    public bool Enabled { get; set; } = true;

    /// <summary>Whether the group is offered in model lists (a group that is off is still served when asked for by name).</summary>
    public bool Listed { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The model id clients use.</summary>
    public string ModelId => Prefix + Slug;

    public List<GroupMember> Members() => Json.Deserialize<List<GroupMember>>(MembersJson) ?? [];

    public void SetMembers(IReadOnlyList<GroupMember> members) => MembersJson = Json.Serialize(members.ToList());

    public RoutingGroup Clone() => new()
    {
        Id = Id, Slug = Slug, Name = Name, Mode = Mode, Stays = Stays, MembersJson = MembersJson, Enabled = Enabled, Listed = Listed,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
    };

    /// <summary>The slug of a <c>group/&lt;slug&gt;</c> model id; null for any other id.</summary>
    public static string? SlugOf(string? model) =>
        model is not null && model.StartsWith(Prefix, StringComparison.Ordinal) && ValidSlug(model[Prefix.Length..]) ? model[Prefix.Length..] : null;

    public static bool ValidSlug(string slug) =>
        slug.Length is >= 1 and <= 48 && (char.IsAsciiLetterLower(slug[0]) || char.IsAsciiDigit(slug[0]))
        && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A slug made from a display name ("Opus anywhere" → <c>opus-anywhere</c>); empty when nothing usable is left.</summary>
    public static string SlugFrom(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 48 ? slug[..48].TrimEnd('-') : slug;
    }
}
