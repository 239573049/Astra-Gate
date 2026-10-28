using System.Text.Json.Nodes;
using Astra.Core.Routing;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Tests.Requests;

public class RequestPresentationTests
{
    /// <summary>A translator that shows its inputs, so a test sees which key and which values were chosen.</summary>
    private static readonly TraceText T = (key, args) => args.Length == 0 ? key : key + "|" + string.Join(",", args.Select(a => $"{a.Name}={a.Value}"));

    [Theory]
    [InlineData("success", RequestStatusKind.Success)]
    [InlineData("client_cancelled", RequestStatusKind.Cancelled)]
    [InlineData("pending", RequestStatusKind.Pending)]
    [InlineData("upstream_error", RequestStatusKind.Failed)]
    [InlineData("blocked", RequestStatusKind.Failed)]
    [InlineData("gateway_error", RequestStatusKind.Failed)]
    public void Status_badge_tone(string status, RequestStatusKind kind) => Assert.Equal(kind, RequestPresentation.Status(status, 200, null, T).Kind);

    [Fact]
    public void Status_tooltip_joins_http_and_hint()
    {
        Assert.Equal("HTTP 502 · requests.viewError", RequestPresentation.Status("upstream_error", 502, "requests.viewError", T).Title);
        Assert.Equal("HTTP 200", RequestPresentation.Status("success", 200, null, T).Title);
        Assert.Null(RequestPresentation.Status("client_cancelled", null, null, T).Title);
        // A pending badge carries only the hint.
        Assert.Equal("h", RequestPresentation.Status("pending", 200, "h", T).Title);
        Assert.Equal("status.upstream_error", RequestPresentation.Status("upstream_error", null, null, T).Label);
    }

    [Theory]
    [InlineData("success", false)]
    [InlineData("client_cancelled", false)]
    [InlineData("pending", false)]
    [InlineData("blocked", true)]
    [InlineData("upstream_error", true)]
    public void Only_failures_get_the_view_error_affordance(string status, bool failed) => Assert.Equal(failed, RequestPresentation.IsFailed(status));

    [Fact]
    public void A_direct_call_has_a_token_but_no_client()
    {
        Assert.Equal("Claude Code", RequestPresentation.RequestClientName("claude-code", "t", T));
        Assert.Equal("requests.direct", RequestPresentation.RequestClientName(null, "tok", T));
        Assert.Equal("—", RequestPresentation.RequestClientName(null, null, T));
        // A kind no table knows is shown as it is.
        Assert.Equal("future-ai", RequestPresentation.RequestClientName("future-ai", null, T));
        Assert.Equal("foo", RequestPresentation.ClientName("plugin-foo"));
        Assert.Equal("Shown", RequestPresentation.ClientName("plugin-foo", "Shown"));
    }

    [Fact]
    public void Reasoning_parts_list_only_what_the_client_set()
    {
        Assert.Empty(RequestPresentation.ReasoningParts(null, null, null, T));
        Assert.Equal(["medium", "enabled", "requests.reasoning.budget|tokens=2048"], RequestPresentation.ReasoningParts("medium", "enabled", 2048, T));
        Assert.Equal(["requests.reasoning.budget|tokens=0"], RequestPresentation.ReasoningParts(null, null, 0, T));
    }

    [Fact]
    public void Mapped_model_appears_only_when_the_gateway_sent_another()
    {
        Assert.Equal("up", RequestPresentation.MappedModel("req", "up"));
        Assert.Null(RequestPresentation.MappedModel("req", "req"));
        Assert.Null(RequestPresentation.MappedModel("req", null));
        Assert.Null(RequestPresentation.MappedModel(null, "up"));
    }

    [Fact]
    public void Returned_model_compares_with_what_was_sent()
    {
        var same = RequestPresentation.ResponseModel("gpt", "gpt", "gpt", "success", true, T);
        Assert.True(same.Comparable);
        Assert.True(same.Matches);
        Assert.Equal("requests.returnedModel|model=gpt", same.Text);
        Assert.Equal("requests.modelMatch", same.MatchWord);
        Assert.StartsWith("requests.modelComparison|requested=gpt,upstream=gpt,returned=gpt", same.Title);

        var other = RequestPresentation.ResponseModel("gpt", "mapped", "different", "success", false, T);
        Assert.True(other.Comparable);
        Assert.False(other.Matches);
        Assert.Equal("different", other.Text);
        Assert.Equal("requests.modelMismatch", other.MatchWord);
    }

