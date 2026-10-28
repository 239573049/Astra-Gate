using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Tokens;

public sealed class BudgetAndSettingsTests
{
    [Fact]
    public void A_new_budget_row_starts_on_the_first_free_metric_and_period()
    {
        List<DraftBudget> rows = [];
        Assert.Equal(new DraftBudget("cost", "day", null), BudgetRules.NextFree(rows));
        rows.Add(new DraftBudget("cost", "day", 5));
        Assert.Equal(new DraftBudget("cost", "week", null), BudgetRules.NextFree(rows));
        rows.Add(new DraftBudget("cost", "week", 5));
        rows.Add(new DraftBudget("cost", "month", 5));
        rows.Add(new DraftBudget("cost", "total", 5));
        Assert.Equal(new DraftBudget("requests", "day", null), BudgetRules.NextFree(rows));
    }

    [Fact]
    public void Every_metric_and_period_taken_leaves_no_free_slot()
    {
        var all = BudgetRules.Metrics.SelectMany(m => BudgetRules.Periods.Select(p => new DraftBudget(m, p, 1))).ToList();
        Assert.Equal(12, all.Count);
        Assert.Null(BudgetRules.NextFree(all));
    }

    [Fact]
    public void Two_rows_on_the_same_metric_and_period_are_a_duplicate()
    {
        Assert.False(BudgetRules.HasDuplicate([new("cost", "day", 1), new("cost", "week", 1), new("tokens", "day", null)]));
        Assert.True(BudgetRules.HasDuplicate([new("cost", "day", 1), new("cost", "day", 2)]));
        Assert.False(BudgetRules.HasDuplicate([]));
    }

    [Fact]
    public void Limits_keep_positive_budgets_and_a_whole_rpm_and_are_null_when_unlimited()
    {
        Assert.Null(BudgetRules.LimitsJson([], null));
        Assert.Null(BudgetRules.LimitsJson([new("cost", "day", null), new("tokens", "day", 0)], 0));
        var json = (JsonObject)BudgetRules.LimitsJson([new("cost", "day", 5.25m), new("requests", "month", 5000), new("tokens", "total", null)], 59.9)!;
        Assert.Equal(59, (int)json["rpm"]!);
        var budgets = json["budgets"]!.AsArray();
        Assert.Equal(2, budgets.Count);
        Assert.Equal(("cost", "day", 5.25m), ((string?)budgets[0]!["metric"], (string?)budgets[0]!["period"], (decimal)budgets[0]!["limit"]!));
        // An rpm cap alone is a limit too; a budget alone leaves rpm null.
        Assert.Equal(10, (int)((JsonObject)BudgetRules.LimitsJson([], 10)!)["rpm"]!);
        Assert.Null(((JsonObject)BudgetRules.LimitsJson([new("cost", "day", 1)], null)!)["rpm"]);
    }

    [Fact]
    public void The_draft_of_a_token_reads_its_limits_clients_and_pool()
    {
        var token = Dtos.Token(providerId: "prov", limits: new TokenLimitsDto([new TokenBudgetDto("cost", "day", 5m)], 60), allowed: ["codex"]);
        var draft = TokenSettingsDraft.Of(token);
        Assert.Equal(("name", 60d), (draft.Name, draft.Rpm));
        Assert.Equal([new DraftBudget("cost", "day", 5m)], draft.Budgets);
        Assert.Equal(ClientsMode.Only, draft.Mode);
        Assert.Equal(["codex"], draft.Allowed);
        Assert.Equal("prov", draft.Pool.Members.Single().ProviderId);
        Assert.Equal(ClientsMode.Any, TokenSettingsDraft.Of(Dtos.Token()).Mode);
    }

    [Fact]
    public void Save_needs_a_name_and_no_duplicate_budgets()
    {
        var draft = TokenSettingsDraft.Of(Dtos.Token());
        Assert.True(draft.CanSave);
        Assert.False((draft with { Name = "  " }).CanSave);
        Assert.False((draft with { Budgets = [new("cost", "day", 1), new("cost", "day", 2)] }).CanSave);
    }

    [Fact]
    public void The_patch_replaces_name_pool_limits_and_allowed_clients()
    {
        var draft = TokenSettingsDraft.Of(Dtos.Token(providerId: "prov")) with
        {
            Name = "  new name ", Budgets = [new("cost", "day", 5)], Rpm = 30, Mode = ClientsMode.Only, Allowed = ["codex", "zed"],
        };
        var patch = draft.ToPatch(["prov"]);
        Assert.Equal("new name", (string?)patch["name"]);
        Assert.Equal("prov", (string?)patch["pool"]!["members"]![0]!["providerId"]);
        Assert.Equal(30, (int)patch["limits"]!["rpm"]!);
        Assert.Equal(["codex", "zed"], patch["allowedClients"]!.AsArray().Select(n => (string)n!));
        // Never both a provider and a pool: the body carries only the pool.
        Assert.False(patch.ContainsKey("providerId"));
        Assert.False(patch.ContainsKey("accountId"));

        var any = TokenSettingsDraft.Of(Dtos.Token()).ToPatch([]);
        Assert.True(any.ContainsKey("allowedClients"));
        Assert.Null(any["allowedClients"]);
        Assert.True(any.ContainsKey("limits"));
        Assert.Null(any["limits"]);
        Assert.Contains("\"allowedClients\":null", any.ToJsonString());
    }

    [Fact]
    public void Client_choices_keep_plugin_agents_that_were_already_chosen()
    {
        Assert.Equal(["a", "b", "plugin-x"], TokenSettingsDraft.ClientChoices(["a", "b"], ["b", "plugin-x"]));
    }

    [Fact]
    public void Limit_text_has_no_trailing_zeros()
    {
        Assert.Equal("5", TokenSettingsDraft.LimitText(5.00m));
        Assert.Equal("0.25", TokenSettingsDraft.LimitText(0.25m));
        Assert.Equal("", TokenSettingsDraft.LimitText(null));
    }
}
