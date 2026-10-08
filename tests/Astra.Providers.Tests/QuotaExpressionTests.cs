using System.Text.Json.Nodes;
using Astra.Providers.Quota;

namespace Astra.Providers.Tests;

public class QuotaExpressionTests
{
    private static readonly Dictionary<string, string> Vars = new() { ["host"] = "api.siliconflow.cn" };

    private static JsonNode? Eval(string spec, string response) =>
        QuotaExpression.Evaluate(JsonNode.Parse(spec), new QuotaExpression.Scope(JsonNode.Parse(response), JsonNode.Parse(response), Vars));

    private static double? Num(string spec, string response) => QuotaExpression.Number(Eval(spec, response));

    [Fact]
    public void Paths_Read_Nested_Fields_Indexes_And_Numeric_Strings()
    {
        const string body = """{"data":{"balance":"110.50","items":[{"v":1},{"v":2}]}}""";
        Assert.Equal(110.5, Num("\"data.balance\"", body));
        Assert.Equal(2, Num("\"data.items[1].v\"", body));
        Assert.Null(Eval("\"data.items[5].v\"", body));
        Assert.Null(Eval("\"data.missing\"", body));
        Assert.Null(Eval("\"data..bad\"", body));
    }

    [Fact]
    public void Candidates_Take_The_First_Present_Value_Including_Zero_And_False()
    {
        Assert.Equal(0, Num("""["a", "b"]""", """{"a":0,"b":5}"""));
        Assert.Equal(5, Num("""["missing", "b"]""", """{"b":5}"""));
        Assert.False(QuotaExpression.Truth(Eval("""["ok", "fallback"]""", """{"ok":false,"fallback":true}""")));
    }

    [Fact]
    public void Arithmetic_Never_Invents_A_Number_From_A_Missing_Operand()
    {
        const string body = """{"quota":1000000,"used_quota":500000}""";
        Assert.Equal(2, Num("""{"$div":["quota",500000]}""", body));
        Assert.Equal(3, Num("""{"$div":[{"$add":["quota","used_quota"]},500000]}""", body));
        Assert.Equal(500000, Num("""{"$sub":["quota","used_quota"]}""", body));
        Assert.Equal(100, Num("""{"$scale":"quota","by":0.0001}""", body));
        Assert.Equal(50, Num("""{"$pctUsed":{"used":"used_quota","total":"quota"}}""", body));

        Assert.Null(Eval("""{"$add":["quota","nope"]}""", body));
        Assert.Null(Eval("""{"$sub":["quota","nope"]}""", body));
        Assert.Null(Eval("""{"$div":["quota",0]}""", body));
        Assert.Null(Eval("""{"$pctUsed":{"used":"used_quota","total":"nope"}}""", body));
        Assert.Null(Eval("""{"$scale":"nope","by":2}""", body));
    }

    [Fact]
    public void Comparison_Case_Var_And_Const()
    {
        Assert.True(QuotaExpression.Truth(Eval("""{"$eq":["base_resp.status_code",0]}""", """{"base_resp":{"status_code":0}}""")));
        Assert.False(QuotaExpression.Truth(Eval("""{"$eq":["code",{"$const":"0"}]}""", """{"code":1004}""")));
        // A bare string is always a path: "0" looks up a field named 0 (absent), it is not the text "0".
        Assert.Null(Eval("""{"$eq":["code","0"]}""", """{"code":1004}"""));
        Assert.Null(Eval("""{"$eq":["base_resp.status_code",0]}""", "{}"));
        Assert.False(QuotaExpression.Truth(Eval("""{"$not":"ok"}""", """{"ok":true}""")));
        Assert.Equal("CNY", QuotaExpression.Text(Eval(
            """{"$case":{"on":{"$var":"host"},"cases":{"api.siliconflow.cn":"CNY"},"default":"USD"}}""", "{}")));
        Assert.Equal("USD", QuotaExpression.Text(Eval("""{"$const":"USD"}""", "{}")));
    }

    [Fact]
    public void Time_Accepts_Seconds_Millis_And_Strings()
    {
        Assert.Equal("2026-10-08T00:00:00.0000000+00:00", QuotaExpression.Text(Eval("""{"$time":"t"}""", """{"t":1791417600}""")));
        Assert.Equal("2026-10-08T00:00:00.0000000+00:00", QuotaExpression.Text(Eval("""{"$time":"t"}""", """{"t":1791417600000}""")));
        Assert.Equal("2026-10-08T00:00:00.0000000+00:00", QuotaExpression.Text(Eval("""{"$time":"t"}""", """{"t":"2026-10-08T08:00:00+08:00"}""")));
        Assert.Null(Eval("""{"$time":"t"}""", """{"t":-1}"""));
        Assert.Null(Eval("""{"$time":"t"}""", """{"t":"soon"}"""));
    }

