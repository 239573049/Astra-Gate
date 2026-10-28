using Astra.Desktop.Core.Requests;

namespace Astra.Desktop.Tests.Requests;

public class RequestFilterTests
{
    [Fact]
    public void Route_param_round_trips_filters_and_model()
    {
        var (model, filters) = RequestRouteParams.Parse("model=gpt%204&token=a,b&status=success&origin=remote&bogus=1");
        Assert.Equal("gpt 4", model);
        Assert.Equal(["a", "b"], filters.Get(RequestFilterId.Token));
        Assert.Equal(["success"], filters.Get(RequestFilterId.Status));
        Assert.Equal(["remote"], filters.Get(RequestFilterId.Origin));
        Assert.Empty(filters.Get(RequestFilterId.Client));
        // Written back model first, then each non-empty filter in the bar's order, comma lists escaped like URLSearchParams.
        Assert.Equal("model=gpt%204&token=a%2Cb&status=success&origin=remote", RequestRouteParams.Format(model, filters));
    }

    [Fact]
    public void Empty_or_missing_param_is_no_filter()
    {
        Assert.Equal(("", RequestFilterValues.Empty), RequestRouteParams.Parse(null));
        Assert.Equal(("", RequestFilterValues.Empty), RequestRouteParams.Parse("?token=&client="));
        Assert.Equal("", RequestRouteParams.Format("", RequestFilterValues.Empty));
    }

    [Fact]
    public void Filter_values_compare_by_content()
    {
        var a = RequestFilterValues.Empty.With(RequestFilterId.Client, ["codex", "pi"]);
        var b = RequestFilterValues.Empty.With(RequestFilterId.Client, ["codex", "pi"]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, RequestFilterValues.Empty);
        Assert.True(a.Any);
        Assert.False(RequestFilterValues.Empty.Any);
    }

    [Fact]
    public void List_query_matches_the_web_url_and_key()
    {
        var filters = RequestFilterValues.Empty
            .With(RequestFilterId.Client, ["codex", "pi"])
            .With(RequestFilterId.Status, ["success"])
            .With(RequestFilterId.Origin, ["remote", "local"]);
        var q = new RequestListQuery("gpt", filters, 2);
        // Both origins picked mean "any": the server's single origin filter stays empty.
        Assert.Equal("/api/requests?model=gpt&client=codex%2Cpi&status=success&page=2&pageSize=50", q.Path);
        Assert.Equal("requests/gpt/codex,pi///success//2/50", q.Key.ToString());
        Assert.StartsWith("requests", q.Key.ToString());
        Assert.True(q.Key.StartsWith("requests"));
    }

    [Fact]
    public void One_origin_narrows_the_list_and_the_rate()
    {
        var filters = RequestFilterValues.Empty.With(RequestFilterId.Origin, ["local"]);
        var q = new RequestListQuery("", filters, 1);
        Assert.Equal("/api/requests?origin=local&page=1&pageSize=50", q.Path);
        var (path, key) = q.Rate();
        // The rate follows the list's filters but never its page.
        Assert.Equal("/api/stats/rate?origin=local", path);
        Assert.True(key.StartsWith("stats"));
    }

    [Fact]
    public void Origin_filter_shows_only_once_remote_access_was_used()
    {
        var none = RequestFilterValues.Empty;
        Assert.False(RequestFilterCatalog.ShowOrigin(false, false, none));
        Assert.True(RequestFilterCatalog.ShowOrigin(true, false, none));
        Assert.True(RequestFilterCatalog.ShowOrigin(false, true, none));
        // A picked value keeps the field so it can be cleared.
        Assert.True(RequestFilterCatalog.ShowOrigin(false, false, none.With(RequestFilterId.Origin, ["remote"])));
        Assert.DoesNotContain(RequestFilterId.Origin, RequestFilterCatalog.VisibleFields(false));
        Assert.Contains(RequestFilterId.Origin, RequestFilterCatalog.VisibleFields(true));
    }

    [Fact]
    public void Detail_and_body_paths_escape_the_id()
    {
        Assert.Equal("/api/requests/a%2Fb", RequestPaths.Detail("a/b"));
        Assert.Equal("/api/requests/r1/bodies/client-request", RequestPaths.Body("r1", "client-request"));
        Assert.Equal(5, RequestPaths.BodyParts.Count);
    }

    [Fact]
    public void Has_filters_counts_a_blank_model_as_none()
    {
        Assert.False(RequestFilterCatalog.HasFilters("  ", RequestFilterValues.Empty));
        Assert.True(RequestFilterCatalog.HasFilters("x", RequestFilterValues.Empty));
        Assert.True(RequestFilterCatalog.HasFilters("", RequestFilterValues.Empty.With(RequestFilterId.Token, ["t"])));
    }
}
