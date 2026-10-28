using Astra.Desktop.Core.Requests;
using static Astra.Desktop.Tests.Requests.RequestTestData;

namespace Astra.Desktop.Tests.Requests;

public class RequestListMergeTests
{
    private static readonly RequestFilterValues None = RequestFilterValues.Empty;

    [Fact]
    public void Live_rows_not_stored_yet_go_in_front_of_the_stored_page()
    {
        var stored = new List<Astra.Api.Contracts.RequestSummaryDto> { Row("s1"), Row("s2") };
        var live = new List<Astra.Api.Contracts.RequestSummaryDto> { Row("n1", "pending"), Row("s1", "pending") };
        var only = RequestListMerge.LiveOnly(1, stored, live, "", None);
        // s1 is stored already, so only n1 is live-only.
        Assert.Equal(["n1"], only.Select(r => r.Id));
        Assert.Equal(["n1", "s1", "s2"], RequestListMerge.Items(only, stored).Select(r => r.Id));
    }

    [Fact]
    public void Only_page_one_follows_the_live_feed()
    {
        Assert.Empty(RequestListMerge.LiveOnly(2, [], [Row("n1", "pending")], "", None));
        Assert.False(RequestListMerge.PollsList(2, false));
        Assert.True(RequestListMerge.PollsList(1, false));
        Assert.False(RequestListMerge.PollsList(1, true));
    }

    [Fact]
    public void Live_rows_are_narrowed_by_the_filters_the_server_applies()
    {
        var codex = Row("a", client: "codex", token: "t1", provider: "p1", status: "pending");
        Assert.True(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Client, ["codex", "pi"])));
        Assert.False(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Client, ["pi"])));
        Assert.False(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Token, ["t2"])));
        Assert.False(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Provider, ["p2"])));
        Assert.False(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Status, ["success"])));
        Assert.True(RequestListMerge.Matches(codex, "", None.With(RequestFilterId.Status, ["success", "pending"])));
        // A direct call has no client kind: it matches "" only.
        Assert.False(RequestListMerge.Matches(Row("d"), "", None.With(RequestFilterId.Client, ["codex"])));
    }

    [Fact]
    public void One_origin_filters_and_two_mean_any()
    {
        var remote = Row("r", remote: true);
        Assert.True(RequestListMerge.Matches(remote, "", None.With(RequestFilterId.Origin, ["remote"])));
        Assert.False(RequestListMerge.Matches(remote, "", None.With(RequestFilterId.Origin, ["local"])));
        Assert.True(RequestListMerge.Matches(remote, "", None.With(RequestFilterId.Origin, ["local", "remote"])));
    }

    [Fact]
    public void Model_keyword_matches_any_of_the_four_model_names_case_insensitively()
    {
        var r = Row("m", model: "gpt-4.1", upstream: "vendor-x", response: "RESP-1");
        Assert.True(RequestListMerge.Matches(r, "GPT", None));
        Assert.True(RequestListMerge.Matches(r, "vendor", None));
        Assert.True(RequestListMerge.Matches(r, "resp-1", None));
        Assert.False(RequestListMerge.Matches(r, "claude", None));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(0L, 1)]
    [InlineData(50L, 1)]
    [InlineData(51L, 2)]
    [InlineData(1000L, 20)]
    public void Page_count_never_drops_below_one(long? total, int pages) => Assert.Equal(pages, RequestListMerge.PageCount(total));

    [Fact]
    public void Freshest_prefers_the_live_copy()
    {
        var stored = Row("a", "pending");
        var live = Row("a", "success");
        Assert.Same(live, RequestListMerge.Freshest(stored, live));
        Assert.Same(stored, RequestListMerge.Freshest(stored, null));
    }
}
