using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.SmartRouting;

/// <summary>
/// The sentences of the Smart routing page that depend on data: a refused save in the UI's words, why a decision fell back, why a
/// caller cannot use a grade (web SmartRoutingSettings.tsx <c>problemText</c>, <c>explain</c>, <c>unusableKey</c>, <c>reasonText</c>).
/// </summary>
public static partial class RoutingTexts
{
    [GeneratedRegex(@"^grades\[(\d+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex GradeField();

    /// <summary>
    /// One refusal in the UI's language (<c>smartRouting.problem.&lt;code&gt;</c>); the server's English sentence when the code is one
    /// this build does not know.
    /// </summary>
    public static string ProblemText(RoutingProblemDto problem, SmartRoutingDraft draft)
    {
        var key = "smartRouting.problem." + problem.Code;
        if (!Loc.Has(key)) return problem.Message;
        var at = GradeField().Match(problem.Field ?? "");
        var grade = at.Success && int.TryParse(at.Groups[1].Value, out var i) && i < draft.Grades.Count ? draft.Grades[i] : null;
        return Loc.T(key, ("grade", grade?.DisplayName ?? ""), ("field", problem.Field ?? ""));
    }

    /// <summary>What went wrong, line by line: every problem of a refused save, or the error's own words (web <c>explain</c>).</summary>
    public static List<string> Explain(Exception error, SmartRoutingDraft draft)
    {
        if (error is ApiException { Details: { } details })
        {
            RoutingProblemsDetails? parsed = null;
            try { parsed = details.Deserialize(JsonContexts.Info<RoutingProblemsDetails>(Json.Api)); }
            catch (JsonException) { /* not a problems list: fall through to the message */ }
            if (parsed is { Problems.Count: > 0 }) return parsed.Problems.Select(p => ProblemText(p, draft)).ToList();
        }
        return [error.Message];
    }

    /// <summary>The message key of why a caller cannot use a grade (a <c>GradeProblem</c> code); null for a reason this build does not know.</summary>
    public static string? UnusableKey(string? reason)
    {
        var key = "smartRouting.unusable." + reason;
        return !string.IsNullOrEmpty(reason) && Loc.Has(key) ? key : null;
    }

    /// <summary>Why a decision fell back (a failure kind, low_confidence, breaker …) in the UI's language; as it is when this build does not know it.</summary>
    public static string ReasonText(string? reason)
    {
        var key = "smartRouting.failure." + reason;
        return Loc.Has(key) ? Loc.T(key) : reason ?? "";
    }

    private static readonly Dictionary<string, string> ClientLabels = new(StringComparer.Ordinal)
    {
        ["codex"] = "Codex", ["claude-code"] = "Claude Code", ["gemini-cli"] = "Gemini CLI", ["opencode"] = "OpenCode",
        ["claude-desktop"] = "Claude Desktop", ["grok-build"] = "Grok Build", ["pi"] = "Pi", ["hermes-agent"] = "Hermes Agent",
        ["minimax-code"] = "MiniMax Code", ["copilot-cli"] = "Copilot CLI", ["vscode-copilot"] = "VS Code Copilot", ["crush"] = "Crush",
        ["qwen-code"] = "Qwen Code", ["droid"] = "Droid", ["kimi-code"] = "Kimi Code", ["zed"] = "Zed", ["vscode-insiders"] = "VS Code Insiders",
        ["vscodium"] = "VSCodium", ["omp"] = "omp (oh-my-pi)", ["mimo-code"] = "MiMo Code", ["deepseek-harness"] = "DeepSeek Harness",
        ["workbuddy"] = "WorkBuddy", ["workbuddy-ai"] = "WorkBuddy AI", ["muse-code"] = "Muse Code", ["nextcowork"] = "NextCoWork",
        ["paseo"] = "Paseo", ["zcode"] = "ZCode", ["tkcode"] = "TKCode",
    };

    /// <summary>A client's display name (web <c>clientName</c>); an agent plugin's client shows its plugin id.</summary>
    public static string ClientName(string kind)
        => ClientLabels.TryGetValue(kind, out var name) ? name : kind.StartsWith("plugin-", StringComparison.Ordinal) ? kind["plugin-".Length..] : kind;
}
