using System.Text.Json.Nodes;
using Astra.Desktop.Core.Plugins.V2;

namespace Astra.Desktop.Tests.PluginUi;

/// <summary>The desktop side of the plugin UI protocol, on the same cases the web shell runs (tests/fixtures/plugin-ui-behavior).</summary>
public class PluginUiProtocolTests
{
    private static JsonObject Fixture(string name) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PluginUi", name)))!;

    [Fact]
    public void Patches_apply_as_the_host_computes_them()
    {
        foreach (var c in Fixture("patch.json")["cases"]!.AsArray().OfType<JsonObject>())
        {
            var after = PluginUiProtocol.ApplyPatch((JsonObject)c["before"]!, (JsonArray)c["ops"]!);
            Assert.True(JsonNode.DeepEquals(c["after"], after), c["name"]!.GetValue<string>() + ": " + after.ToJsonString());
        }
    }

    [Fact]
    public void Local_conditions_evaluate_as_on_the_web()
    {
        foreach (var c in Fixture("conditions.json")["cases"]!.AsArray().OfType<JsonObject>())
        {
            var local = ((JsonObject)c["local"]!).ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
            Assert.True(PluginUiProtocol.EvalWhen(c["expr"], local) == c["result"]!.GetValue<bool>(), c["expr"]!.ToJsonString());
        }
    }

    [Fact]
    public void Axis_ticks_match_the_web()
    {
        foreach (var c in Fixture("ticks.json")["cases"]!.AsArray().OfType<JsonObject>())
        {
            var ticks = PluginUiProtocol.NiceTicks(c["min"]!.GetValue<double>(), c["max"]!.GetValue<double>(), c["count"]!.GetValue<int>());
            Assert.Equal(c["ticks"]!.AsArray().Select(t => t!.GetValue<double>()), ticks);
        }
    }

    [Fact]
    public void Every_schema_component_is_rendered()
    {
        var schema = Fixture("components.json")["components"]!.AsObject().Select(kv => kv.Key).Order(StringComparer.Ordinal);
        Assert.Equal(schema, PluginUiProtocol.Components.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Formatting_and_dates()
    {
        Assert.Equal("$0.0123", PluginUiProtocol.Format("money", JsonValue.Create(12_345_000), "en"));
        Assert.Equal("1.5 KB", PluginUiProtocol.Format("bytes", JsonValue.Create(1536), "en"));
        Assert.Equal("320 ms", PluginUiProtocol.Format("duration", JsonValue.Create(320), "en"));
        Assert.Equal(new DateOnly(2026, 1, 2), PluginUiProtocol.ResolveDate("2026-01-02"));
        var week = PluginUiProtocol.Preset("7d")!.Value;
        Assert.Equal(6, week.To.DayNumber - week.From.DayNumber);
        Assert.Equal("compact", PluginUiProtocol.WidthClass(300));
        Assert.Equal(4, PluginUiProtocol.Responsive(JsonNode.Parse("""{"compact":1,"regular":2,"wide":4}"""), "wide")!.GetValue<int>());
    }

    [Fact]
    public void A_qr_code_has_its_finder_patterns()
    {
        var m = QrEncoder.Encode("astra://join?gateway=https://x.example&token=abc")!;
        var n = m.GetLength(0);
        Assert.True(n >= 21 && (n - 17) % 4 == 0);
        foreach (var (r, c) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
        {
            Assert.True(m[r, c] && m[r + 6, c + 6] && m[r + 3, c + 3]);
            Assert.False(m[r + 1, c + 1]);
        }
        Assert.Null(QrEncoder.Encode(new string('x', 400)));
    }
}
