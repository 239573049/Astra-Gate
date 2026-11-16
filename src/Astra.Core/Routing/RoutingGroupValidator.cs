namespace Astra.Core.Routing;

/// <summary>
/// The rules a routing group must satisfy when saved (like <see cref="SmartRoutingValidator"/>: stable codes the console turns
/// into its own words, an English message for everything else, the form field). Whether a provider serves a member's model is
/// not checked here — a member may name a model that is switched off today; the request then skips it.
/// </summary>
public static class RoutingGroupValidator
{
    public static class Codes
    {
        public const string SlugInvalid = "slug_invalid";
        public const string SlugDuplicate = "slug_duplicate";
        public const string NameMissing = "name_missing";
        public const string TooLong = "too_long";
        public const string ModeInvalid = "mode_invalid";
        public const string StaysInvalid = "stays_invalid";
        public const string MembersEmpty = "members_empty";
        public const string MembersTooMany = "members_too_many";
        public const string MemberInvalid = "member_invalid";
        public const string MemberProviderMissing = "member_provider_missing";
        public const string MemberGroupMissing = "member_group_missing";
        public const string MemberDuplicate = "member_duplicate";
        public const string MemberSelf = "member_self";
        public const string MemberCycle = "member_cycle";
        public const string MemberDepth = "member_depth";
        public const string WeightRange = "weight_range";
    }

    /// <summary>
    /// The problems of saving <paramref name="group"/> next to the <paramref name="others"/> already stored (the group itself, when
    /// it is being edited, is left out of them). <paramref name="providerIds"/> is every provider id that exists; null skips that check.
    /// </summary>
    public static List<RoutingProblem> Check(RoutingGroup group, IReadOnlyList<RoutingGroup> others, IReadOnlySet<string>? providerIds)
    {
        var problems = new List<RoutingProblem>();
        if (!RoutingGroup.ValidSlug(group.Slug))
            problems.Add(new(Codes.SlugInvalid, "slug must start with a letter or digit and use only a-z, 0-9 and '-' (at most 48 characters)", "slug"));
        else if (others.Any(o => o.Slug == group.Slug))
            problems.Add(new(Codes.SlugDuplicate, $"a group '{group.Slug}' already exists", "slug"));
        if (string.IsNullOrWhiteSpace(group.Name)) problems.Add(new(Codes.NameMissing, "a group needs a name", "name"));
        else if (group.Name.Length > 80) problems.Add(new(Codes.TooLong, "a group name is at most 80 characters", "name"));
        if (!GroupModes.All.Contains(group.Mode))
            problems.Add(new(Codes.ModeInvalid, $"mode must be one of: {string.Join(", ", GroupModes.All)}", "mode"));
        if (!Stays.All.Contains(group.Stays))
            problems.Add(new(Codes.StaysInvalid, $"stays must be one of: {string.Join(", ", Stays.All)}", "stays"));

        var members = group.Members();
        if (members.Count == 0) problems.Add(new(Codes.MembersEmpty, "a group needs at least one member", "members"));
        if (members.Count > RoutingGroup.MaxMembers)
            problems.Add(new(Codes.MembersTooMany, $"a group has at most {RoutingGroup.MaxMembers} members", "members"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var at = $"members[{i}]";
            var asGroup = !string.IsNullOrEmpty(m.Group);
            var asModel = !string.IsNullOrEmpty(m.Provider) || !string.IsNullOrEmpty(m.Model);
            if (asGroup == asModel || asModel && (string.IsNullOrEmpty(m.Provider) || string.IsNullOrEmpty(m.Model)))
            {
                problems.Add(new(Codes.MemberInvalid, "a member is either a provider and a model, or a group", at));
                continue;
            }
            if (m.Weight is < 1 or > 1000) problems.Add(new(Codes.WeightRange, "a weight is 1 to 1000", $"{at}.weight"));
            if (m.Provider is { Length: > 200 } || m.Model is { Length: > 200 } || m.Group is { Length: > 48 })
                problems.Add(new(Codes.TooLong, "a member field is too long", at));
            if (!seen.Add(m.Key)) problems.Add(new(Codes.MemberDuplicate, $"'{m.Key}' is listed twice", at));
            if (asModel && providerIds is not null && !providerIds.Contains(m.Provider!))
                problems.Add(new(Codes.MemberProviderMissing, $"provider '{m.Provider}' does not exist", $"{at}.provider"));
            if (asGroup)
            {
                if (m.Group == group.Slug) problems.Add(new(Codes.MemberSelf, "a group cannot contain itself", $"{at}.group"));
                else if (others.All(o => o.Slug != m.Group)) problems.Add(new(Codes.MemberGroupMissing, $"group '{m.Group}' does not exist", $"{at}.group"));
            }
        }
        if (problems.Any(p => p.Code is Codes.MemberSelf or Codes.MemberGroupMissing or Codes.SlugInvalid)) return problems;

        // Nesting: no cycle through the stored groups, and no chain of groups deeper than MaxDepth.
        var graph = others.ToDictionary(o => o.Slug, o => o.Members().Where(m => m.IsGroup).Select(m => m.Group!).ToList(), StringComparer.Ordinal);
        graph[group.Slug] = members.Where(m => m.IsGroup).Select(m => m.Group!).ToList();
        if (Cycle(graph, group.Slug)) problems.Add(new(Codes.MemberCycle, "these members make a group contain itself", "members"));
        else if (Depth(graph, group.Slug) > RoutingGroup.MaxDepth)
            problems.Add(new(Codes.MemberDepth, $"groups nest at most {RoutingGroup.MaxDepth} deep", "members"));
        else
            // Saving this group may also deepen a group that already contains it.
            foreach (var slug in graph.Keys)
                if (slug != group.Slug && Depth(graph, slug) > RoutingGroup.MaxDepth)
                {
                    problems.Add(new(Codes.MemberDepth, $"groups nest at most {RoutingGroup.MaxDepth} deep ('{slug}' would go deeper)", "members"));
                    break;
                }
        return problems;
    }

    /// <summary>Whether the slugs reachable from <paramref name="start"/> lead back to it.</summary>
    public static bool Cycle(IReadOnlyDictionary<string, List<string>> graph, string start)
    {
        var done = new HashSet<string>(StringComparer.Ordinal);
        var path = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string slug)
        {
            if (path.Contains(slug)) return true;
            if (!done.Add(slug)) return false;
            path.Add(slug);
            if (graph.TryGetValue(slug, out var next))
                foreach (var n in next)
                    if (Visit(n)) return true;
            path.Remove(slug);
            return false;
        }
        return Visit(start);
    }

    /// <summary>The number of groups on the longest chain starting at <paramref name="slug"/> (1 = a group of plain models). Call only on an acyclic graph.</summary>
    public static int Depth(IReadOnlyDictionary<string, List<string>> graph, string slug)
    {
        var memo = new Dictionary<string, int>(StringComparer.Ordinal);
        int Of(string s) => memo.TryGetValue(s, out var d) ? d : memo[s] = 1 + (graph.TryGetValue(s, out var next) && next.Count > 0 ? next.Max(Of) : 0);
        return Of(slug);
    }
}
