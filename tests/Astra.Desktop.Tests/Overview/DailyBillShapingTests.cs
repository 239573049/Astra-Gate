using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

// The same bill and expectations as web/src/test/bill.test.ts: the two clients must print identical text.
public sealed class DailyBillShapingTests
{
    private static readonly DailyBillDto Bill = new(
        "2026-11-11", 480,
        DateTimeOffset.Parse("2026-11-10T16:00:00Z"), DateTimeOffset.Parse("2026-11-11T10:42:07Z"), DateTimeOffset.Parse("2026-11-11T10:42:07Z"),
        32.4m, 878, 872, 8_728_100, 648_220,
        [
            new("anthropic", "Anthropic", 29.45m, 540, 6_041_500, 372_520,
            [
                new("claude-opus-5-5", 18.42m, 42, 1_284_300, 96_420),
                new("claude-haiku-5-5", 0.0123m, 312, 8_452, 61_300),
            ]),
            new("openai", "OpenAI 中转", 2.95m, 338, 2_686_600, 275_700, [new("gpt-5", 2.95m, 338, 2_686_600, 275_700)]),
        ]);

    [Fact]
    public void Prints_the_day_window_and_totals_in_the_bills_own_offset()
    {
        var v = DailyBillShaping.Shape(Bill, "zh");
        Assert.Equal("AG-20261111", v.Number);
        Assert.Equal("周三", v.Weekday);
        Assert.Equal("11.11", v.DateShort);
        Assert.Equal("00:00 – 18:42", v.Window);
        Assert.Equal("18:42", v.Until);
        Assert.Equal("2026-11-11 18:42", v.GeneratedAt);
        Assert.Equal("$32.40", v.Total);
        Assert.Equal("878", v.Requests);
        Assert.Equal("99.3%", v.SuccessRate);
        Assert.Equal("8,728,100", v.InputTokens);
        Assert.Equal("648,220", v.OutputTokens);
        Assert.Equal("9.4M", v.TokensCompact);
        Assert.Equal("Wed", DailyBillShaping.Shape(Bill, "en").Weekday);
    }

    [Fact]
    public void Lists_providers_and_models_with_compact_token_counts()
    {
        var providers = DailyBillShaping.Shape(Bill, "zh").Providers;
        var a = providers[0];
        Assert.Equal(("anthropic", "ANTHROPIC", "$29.45", "540"), (a.Id, a.Upper, a.Cost, a.Requests));
        Assert.Equal(
            [new BillModelLine("claude-opus-5-5", "$18.42", "42", "1.3M", "96.4K"), new BillModelLine("claude-haiku-5-5", "$0.0123", "312", "8,452", "61.3K")],
            a.Models);
        Assert.Equal("OPENAI 中转", providers[1].Upper);
        Assert.Equal(29.45 / 32.4, a.Share, 6);
    }

    [Fact]
    public void Names_the_leading_provider_with_a_whole_percent()
        => Assert.Equal(new BillLeader("Anthropic", "91%"), DailyBillShaping.Shape(Bill, "zh").Top);

    [Fact]
    public void Ends_a_finished_day_at_24_00_and_shares_by_requests_when_nothing_cost_anything()
    {
        var free = Bill with
        {
            ToUtc = DateTimeOffset.Parse("2026-11-11T16:00:00Z"),
            CostUsd = 0,
            Providers = Bill.Providers.Select(p => p with { CostUsd = 0, Models = p.Models.Select(m => m with { CostUsd = 0 }).ToList() }).ToList(),
        };
        var v = DailyBillShaping.Shape(free, "zh");
        Assert.Equal("00:00 – 24:00", v.Window);
        Assert.Equal("$0", v.Total);
        Assert.Equal(new BillLeader("Anthropic", "62%"), v.Top);
    }

    [Fact]
    public void Has_no_leader_and_a_dash_for_the_success_rate_on_an_empty_day()
    {
        var v = DailyBillShaping.Shape(Bill with { CostUsd = 0, Requests = 0, SuccessRequests = 0, InputTokens = 0, OutputTokens = 0, Providers = [] }, "en");
        Assert.Empty(v.Providers);
        Assert.Null(v.Top);
        Assert.Equal("—", v.SuccessRate);
        Assert.Equal("0", v.TokensCompact);
    }

    [Fact]
    public void Names_the_saved_file_after_the_day_and_the_style()
        => Assert.Equal("astra-bill-2026-11-11-manga.png", DailyBillShaping.FileName("2026-11-11", BillStyles.Manga));

    [Fact]
    public void The_style_is_remembered_under_billStyle_and_defaults_to_the_receipt()
    {
        var store = new MemoryStore();
        BillPrefs.Use(store);
        try
        {
            Assert.Equal(BillStyles.Receipt, BillPrefs.Style);
            BillPrefs.Style = BillStyles.Manga;
            Assert.Equal("manga", store.Values[BillPrefs.Key]!.GetValue<string>());
            BillPrefs.Use(null);
            BillPrefs.Use(store);
            Assert.Equal(BillStyles.Manga, BillPrefs.Style);
            BillPrefs.Style = "nonsense";
            Assert.Equal(BillStyles.Receipt, BillPrefs.Style);
            Assert.False(store.Values.ContainsKey(BillPrefs.Key));
        }
        finally
        {
            BillPrefs.Use(null);
        }
    }

    private sealed class MemoryStore : IBillPrefsStore
    {
        public Dictionary<string, JsonNode?> Values { get; } = [];

        public JsonNode? Get(string key) => Values.TryGetValue(key, out var v) ? v?.DeepClone() : null;

        public void Set(string key, JsonNode? value)
        {
            if (value is null) Values.Remove(key);
            else Values[key] = value.DeepClone();
        }
    }
}
