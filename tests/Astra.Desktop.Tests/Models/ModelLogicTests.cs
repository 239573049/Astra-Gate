using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Models;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Models;

// Reads the global Loc language, which the Loc collection's tests switch: run with them, not beside them.
[Collection("Loc")]
public class ModelLogicTests
{
    private static ModelDto Model(string id = "gpt-4.1", string vendor = "openai", string pricing = """{"base":{"input":2,"output":8}}""", int providerPrices = 1, string source = "seed", params string[] modified)
        => new()
        {
            Id = id, DisplayName = id.ToUpperInvariant(), Vendor = vendor, ContextWindow = 1_047_576, Source = source, Enabled = true,
            Pricing = JsonNode.Parse(pricing), UserModifiedFields = [.. modified],
            ProviderPriceKeys = Enumerable.Range(0, providerPrices).Select(i => "k" + i).ToList(),
            Aliases = ["a", "b"], Capabilities = new ModelCapabilities { Vision = true, Tools = false },
        };

    // ---------- rows ----------

    [Fact]
    public void Row_values_and_texts()
    {
        var row = ModelRow.From(Model());
        Assert.Equal("gpt-4.1", row.Id);
        Assert.Equal(2m, row.Input);
        Assert.Equal(1_047_576, row.Context);
        Assert.Equal("1.05M", row.ContextText);
        Assert.Equal("$2 / $8", row.PriceText);
        Assert.Equal(1, row.ProviderPrices);
        Assert.False(row.IsUserModel);
        Assert.False(row.IsModified);
        Assert.False(row.IsDisabled);
    }

    [Fact]
    public void Row_without_pricing_or_windows()
    {
        var m = new ModelDto { Id = "x", DisplayName = "X", Vendor = "v", Enabled = false, Source = "user", UserModifiedFields = ["pricing"] };
        var row = ModelRow.From(m);
        Assert.Equal(0, row.Context);
        Assert.Equal(0m, row.Input);
        Assert.Equal("—", row.ContextText);
        Assert.Equal("—", row.PriceText);
        Assert.Equal("—", row.ProviderPricesText);
        Assert.True(row.IsUserModel);
        Assert.True(row.IsModified);
        Assert.True(row.IsDisabled);
    }

    [Fact]
    public void Provider_price_count_text_uses_the_catalog()
    {
        Assert.Equal(Loc.T("models.providerPriceCount", ("count", 3)), ModelRow.From(Model(providerPrices: 3)).ProviderPricesText);
    }

    [Fact]
    public void Vendors_are_unique_and_sorted_by_code_unit()
    {
        var all = new[] { Model(vendor: "openai"), Model(vendor: "anthropic"), Model(vendor: "openai"), Model(vendor: "Zed"), Model(vendor: "google") };
        Assert.Equal(["Zed", "anthropic", "google", "openai"], ModelCatalog.Vendors(all));
        Assert.Empty(ModelCatalog.Vendors(null));
    }

    [Fact]
    public void List_and_detail_paths()
    {
        Assert.Equal("/api/models", ModelCatalog.ListPath("", ""));
        Assert.Equal("/api/models?search=gpt%205", ModelCatalog.ListPath("gpt 5", null));
        Assert.Equal("/api/models?search=a&vendor=openai", ModelCatalog.ListPath("a", "openai"));
        Assert.Equal("/api/models/models%2Ffoo%20bar", ModelCatalog.DetailPath("models/foo bar"));
    }

    // ---------- form ----------

    [Fact]
    public void Form_round_trips_a_saved_model_without_being_dirty()
    {
        var saved = ModelForm.From(Model());
        Assert.Equal("a, b", saved.AliasText);
        Assert.False(saved.IsDirty(ModelForm.From(Model())));
        var input = saved.ToInput();
        Assert.Equal(["a", "b"], input.Aliases);
        Assert.Equal("GPT-4.1", input.DisplayName);
        Assert.Equal(1_047_576, input.ContextWindow);
        Assert.True(input.Enabled);
        Assert.Equal("""{"base":{"input":2,"output":8}}""", input.Pricing!.ToJsonString());
    }

    [Fact]
    public void Each_field_makes_the_form_dirty()
    {
        var saved = ModelForm.From(Model());
        Assert.True((saved with { DisplayName = "x" }).IsDirty(saved));
        Assert.True((saved with { Vendor = "x" }).IsDirty(saved));
        Assert.True(saved.WithFamily("fam").IsDirty(saved));
        Assert.True((saved with { AliasText = "a, b, c" }).IsDirty(saved));
        Assert.True((saved with { ContextWindow = 5 }).IsDirty(saved));
        Assert.True((saved with { MaxOutputTokens = 5 }).IsDirty(saved));
        Assert.True(saved.WithCapability("pdf", true).IsDirty(saved));
        Assert.True((saved with { Pricing = null }).IsDirty(saved));
        Assert.True((saved with { Enabled = false }).IsDirty(saved));
    }

