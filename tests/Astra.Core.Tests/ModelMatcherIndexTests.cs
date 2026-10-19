using Astra.Core.Models;
using Astra.Core.Seed;

namespace Astra.Core.Tests;

public sealed class ModelMatcherIndexTests
{
    [Theory]
    [InlineData("FIRST.MODEL", "first.model")]
    [InlineData("SHARED", "shared")]
    [InlineData("mapped", "first-model")]
    [InlineData("alias", "first.model")]
    [InlineData("vendor/first_model", "first.model")]
    [InlineData("vendor/normalize-id", "first.model")]
    [InlineData("normalize-id", "normalize-id")]
    [InlineData("unknown", null)]
    [InlineData("~vendor/---", null)]
    public void Indexed_matching_preserves_priority_and_candidate_order(string input, string? expected)
    {
        ModelIdMatcher.Candidate[] candidates =
        [
            new("first.model", ["shared", "mapped", "alias", "normalize.id"]),
            new("first-model", ["ALIAS"]),
            new("shared", []),
            new("normalize-id", []),
        ];
        var upstream = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FIRST.MODEL"] = "first-model", ["shared"] = "first-model", ["mapped"] = "first-model",
        };
        Assert.Equal(expected, ModelIdMatcher.Match(input, candidates, upstream));
        Assert.Equal(expected, new ModelIdMatcher.Index(candidates).Match(input, upstream));
    }

    [Fact]
    public void Index_matches_the_reference_matcher_across_the_seed_and_normalized_variants()
    {
        var candidates = SeedCatalog.Current.Models.Select(m => new ModelIdMatcher.Candidate(m.Id, m.Aliases)).ToArray();
        var index = new ModelIdMatcher.Index(candidates);
        foreach (var candidate in candidates)
        foreach (var id in candidate.Aliases.Prepend(candidate.SystemModelId))
        foreach (var query in new[] { id, id.ToUpperInvariant(), "vendor/" + id, id + "-latest", id.Replace('-', '_') })
            Assert.Equal(ModelIdMatcher.Match(query, candidates), index.Match(query));
    }

    [Fact]
    public void Index_owns_its_snapshot_even_if_the_input_is_mutated()
    {
        var aliases = new List<string> { "old-alias" };
        var candidates = new List<ModelIdMatcher.Candidate> { new("model", aliases) };
        var index = new ModelIdMatcher.Index(candidates);
        aliases.Clear();
        candidates.Clear();
        Assert.Equal("model", index.Match("old-alias"));
        Assert.Equal("model", index.Match("model"));
    }
}
