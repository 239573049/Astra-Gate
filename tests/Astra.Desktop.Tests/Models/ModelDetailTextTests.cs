using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;

namespace Astra.Desktop.Tests.Models;

// Reads the global Loc language, which the Loc collection's tests switch: run with them, not beside them.
[Collection("Loc")]
public class ModelDetailTextTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Date_time_follows_the_two_intl_layouts()
    {
        Assert.Equal("2026/10/06 12:00", ModelDetailText.DateTime(At, "zh", TimeZoneInfo.Utc));
        Assert.Equal("10/06/2026, 12:00", ModelDetailText.DateTime(At, "en", TimeZoneInfo.Utc));
        Assert.Equal("—", ModelDetailText.DateTime(null, "zh"));
    }

    [Fact]
    public void Date_time_is_shown_in_the_requested_zone()
    {
        var shanghai = TimeZoneInfo.CreateCustomTimeZone("s", TimeSpan.FromHours(8), "s", "s");
        Assert.Equal("2026/10/06 20:00", ModelDetailText.DateTime(At, "zh", shanghai));
        Assert.Equal("2026/10/07 00:05", ModelDetailText.DateTime(At.AddHours(4).AddMinutes(5), "zh", shanghai));
    }

    [Fact]
    public void Subtitle_is_id_source_and_update_time()
    {
        var m = new ModelDto { Id = "gpt-4.1", Source = "seed", UpdatedAt = At };
        Assert.Equal($"gpt-4.1 · {Loc.T("models.source.seed")} · {Loc.T("models.updated", ("time", ModelDetailText.DateTime(At, Loc.Language, TimeZoneInfo.Utc)))}", ModelDetailText.Subtitle(m, Loc.Language, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Price_detail_joins_summary_upstream_and_rule_flags()
    {
        var pricing = JsonNode.Parse("""{"base":{"input":3,"output":15},"time_windows":[{"name":"x"}],"context_tiers":[{"name":"y"}]}""");
        Assert.Equal($"$3 / $15 · up-model · {Loc.T("models.hasTimeWindows")} · {Loc.T("models.hasTiers")}", ModelDetailText.PriceDetail(pricing, "up-model"));
        Assert.Equal("$3 / $15", ModelDetailText.PriceDetail(JsonNode.Parse("""{"base":{"input":3,"output":15},"time_windows":[],"context_tiers":null}"""), null));
        Assert.Equal("—", ModelDetailText.PriceDetail(null, ""));
        Assert.Equal("— · u", ModelDetailText.PriceDetail(JsonNode.Parse("""{"base":{}}"""), "u"));
    }

    [Fact]
    public void Override_detail_is_model_and_summary()
    {
        var o = new ProviderOverrideRefDto("p", "P", 1, "gpt-4.1", JsonNode.Parse("""{"base":{"input":1.5,"output":6}}"""));
        Assert.Equal("gpt-4.1 · $1.5 / $6", ModelDetailText.OverrideDetail(o));
    }
}