    [Fact]
    public void Alias_spelling_alone_is_not_a_change()
    {
        var saved = ModelForm.From(Model());
        Assert.False((saved with { AliasText = "a,b" }).IsDirty(saved));
        Assert.False((saved with { AliasText = " a ,\n b ,, " }).IsDirty(saved));
        Assert.Equal(["a", "b"], (saved with { AliasText = "a\nb" }).Aliases);
    }

    [Fact]
    public void Number_spelling_of_a_price_is_not_a_change_but_its_value_is()
    {
        var saved = ModelForm.From(Model());
        Assert.False((saved with { Pricing = JsonNode.Parse("""{"base":{"input":2.0,"output":8.00}}""") }).IsDirty(saved));
        Assert.True((saved with { Pricing = JsonNode.Parse("""{"base":{"input":2.000001,"output":8}}""") }).IsDirty(saved));
    }

    [Fact]
    public void Unchecking_then_checking_a_null_flag_stays_dirty_like_the_web()
    {
        var saved = ModelForm.From(Model());
        Assert.Null(ModelCapabilityList.Get(saved.Capabilities, "pdf"));
        Assert.True(saved.WithCapability("pdf", false).IsDirty(saved)); // JSON null becomes false
        Assert.False(saved.WithCapability("vision", true).IsDirty(saved)); // already true
    }

    [Fact]
    public void Empty_family_is_null()
    {
        Assert.Null(ModelForm.From(Model()).WithFamily("").Family);
        Assert.Equal("f", ModelForm.From(Model()).WithFamily("f").Family);
    }

    [Fact]
    public void Form_pricing_is_a_copy()
    {
        var m = Model();
        var form = ModelForm.From(m);
        m.Pricing!["base"]!["input"] = 99;
        Assert.Equal("""{"base":{"input":2,"output":8}}""", form.Pricing!.ToJsonString());
        var input = form.ToInput();
        input.Pricing!["base"]!["input"] = 7;
        Assert.Equal("""{"base":{"input":2,"output":8}}""", form.Pricing.ToJsonString());
    }

    [Fact]
    public void Capability_list_is_the_seven_checkboxes_in_order()
    {
        Assert.Equal(["vision", "tools", "reasoning", "promptCache", "audio", "pdf", "structuredOutput"], ModelCapabilityList.All.Select(c => c.Key));
        Assert.All(ModelCapabilityList.All, c => Assert.True(Loc.Has(c.LabelKey)));
    }

    // ---------- create ----------

    [Fact]
    public void Create_needs_an_id_and_a_vendor()
    {
        var d = new CreateModelDraft();
        Assert.False(d.CanSubmit);
        Assert.Null(d.ToInput());
        d.Id = " my-model ";
        Assert.False(d.CanSubmit);
        d.Vendor = "openai";
        Assert.True(d.CanSubmit);
        d.Vendor = "  ";
        Assert.False(d.CanSubmit);
    }

    [Fact]
    public void Create_request_follows_the_web_sheet()
    {
        var d = new CreateModelDraft { Id = " my-model ", Vendor = " openai ", ContextWindow = 128_000 };
        var input = d.ToInput()!;
        Assert.Equal("my-model", input.Id);
        Assert.Equal("my-model", input.DisplayName); // empty display name falls back to the id
        Assert.Equal("openai", input.Vendor);
        Assert.Equal(128_000, input.ContextWindow);
        Assert.Null(input.Pricing);

        d.DisplayName = " My Model ";
        d.InputPrice = 0.000003m;
        Assert.Equal("My Model", d.ToInput()!.DisplayName);
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":0.000003,"output":null}}""", d.ToInput()!.Pricing!.ToJsonString());