    [Fact]
    public void Returned_model_of_a_pending_request_is_not_yet_recorded()
    {
        var pending = RequestPresentation.ResponseModel("gpt", "gpt", null, "pending", true, T);
        Assert.False(pending.Comparable);
        Assert.Equal("requests.returnedModel|model=requests.modelPending", pending.Text);
        Assert.Equal("requests.modelPending.hint", pending.Title);
        Assert.Null(pending.MatchWord);
        var old = RequestPresentation.ResponseModel("gpt", "gpt", null, "success", false, T);
        Assert.Equal("requests.modelNotRecorded", old.Text);
        Assert.Equal("requests.modelNotRecorded.hint", old.Title);
    }

    [Fact]
    public void Protocol_shows_a_conversion_only_when_the_call_was_translated()
    {
        Assert.Equal(new ProtocolInfo("openai-chat", null), RequestPresentation.Protocol("openai-chat", "openai-chat", false));
        Assert.Equal(new ProtocolInfo("openai-chat", null), RequestPresentation.Protocol("openai-chat", "anthropic", true));
        Assert.Equal(new ProtocolInfo("openai-chat", null), RequestPresentation.Protocol("openai-chat", null, false));
        Assert.Equal(new ProtocolInfo("openai-chat", "openai-responses"), RequestPresentation.Protocol("openai-chat", "openai-responses", false));
    }

    [Fact]
    public void Routing_badge_judged_reused_fallback_and_moved()
    {
        Assert.Null(RequestPresentation.Routing(null, T));
        var judged = RequestPresentation.Routing(new RoutingReport("high", "m1", "judge", "turn", 0.876), T)!;
        Assert.True(judged.Ok);
        Assert.Equal("traces.node.routing: high", judged.Text);
        Assert.Contains("requests.routing.hint.judge|grade=high,model=m1,percent=88", judged.Title);

        var reused = RequestPresentation.Routing(new RoutingReport("low", "m2", "pin", "session"), T)!;
        Assert.True(reused.Ok);
        Assert.Equal("traces.node.routing: low · traces.route.source.pin", reused.Text);

        // An unknown source is shown as recorded.
        Assert.Equal("traces.node.routing: low · weird", RequestPresentation.Routing(new RoutingReport("low", "m2", "weird", "turn"), T)!.Text);

        var fallback = RequestPresentation.Routing(new RoutingReport("mid", "m3", "fallback", "turn", FallbackReason: "timeout"), T, _ => true)!;
        Assert.False(fallback.Ok);
        Assert.Equal("traces.node.routing: requests.routing.fallback|grade=mid,model=m3,reason=smartRouting.failure.timeout", fallback.Text);

        var moved = RequestPresentation.Routing(new RoutingReport("mid", "m3", "judge", "turn", AdjustedFrom: "high", AdjustedReason: "token_limited"), T, _ => true)!;
        Assert.False(moved.Ok);
        Assert.Contains("requests.routing.moved|grade=mid,model=m3,from=high", moved.Text);
        Assert.Contains("reason=smartRouting.unusable.token_limited", moved.Title);
    }

    [Fact]
    public void Unknown_reasons_fall_back_to_the_raw_code_or_the_unknown_text()
    {
        Assert.Equal("some_new_reason", RequestPresentation.RouteReason("some_new_reason", T, _ => false));
        Assert.Equal("smartRouting.unusable.unknown", RequestPresentation.Unusable("nope", T, _ => false));
        Assert.Equal("smartRouting.unusable.unknown", RequestPresentation.Unusable(null, T, _ => true));
        Assert.Equal("weird", RequestPresentation.RouteMode("weird", T));
        Assert.Equal("traces.route.mode.turn", RequestPresentation.RouteMode("turn", T));
    }

