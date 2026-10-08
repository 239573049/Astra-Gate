using System.Text.Json.Nodes;

namespace Astra.Providers.Quota;

/// <summary>
/// Applies an <c>extract</c> spec to a parsed upstream response. Field names follow cc-switch's
/// extractor result (<c>isValid</c>, <c>invalidMessage</c>, <c>remaining</c>, <c>unit</c>, <c>planName</c>,
/// <c>total</c>, <c>used</c>, <c>extra</c>) so a cc-switch script maps onto it field by field:
/// <code>
/// {
///   "isValid": "success", "invalidMessage": "message", "planLabel": "data.group",
///   "plans": [ { "each": "data.limits", "where": { "unit": 3 }, "first": true, "when": node,
///                "name": node, "unit": node, "remaining": node, "total": node, "used": node,
///                "usedPercent": node, "resetsAt": node, "windowMinutes": node, "extra": node } ]
/// }
/// </code>
/// Without <c>plans</c>, the plan fields at the top level describe a single plan (the flat cc-switch
/// form; <c>planName</c> is accepted for <c>name</c>). A plan with no numeric field is dropped.
/// </summary>
public static class QuotaExtractor
{
    private static readonly string[] PlanValueKeys =
        ["name", "planName", "unit", "remaining", "total", "used", "usedPercent", "resetsAt", "windowMinutes", "extra"];

    private static readonly HashSet<string> PlanKeys = [.. PlanValueKeys, "each", "where", "first", "when"];

    private static readonly HashSet<string> TopKeys = [.. PlanValueKeys, "isValid", "invalidMessage", "planLabel", "plans"];

    /// <summary>What an extract spec produced. <see cref="Plans"/> holds the normalized plan objects.</summary>
    public sealed record Result(bool? IsValid, string? InvalidMessage, string? PlanLabel, List<JsonObject> Plans);

    public static Result Extract(JsonObject spec, JsonNode? response, IReadOnlyDictionary<string, string> vars)
    {
        var root = new QuotaExpression.Scope(response, response, vars);
        var plans = new List<JsonObject>();
        if (spec["plans"] is JsonArray planSpecs)
        {
            foreach (var planSpec in planSpecs.OfType<JsonObject>()) plans.AddRange(ExtractPlans(planSpec, root));
        }
        else if (Plan(spec, root) is { } single)
        {
            plans.Add(single);
        }
        return new Result(
            QuotaExpression.Truth(QuotaExpression.Evaluate(spec["isValid"], root)),
            QuotaExpression.Text(QuotaExpression.Evaluate(spec["invalidMessage"], root)),
            QuotaExpression.Text(QuotaExpression.Evaluate(spec["planLabel"], root)),
            plans);
    }

    /// <summary>Validates an extract spec; returns the problems found (empty when it is usable).</summary>
    public static List<string> Validate(JsonNode? spec)
    {
        var errors = new List<string>();
        if (spec is not JsonObject o)
        {
            errors.Add("extract 必须是对象");
            return errors;
        }
        foreach (var (key, value) in o)
        {
            if (!TopKeys.Contains(key)) errors.Add($"extract: 不支持的字段 {key}");
            else if (key != "plans") QuotaExpression.Validate(value, $"extract.{key}", errors);
        }
        if (o.ContainsKey("plans"))
        {
            if (o["plans"] is not JsonArray plans || plans.Count == 0) errors.Add("extract.plans 必须是非空数组");
            else
            {
                if (PlanValueKeys.Any(o.ContainsKey)) errors.Add("extract: 已有 plans 时不能在顶层再写套餐字段");
                for (var i = 0; i < plans.Count; i++)
                {
                    if (plans[i] is not JsonObject plan)
                    {
                        errors.Add($"extract.plans[{i}] 必须是对象");
                        continue;
                    }
                    foreach (var (key, value) in plan)
                    {
                        var where = $"extract.plans[{i}].{key}";
                        if (!PlanKeys.Contains(key)) errors.Add($"extract.plans[{i}]: 不支持的字段 {key}");
                        else if (key == "each" && (value is not JsonValue v || !v.TryGetValue<string>(out _)))
                            errors.Add($"{where}: 必须是数组路径");
                        else if (key == "where" && value is not JsonObject) errors.Add($"{where}: 必须是 {{字段: 值}} 对象");
                        else if (key == "first" && (value is not JsonValue f || !f.TryGetValue<bool>(out _))) errors.Add($"{where}: 必须是布尔值");
                        else if (key is not ("each" or "where" or "first")) QuotaExpression.Validate(value, where, errors);
                    }
                }
            }
        }
        return errors;
    }

    private static IEnumerable<JsonObject> ExtractPlans(JsonObject spec, QuotaExpression.Scope root)
    {
        if (spec["each"] is not JsonValue each || !each.TryGetValue<string>(out var path))
        {
            if (Plan(spec, root) is { } plan) yield return plan;
            yield break;
        }
        if (QuotaExpression.Resolve(path, root) is not JsonArray items) yield break;
        foreach (var item in items)
        {
            if (!Matches(item, spec["where"] as JsonObject, root)) continue;
            var plan = Plan(spec, root.At(item));
            if (plan is null) continue;
            yield return plan;
            if (spec["first"] is JsonValue first && first.TryGetValue<bool>(out var only) && only) yield break;
        }
    }

    private static bool Matches(JsonNode? item, JsonObject? where, QuotaExpression.Scope root)
    {
        if (where is null) return true;
        var scope = root.At(item);
        foreach (var (field, expected) in where)
        {
            var actual = QuotaExpression.Resolve(field, scope);
            if (actual is null || expected is null) return false;
            var options = expected is JsonArray list ? list.Where(x => x is not null).Select(x => x!) : [expected];
            if (!options.Any(option => QuotaExpression.Same(actual, option))) return false;
        }
        return true;
    }

    /// <summary>One normalized plan, or null when <c>when</c> is false or no numeric field is present.</summary>
    private static JsonObject? Plan(JsonObject spec, QuotaExpression.Scope scope)
    {
        if (spec.ContainsKey("when") && QuotaExpression.Truth(QuotaExpression.Evaluate(spec["when"], scope)) != true) return null;
        double? Num(string key) => QuotaExpression.Number(QuotaExpression.Evaluate(spec[key], scope));
        string? Str(string key) => QuotaExpression.Text(QuotaExpression.Evaluate(spec[key], scope));

        var remaining = Num("remaining");
        var total = Num("total");
        var used = Num("used");
        var usedPercent = Num("usedPercent");
        if (usedPercent is null && used is { } u && total is { } t && t > 0) usedPercent = u / t * 100;
        if (remaining is null && used is null && total is null && usedPercent is null) return null;

        var plan = new JsonObject();
        if ((Str("name") ?? Str("planName")) is { } name) plan["name"] = name;
        if (Str("unit") is { } unit) plan["unit"] = unit;
        if (remaining is { } r) plan["remaining"] = Round(r);
        if (total is { } tt) plan["total"] = Round(tt);
        if (used is { } uu) plan["used"] = Round(uu);
        if (usedPercent is { } p) plan["usedPercent"] = Math.Round(Math.Clamp(p, 0, 100), 1);
        if (QuotaExpression.Time(QuotaExpression.Evaluate(spec["resetsAt"], scope)) is { } resets) plan["resetsAtUtc"] = resets;
        if (Num("windowMinutes") is { } window && window > 0) plan["windowMinutes"] = (int)window;
        if (Str("extra") is { } extra) plan["extra"] = extra;
        return plan;
    }

    private static double Round(double value) => Math.Round(value, 6);
}