        d.InputPrice = null;
        d.OutputPrice = 15m;
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":null,"output":15}}""", d.ToInput()!.Pricing!.ToJsonString());
    }

    [Fact]
    public void After_creating_only_id_and_name_are_cleared()
    {
        var d = new CreateModelDraft { Id = "a", DisplayName = "A", Vendor = "v", ContextWindow = 5, InputPrice = 1 };
        d.ResetAfterCreate();
        Assert.Equal("", d.Id);
        Assert.Equal("", d.DisplayName);
        Assert.Equal("v", d.Vendor);
        Assert.Equal(5, d.ContextWindow);
        Assert.Equal(1m, d.InputPrice);
    }

    // ---------- sync ----------

    private static SyncChangeDto Change(string id, string model, string kind = "price", string? key = "openai", string? field = null, string? before = null, string? after = null, bool skipped = false)
        => new(id, model, key, kind, field, before is null ? null : JsonNode.Parse(before), after is null ? null : JsonNode.Parse(after), skipped);

    [Fact]
    public void Flatten_lists_leaves_and_drops_nulls()
    {
        var leaves = ModelSync.Flatten(JsonNode.Parse("""{"base":{"input":1,"x":null},"tiers":[1,2],"empty":{},"n":"s"}"""));
        Assert.Equal(["base.input", "tiers", "empty", "n"], leaves.Select(l => l.Key));
        Assert.Single(ModelSync.Flatten(JsonNode.Parse("5")));
        Assert.Empty(ModelSync.Flatten(null));
    }

    [Fact]
    public void Diff_shows_only_the_leaves_that_changed()
    {
        var lines = ModelSync.DiffLines(
            JsonNode.Parse("""{"pricing":{"base":{"input":0.5,"output":2}},"x":1}"""),
            JsonNode.Parse("""{"pricing":{"base":{"input":0.6,"output":2},"per_request":0.01},"x":1}"""));
        Assert.Equal(["base.input", "per_request"], lines.Select(l => l.Path));
        Assert.Equal("0.5", SyncDiffLine.LeafText(lines[0].Before));
        Assert.Equal("0.6", SyncDiffLine.LeafText(lines[0].After));
        Assert.Null(lines[1].Before);
        Assert.Equal("0.01", SyncDiffLine.LeafText(lines[1].After));
    }

    [Fact]
    public void Diff_lists_removed_leaves_after_added_ones_and_spelling_is_not_a_change()
    {
        var lines = ModelSync.DiffLines(JsonNode.Parse("""{"a":1,"b":2.0}"""), JsonNode.Parse("""{"b":2,"c":3}"""));
        Assert.Equal(["c", "a"], lines.Select(l => l.Path));
        Assert.Null(lines[1].After);
        Assert.Equal("1", SyncDiffLine.LeafText(lines[1].Before));
    }

    [Fact]
    public void Scalar_diff_has_an_empty_path_and_strings_print_as_written()
    {
        var lines = ModelSync.DiffLines(JsonNode.Parse("\"Old\""), JsonNode.Parse("\"New\""));
        var line = Assert.Single(lines);
        Assert.Equal("", line.Path);
        Assert.Equal("Old", SyncDiffLine.LeafText(line.Before));
        Assert.Equal("New", SyncDiffLine.LeafText(line.After));
        Assert.Empty(ModelSync.DiffLines(JsonNode.Parse("\"same\""), JsonNode.Parse("\"same\"")));
    }

    [Fact]
    public void Tooltip_pretty_prints_both_sides()
    {
        var tip = ModelSync.Tooltip(JsonNode.Parse("""{"a":1}"""), null);
        Assert.Equal("{\n  \"a\": 1\n}\n→\n∅", tip);
        Assert.Equal("plain\n→\n{\n  \"b\": 2\n}", ModelSync.Tooltip(JsonValue.Create("plain"), JsonValue.Create("""{"b":2}""")));
    }

    [Fact]
    public void Labels_per_kind()
    {
        Assert.Equal(Loc.T("models.sync.newModel"), ModelSync.Label(Change("1", "m", "new_model")));
        Assert.Equal(Loc.T("models.sync.price", ("key", "openai")), ModelSync.Label(Change("1", "m", "price")));
        Assert.Equal(Loc.T("models.sync.field", ("field", "displayName")), ModelSync.Label(Change("1", "m", "field", field: "displayName")));
    }

    [Fact]
    public void Changes_group_by_model_in_first_seen_order()
    {
        var groups = ModelSync.Group([Change("1", "b"), Change("2", "a"), Change("3", "b", "field", field: "f")]);
        Assert.Equal(["b", "a"], groups.Select(g => g.ModelId));
        Assert.Equal(["1", "3"], groups[0].Ids);
        Assert.False(groups[0].IsSingle);
        Assert.True(groups[1].IsSingle);
    }

    [Fact]
    public void A_preview_starts_with_everything_but_user_edited_changes_ticked()
    {
        var preview = new SyncPreviewDto("models.dev", DateTimeOffset.UnixEpoch, [Change("1", "a"), Change("2", "a", skipped: true), Change("3", "b")]);
        Assert.Equal(["1", "3"], ModelSync.InitialSelection(preview).Order());
    }

    [Fact]
    public void Checkbox_states_and_toggles()
    {
        var sel = new SyncSelection(["1"]);
        string[] all = ["1", "2", "3"];
        Assert.Equal(CheckState.Mixed, sel.StateOf(all));
        Assert.Equal(CheckState.Checked, sel.StateOf(["1"]));
        Assert.Equal(CheckState.Unchecked, sel.StateOf(["2", "3"]));
        Assert.Equal(CheckState.Unchecked, sel.StateOf([]));

        sel.Toggle(all); // mixed -> all on
        Assert.Equal(CheckState.Checked, sel.StateOf(all));
        Assert.Equal(3, sel.Count);
        sel.Toggle(all); // all on -> all off
        Assert.Equal(CheckState.Unchecked, sel.StateOf(all));
        Assert.Equal(0, sel.Count);
        sel.Toggle(["2"]);
        Assert.True(sel.Contains("2"));
        Assert.Equal(["2"], sel.Ids());
    }

    [Fact]
    public void Selection_summary_uses_the_catalog_text()
    {
        var sel = new SyncSelection(["1", "2"]);
        Assert.Equal(Loc.T("models.sync.selectedOf", ("n", 2), ("total", 9), ("models", 4)), sel.Summary(9, 4));
    }
}