    [Fact]
    public void Cache_cell_never_shows_missing_usage_as_a_zero_hit_rate()
    {
        var missing = RequestPresentation.CacheCell(UsageSplit.Of(100, 10, 0, 0, 0, "missing"), T);
        Assert.True(missing.Empty);
        Assert.Equal("usage.missing", missing.EmptyTitle);
        var none = RequestPresentation.CacheCell(UsageSplit.Of(100, 10, 0, 0, 0, "reported"), T);
        Assert.True(none.Empty);
        Assert.Equal("usage.noCache", none.EmptyTitle);
        var hit = RequestPresentation.CacheCell(UsageSplit.Of(35906, 1078, 28567, 12000, 0, "reported"), T);
        Assert.False(hit.Empty);
        Assert.Equal("usage.hitShort 28.6K 79.6%", hit.First);
        Assert.Equal("usage.writeShort 12K", hit.Second);
    }

    [Fact]
    public void Tokens_cell_adds_reasoning_underneath()
    {
        var plain = RequestPresentation.TokensCell(UsageSplit.Of(35906, 1078, 0, 0, 0, "reported"), T);
        Assert.Equal("35.9K / 1,078", plain.First);
        Assert.Null(plain.Second);
        var thinking = RequestPresentation.TokensCell(UsageSplit.Of(32600, 1715, 0, 0, 491, "reported"), T);
        Assert.Equal("usage.reasoningShort 491", thinking.Second);
        Assert.True(RequestPresentation.TokensCell(UsageSplit.Of(1, 1, 0, 0, 0, "missing"), T).Empty);
    }

    [Fact]
    public void Usage_split_is_disjoint_and_clamped()
    {
        var u = UsageSplit.Of(1000, 200, 600, 100, 50, "reported");
        Assert.Equal(300, u.UncachedInput);
        Assert.Equal(150, u.RegularOutput);
        Assert.Equal(0.6, u.HitRatio);
        var odd = UsageSplit.Of(10, 5, 20, 5, 9, "reported");
        Assert.Equal(0, odd.UncachedInput);
        Assert.Equal(0, odd.RegularOutput);
        Assert.Equal(1.0, odd.HitRatio);
        Assert.Null(UsageSplit.Of(100, 1, 50, 0, 0, "missing").HitRatio);
        Assert.Null(UsageSplit.CacheHitRatio(5, 0));
    }

    [Fact]
    public void Origin_line_of_the_detail()
    {
        Assert.Equal("requests.origin.remoteFrom|ip=1.2.3.4", RequestPresentation.Origin(true, "1.2.3.4", T));
        Assert.Equal("requests.origin.remote", RequestPresentation.Origin(true, null, T));
        Assert.Equal("127.0.0.1", RequestPresentation.Origin(false, "127.0.0.1", T));
        Assert.Equal("requests.origin.local", RequestPresentation.Origin(false, null, T));
    }

    [Fact]
    public void Pricing_source_badge_tones()
    {
        Assert.Equal("orange", RequestPresentation.PricingSource("provider_override", null, T).Tone);
        Assert.Equal(("accent", "pricingSource.provider|key=k"), RequestPresentation.PricingSource("provider_price", "k", T));
        Assert.Equal("neutral", RequestPresentation.PricingSource("system_default", null, T).Tone);
        Assert.Equal("red", RequestPresentation.PricingSource(null, null, T).Tone);
    }

    [Fact]
    public void Truncated_means_the_part_is_bigger_than_the_preview_limit()
    {
        Assert.True(RequestPresentation.IsTruncated(100, 50));
        Assert.False(RequestPresentation.IsTruncated(50, 50));
        Assert.False(RequestPresentation.IsTruncated(null, 50));
        Assert.False(RequestPresentation.IsTruncated(100, null));
    }

    [Fact]
    public void Elapsed_never_goes_negative()
    {
        var t0 = DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("1500 ms", RequestPresentation.Elapsed(t0, t0.AddMilliseconds(1500)));
        Assert.Equal("0 ms", RequestPresentation.Elapsed(t0.AddSeconds(5), t0));
        Assert.Equal("12.0 s", RequestPresentation.Elapsed(t0, t0.AddSeconds(12)));
    }

