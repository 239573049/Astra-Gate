using System.Text.Json.Nodes;
using Astra.Core.Alerts;

namespace Astra.Core.Tests;

public class AlertConditionsTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static AlertEvaluation? Eval(string eventId, string condition, string observation) =>
        AlertConditions.Evaluate(eventId, AlertConditions.Normalize(eventId, Obj(condition)), Obj(observation));

    [Fact]
    public void Normalize_Fills_Defaults_And_Rejects_What_Does_Not_Fit()
    {
        Assert.Equal(90, AlertConditions.Normalize(AlertEventIds.AccountQuotaHigh, null)["value"]!.GetValue<double>());
        Assert.Equal("any", AlertConditions.Normalize(AlertEventIds.AccountQuotaHigh, null)["window"]!.GetValue<string>());
        Assert.Empty(AlertConditions.Normalize(AlertEventIds.AccountAuthInvalid, null));
        Assert.Throws<ArgumentException>(() => AlertConditions.Normalize(AlertEventIds.ProviderBalanceLow, null)); // value required
        Assert.Throws<ArgumentException>(() => AlertConditions.Normalize(AlertEventIds.AccountQuotaHigh, Obj("""{"window":"daily"}""")));
        Assert.Throws<ArgumentException>(() => AlertConditions.Normalize(AlertEventIds.TokenUsageHigh, Obj("""{"value":-1}""")));
        Assert.Throws<ArgumentException>(() => AlertConditions.Normalize(AlertEventIds.TokenUsageHigh, Obj("""{"nope":1}""")));
        Assert.Throws<ArgumentException>(() => AlertConditions.Normalize("no.such.event", null));
        Assert.Equal("cost:month", AlertConditions.Normalize(AlertEventIds.TokenUsageHigh, Obj("""{"budget":" cost:month "}"""))["budget"]!.GetValue<string>());
    }

    [Fact]
    public void Provider_Balance_Compares_Remaining_Or_Used_Percent_Of_The_Chosen_Plan()
    {
        const string quota = """{"quota":{"isValid":true,"plans":[{"name":"CNY","unit":"CNY","remaining":3.2},{"name":"USD","unit":"USD","remaining":50,"used":90,"total":100}]}}""";
        var low = Eval(AlertEventIds.ProviderBalanceLow, """{"value":5}""", quota)!;
        Assert.True(low.Firing);
        Assert.Equal("CNY", low.Observed["unit"]!.GetValue<string>());
        Assert.False(Eval(AlertEventIds.ProviderBalanceLow, """{"value":5,"plan":"usd"}""", quota)!.Firing);
        var percent = Eval(AlertEventIds.ProviderBalanceLow, """{"metric":"usedPercent","value":80}""", quota)!;
        Assert.True(percent.Firing); // the first plan has no percent: the USD plan (90%) decides
        Assert.Equal(90, percent.Observed["usedPercent"]!.GetValue<double>());

        Assert.Null(Eval(AlertEventIds.ProviderBalanceLow, """{"value":5}""", """{"quota":null}"""));
        Assert.Null(Eval(AlertEventIds.ProviderBalanceLow, """{"value":5}""", """{"quota":{"isValid":false,"plans":[{"remaining":1}]}}"""));
        Assert.Null(Eval(AlertEventIds.ProviderBalanceLow, """{"value":5,"plan":"EUR"}""", quota));
    }

    [Fact]
    public void Failures_Count_From_The_Snapshot_Or_The_Account()
    {
        Assert.True(Eval(AlertEventIds.ProviderQuotaFailed, "{}", """{"quota":{"failures":3,"error":"boom"}}""")!.Firing);
        Assert.False(Eval(AlertEventIds.ProviderQuotaFailed, "{}", """{"quota":{"plans":[]}}""")!.Firing);
        Assert.Null(Eval(AlertEventIds.ProviderQuotaFailed, "{}", "{}"));
        Assert.True(Eval(AlertEventIds.AccountQuotaFailed, """{"value":1}""", """{"failures":1,"error":"x"}""")!.Firing);
        Assert.Null(Eval(AlertEventIds.AccountQuotaFailed, "{}", """{"status":"active"}"""));
    }

    [Fact]
    public void Account_Windows_And_Status()
    {
        const string obs = """{"status":"active","quota":{"session":{"usedPercent":40},"weekly":{"usedPercent":95,"resetsAtUtc":"2026-10-20T00:00:00Z"}}}""";
        var any = Eval(AlertEventIds.AccountQuotaHigh, "{}", obs)!;
        Assert.True(any.Firing);
        Assert.Equal("weekly", any.Observed["window"]!.GetValue<string>());
        Assert.False(Eval(AlertEventIds.AccountQuotaHigh, """{"window":"session"}""", obs)!.Firing);
        Assert.Null(Eval(AlertEventIds.AccountQuotaHigh, """{"window":"credits"}""", obs));
        Assert.Null(Eval(AlertEventIds.AccountQuotaHigh, "{}", """{"status":"active"}"""));

        Assert.False(Eval(AlertEventIds.AccountAuthInvalid, "{}", obs)!.Firing);
        Assert.True(Eval(AlertEventIds.AccountAuthInvalid, "{}", """{"status":"expired"}""")!.Firing);
        Assert.Null(Eval(AlertEventIds.AccountAuthInvalid, "{}", """{"quota":{}}"""));
    }

    [Fact]
    public void Token_Budgets_And_The_Rpm_Window()
    {
        const string obs = """{"budgets":[{"key":"cost:month","metric":"cost","period":"month","used":8.5,"limit":10,"usedPercent":85,"exhausted":false},{"key":"requests:day","metric":"requests","period":"day","used":10,"limit":10,"usedPercent":100,"exhausted":true}]}""";
        Assert.True(Eval(AlertEventIds.TokenUsageHigh, "{}", obs)!.Firing);
        var cost = Eval(AlertEventIds.TokenUsageHigh, """{"budget":"cost:month","value":90}""", obs)!;
        Assert.False(cost.Firing);
        Assert.Equal(85, cost.Observed["usedPercent"]!.GetValue<double>());
        Assert.Null(Eval(AlertEventIds.TokenUsageHigh, """{"budget":"tokens:total"}""", obs));

        var reached = Eval(AlertEventIds.TokenLimitReached, "{}", obs)!;
        Assert.True(reached.Firing);
        Assert.Equal("requests:day", reached.Observed["key"]!.GetValue<string>());
        Assert.False(Eval(AlertEventIds.TokenLimitReached, """{"budget":"cost:month"}""", obs)!.Firing);
        Assert.True(Eval(AlertEventIds.TokenLimitReached, "{}", """{"budgets":[],"rpmLimited":true}""")!.Firing);
        Assert.True(Eval(AlertEventIds.TokenLimitReached, """{"budget":"rpm"}""", """{"budgets":[],"rpmLimited":true}""")!.Firing);
        Assert.False(Eval(AlertEventIds.TokenLimitReached, "{}", """{"budgets":[]}""")!.Firing);
        Assert.Null(Eval(AlertEventIds.TokenLimitReached, "{}", "{}"));
    }

    [Fact]
    public void Every_Event_Has_A_Descriptor_And_Valid_Defaults()
    {
        foreach (var e in AlertEvents.All)
        {
            var hasRequired = e.Params.Any(p => p.Required);
            if (!hasRequired) AlertConditions.Normalize(e.Id, null);
            Assert.Contains(e.SubjectType, new[] { AlertSubjectTypes.Provider, AlertSubjectTypes.Account, AlertSubjectTypes.Token });
        }
        Assert.Equal(AlertEvents.All.Count, AlertEvents.All.Select(e => e.Id).Distinct().Count());
    }
}