    [Fact]
    public void Validation_Reports_Unknown_Operators_Bad_Arity_And_Bad_Paths()
    {
        var errors = new List<string>();
        QuotaExpression.Validate(JsonNode.Parse("""{"$eval":"x"}"""), "x", errors);
        QuotaExpression.Validate(JsonNode.Parse("""{"$sub":["a"]}"""), "y", errors);
        QuotaExpression.Validate(JsonNode.Parse("\"a..b\""), "z", errors);
        QuotaExpression.Validate(JsonNode.Parse("""{"$scale":"a"}"""), "w", errors);
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("$eval"));

        var ok = new List<string>();
        QuotaExpression.Validate(JsonNode.Parse("""{"$div":[{"$add":["a","b"]},500000]}"""), "ok", ok);
        Assert.Empty(ok);
    }

    [Fact]
    public void Extractor_Handles_Flat_Form_Each_Where_First_And_When()
    {
        var flat = QuotaExtractor.Extract(JsonNode.Parse("""
            {"isValid":"success","planLabel":"data.group","unit":{"$const":"USD"},
             "remaining":{"$div":["data.quota",500000]},"used":{"$div":["data.used_quota",500000]},
             "total":{"$div":[{"$add":["data.quota","data.used_quota"]},500000]}}
            """)!.AsObject(), JsonNode.Parse("""{"success":true,"data":{"group":"vip","quota":1500000,"used_quota":500000}}"""), Vars);
        Assert.True(flat.IsValid);
        Assert.Equal("vip", flat.PlanLabel);
        var plan = Assert.Single(flat.Plans);
        Assert.Equal(3, plan["remaining"]!.GetValue<double>());
        Assert.Equal(4, plan["total"]!.GetValue<double>());
        Assert.Equal(25, plan["usedPercent"]!.GetValue<double>()); // derived from used / total

        var spec = JsonNode.Parse("""
            {"plans":[
              {"each":"limits","where":{"type":["TOKENS_LIMIT","CREDIT_LIMIT"],"unit":3},"first":true,"windowMinutes":300,"usedPercent":"percentage"},
              {"each":"limits","where":{"unit":6},"first":true,"when":{"$eq":["active",1]},"windowMinutes":10080,"usedPercent":"percentage"}
            ]}
            """)!.AsObject();
        var windows = QuotaExtractor.Extract(spec, JsonNode.Parse("""
            {"limits":[{"type":"TIME_LIMIT","unit":3,"percentage":99},{"type":"TOKENS_LIMIT","unit":3,"percentage":12},
                       {"type":"TOKENS_LIMIT","unit":3,"percentage":80},{"type":"TOKENS_LIMIT","unit":6,"percentage":40,"active":0}]}
            """), Vars);
        var only = Assert.Single(windows.Plans); // weekly skipped by "when"
        Assert.Equal(12, only["usedPercent"]!.GetValue<double>());
        Assert.Equal(300, only["windowMinutes"]!.GetValue<int>());
        Assert.Null(windows.IsValid);
    }

    [Fact]
    public void Extractor_Drops_Plans_Without_Any_Number()
    {
        var result = QuotaExtractor.Extract(JsonNode.Parse("""{"unit":{"$const":"USD"},"remaining":"balance"}""")!.AsObject(),
            JsonNode.Parse("""{"other":1}"""), Vars);
        Assert.Empty(result.Plans);
    }

    [Fact]
    public void Extractor_Validation_Rejects_Unknown_Fields_And_Mixed_Forms()
    {
        Assert.Contains(QuotaExtractor.Validate(JsonNode.Parse("""{"balance":"x"}""")), e => e.Contains("balance"));
        Assert.NotEmpty(QuotaExtractor.Validate(JsonNode.Parse("""{"remaining":"a","plans":[{"remaining":"b"}]}""")));
        Assert.NotEmpty(QuotaExtractor.Validate(JsonNode.Parse("""{"plans":[{"each":1}]}""")));
        Assert.Empty(QuotaExtractor.Validate(JsonNode.Parse("""{"isValid":"ok","plans":[{"each":"a","remaining":"b"}]}""")));
    }

    [Fact]
    public void Every_Built_In_Template_Loads_And_Validates()
    {
        var catalog = new QuotaTemplateCatalog();
        Assert.Equal(
            ["deepseek", "openrouter", "siliconflow", "stepfun", "novita", "newapi", "kimi-coding", "glm-coding", "minimax-coding"],
            catalog.All.Select(t => t.Id).ToList());
        Assert.All(catalog.All, t => Assert.Empty(QuotaExtractor.Validate(t.Extract)));
    }
}