    /// <summary>Every key the page asks for exists in the generated catalog (same keys as web/src/i18n/messages.ts).</summary>
    [Fact]
    public void Every_key_the_requests_page_uses_is_in_the_catalog()
    {
        string[] keys =
        [
            "nav.requests", "requests.total", "requests.totalCost", "requests.totalCost.hint", "requests.rate.rpm", "requests.rate.tpm", "requests.rate.hint", "requests.elapsed",
            "requests.searchModel", "requests.clearFilters", "requests.filteredEmpty", "requests.filteredEmpty.detail", "requests.returnedModel", "requests.modelMatch",
            "requests.modelMismatch", "requests.modelComparison", "requests.modelNotRecorded", "requests.modelNotRecorded.hint", "requests.modelPending", "requests.modelPending.hint",
            "requests.mappedModel.hint", "requests.allClients", "requests.allTokens", "requests.allProviders", "requests.allStatus", "requests.allOrigins", "requests.col.time",
            "requests.col.client", "requests.col.model", "requests.col.token", "requests.col.provider", "requests.col.status", "requests.col.ttft", "requests.col.tps",
            "requests.col.tokens", "requests.col.cache", "requests.col.cost", "requests.col.origin", "requests.empty", "requests.empty.detail", "requests.page", "requests.filters",
            "requests.openDetail", "requests.viewError", "requests.direct", "requests.origin.remote", "requests.origin.local", "requests.origin.remoteFrom", "requests.detail.title",
            "requests.detail.summary", "requests.detail.account", "requests.detail.protocol", "requests.detail.requestedModel", "requests.detail.upstreamModel",
            "requests.detail.responseModel", "requests.detail.systemModel", "requests.detail.reasoning", "requests.detail.stream", "requests.detail.upstreamId", "requests.detail.timing",
            "requests.detail.generation", "requests.detail.total", "requests.detail.tps", "requests.detail.error", "requests.detail.privacy", "requests.detail.privacyBlocked",
            "requests.detail.privacyDryRun", "requests.detail.privacyRedacted", "requests.detail.billing", "requests.detail.usageMissing", "requests.detail.usageRaw",
            "requests.detail.pricingSnapshot", "requests.body.clientRequest", "requests.body.upstreamRequest", "requests.body.upstreamRequestHeaders", "requests.body.upstreamResponse",
            "requests.body.clientResponse", "requests.body.truncated", "requests.body.loadAll", "requests.reasoning", "requests.reasoning.notRecorded", "requests.reasoning.budget",
            "requests.reasoning.hint", "requests.routing.grade", "requests.routing.judgeMs", "requests.routing.fallback", "requests.routing.moved", "requests.routing.hint.judge",
            "requests.routing.hint.reused", "requests.routing.hint.fallback", "requests.routing.hint.moved", "status.success", "status.blocked", "status.upstream_error",
            "status.gateway_error", "status.client_cancelled", "status.pending", "billing.type", "billing.tokens", "billing.unitPrice", "billing.tier", "billing.cost",
            "usage.title", "usage.inputTotal", "usage.outputTotal", "usage.input", "usage.output", "usage.uncachedInput", "usage.regularOutput", "usage.cacheRead", "usage.cacheWrite",
            "usage.cacheWrite5m", "usage.cacheWrite1h", "usage.reasoning", "usage.inputAudio", "usage.outputAudio", "usage.inputImage", "usage.outputImage", "usage.hitShort",
            "usage.writeShort", "usage.reasoningShort", "usage.hitRate", "usage.noCache", "usage.missing", "usage.hitTooltip", "usage.writeTooltip", "usage.totalsHint",
            "pricingSource.override", "pricingSource.provider", "pricingSource.default", "pricingSource.none", "traces.openTrace", "traces.node.routing", "traces.kv.routeSource",
            "traces.kv.routeMode", "traces.kv.routeConfidence", "traces.kv.routeFallback", "traces.kv.routeAdjusted", "traces.route.source.judge", "traces.route.source.pin",
            "traces.route.source.turn", "traces.route.source.fallback", "traces.route.source.breaker", "traces.route.mode.session", "traces.route.mode.turn",
            "smartRouting.unusable.unknown", "settings.privacy.noHits", "common.refresh", "common.close", "common.copy", "common.error", "common.retry", "common.yes", "common.no",
            "common.clearSearch", "common.noResults",
        ];
        foreach (var action in new[] { "warn", "block", "redact" }) Assert.NotNull(RequestPresentation.PrivacyActionKey(action));
        var missing = keys.Concat(new[] { "warn", "block", "redact" }.Select(a => RequestPresentation.PrivacyActionKey(a)!))
            .Concat(new[] { "api_key", "custom", "national_id" }.Select(c => RequestPresentation.PrivacyCategoryKey(c)!))
            .Where(k => !Loc.Has(k)).ToList();
        Assert.Empty(missing);
    }
}

public class RequestFormatTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(62.94, "62.9 tok/s")]
    [InlineData(0, "0.0 tok/s")]
    [InlineData(-1, "—")]
    [InlineData(double.NaN, "—")]
    public void Tps(double v, string expected) => Assert.Equal(expected, RequestFormat.Tps(v));

    [Fact]
    public void Tps_of_nothing_is_a_dash() => Assert.Equal("—", RequestFormat.Tps(null));

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(20480L, "20 KB")]
    [InlineData(3_145_728L, "3.0 MB")]
    public void Bytes(long n, string expected) => Assert.Equal(expected, RequestFormat.Bytes(n));

    [Fact]
    public void Time_is_seconds_today_and_date_otherwise()
    {
        var today = DateTimeOffset.Parse("2026-10-26T11:57:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var earlier = DateTimeOffset.Parse("2026-10-25T09:05:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("11:57:00", RequestFormat.Time(today, "zh", Now, Utc));
        Assert.Equal("10/25 09:05", RequestFormat.Time(earlier, "zh", Now, Utc));
        Assert.Equal("10/25, 09:05", RequestFormat.Time(earlier, "en", Now, Utc));
    }

    [Fact]
    public void Today_is_judged_in_the_viewers_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(8), "t", "t");
        // 11:57 UTC and 12:00 UTC are both the 26th at +8, but 23:30 UTC the day before is the 26th 07:30 there.
        var lateYesterdayUtc = DateTimeOffset.Parse("2026-10-25T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("07:30:00", RequestFormat.Time(lateYesterdayUtc, "zh", Now, zone));
    }

    [Fact]
    public void Date_time()
    {
        var at = DateTimeOffset.Parse("2026-10-26T11:57:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("2026/10/26 11:57", RequestFormat.DateTime(at, "zh", Utc));
        Assert.Equal("10/26/2026, 11:57", RequestFormat.DateTime(at, "en", Utc));
        Assert.Equal("—", RequestFormat.DateTime(null, "zh", Utc));
    }

    [Theory]
    [InlineData("3", "$3")]
    [InlineData("0.075", "$0.075")]
    [InlineData("1.250", "$1.25")]
    [InlineData("0.00001", "$0.00001")]
    [InlineData("12.3456789012", "$12.345679")]
    [InlineData("", "—")]
    [InlineData("abc", "abc")]
    public void Unit_price(string input, string expected) => Assert.Equal(expected, RequestFormat.UnitPrice(input));

    [Fact]
    public void Pretty_reindents_json_text_and_leaves_other_text_alone()
    {
        Assert.Equal("{\n  \"a\": 1,\n  \"b\": [\n    true\n  ]\n}", RequestFormat.Pretty("{\"a\":1,\"b\":[true]}").Replace("\r\n", "\n"));
        Assert.Equal("not json {", RequestFormat.Pretty("not json {"));
        Assert.Equal("", RequestFormat.Pretty((string?)null));
        Assert.Equal("", RequestFormat.Pretty((JsonNode?)null));
        Assert.Equal("{\n  \"x\": \"中文<\"\n}", RequestFormat.Pretty(JsonNode.Parse("{\"x\":\"中文<\"}")).Replace("\r\n", "\n"));
        // A node holding a string is treated as text, which may itself be JSON (the usage blob of some upstreams).
        Assert.Equal("[\n  1\n]", RequestFormat.Pretty(JsonValue.Create("[1]")).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(3.44, "3.4")]
    [InlineData(99.95, "100.0")]
    [InlineData(250.0, "250")]
    public void Rate_text(double n, string expected) => Assert.Equal(expected, RequestFormat.RateText(n));
}
