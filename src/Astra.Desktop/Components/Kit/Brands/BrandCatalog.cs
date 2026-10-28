using System.Text.RegularExpressions;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// The tables of web/src/components/icons.tsx, ported: which mark, avatar tile or bundled logo stands for a provider template
/// icon id, a client kind, a model family or a wire protocol. Change them together with the web file.
/// </summary>
internal static class BrandCatalog
{
    /// <summary>A client's identity: a lobehub mark and avatar, or a bundled logo, with an optional corner badge.</summary>
    internal readonly record struct ClientBrand(string? Mark, string? Avatar, string? Logo, LucideIconKind? Badge);

    private static ClientBrand Lobe(string mark, string avatar, LucideIconKind? badge = null) => new(mark, avatar, null, badge);

    private static ClientBrand Logo(string key, LucideIconKind? badge = null) => new(null, null, key, badge);

    /// <summary>The identity of a client kind; null for kinds the table does not know (agent plugins).</summary>
    public static ClientBrand? Client(string kind) => kind switch
    {
        "codex" => Lobe("OpenAI.Mono", "OpenAI"),
        "claude-code" => Lobe("Claude.Color", "Claude", LucideIconKind.SquareTerminal),
        "gemini-cli" => Lobe("Gemini.Color", "Gemini"),
        "opencode" => Lobe("OpenCode.Mono", "OpenCode"),
        "claude-desktop" => Lobe("Claude.Color", "Claude", LucideIconKind.Monitor),
        "grok-build" => Lobe("Grok.Mono", "Grok"),
        "pi" => Lobe("Pi.Mono", "Pi"),
        "hermes-agent" => Lobe("HermesAgent.Mono", "HermesAgent"),
        "minimax-code" => Lobe("Minimax.Color", "Minimax"),
        "copilot-cli" => Lobe("GithubCopilot.Mono", "GithubCopilot", LucideIconKind.SquareTerminal),
        "vscode-copilot" => Lobe("GithubCopilot.Mono", "GithubCopilot", LucideIconKind.Code),
        "crush" => Logo("crush"),
        "qwen-code" => Lobe("Qwen.Color", "Qwen", LucideIconKind.SquareTerminal),
        "droid" => Logo("droid"),
        "kimi-code" => Lobe("Kimi.Color", "Kimi", LucideIconKind.SquareTerminal),
        "zed" => Logo("zed"),
        "vscode-insiders" => Logo("vscode-insiders"),
        "vscodium" => Logo("vscodium"),
        "omp" => Logo("omp"),
        "mimo-code" => Lobe("XiaomiMiMo.Mono", "XiaomiMiMo", LucideIconKind.SquareTerminal),
        "deepseek-harness" => Lobe("DeepSeek.Color", "DeepSeek", LucideIconKind.Monitor),
        "workbuddy" => Logo("workbuddy"),
        "workbuddy-ai" => Logo("workbuddy", LucideIconKind.Globe),
        "muse-code" => Lobe("Meta.Color", "Meta", LucideIconKind.SquareTerminal),
        "nextcowork" => Logo("nextcowork-client"),
        "paseo" => Logo("paseo"),
        "zcode" => Lobe("ZAI.Mono", "ZAI", LucideIconKind.Monitor),
        "tkcode" => Logo("tkcode"),
        _ => null,
    };

    /// <summary>Provider-template <c>icon</c> id -> lobehub brand whose Avatar is the provider's tile.</summary>
    public static string? ProviderAvatar(string id) => id switch
    {
        "openai" => "OpenAI",
        "copilot" => "GithubCopilot",
        "anthropic" => "Anthropic",
        "gemini" => "Gemini",
        "grok" => "XAI",
        "deepseek" => "DeepSeek",
        "kimi" => "Moonshot",
        "glm" => "Zhipu",
        "qwen" => "Qwen",
        "minimax" => "Minimax",
        "volcengine" => "Volcengine",
        "openrouter" => "OpenRouter",
        "siliconflow" => "SiliconCloud",
        "ollama" => "Ollama",
        "lm-studio" => "LmStudio",
        "qoder" => "Qoder",
        _ => null,
    };

    /// <summary>Provider-template icon ids that ship a vendor image instead (key of <c>BrandLogoData</c>).</summary>
    public static string? ProviderLogo(string id) => id switch
    {
        "nextcowork" => "nextcowork-provider",
        "routin" => "routin",
        "workbuddy" => "workbuddy",
        _ => null,
    };

    /// <summary>magpie's icon names that Astra ships under another name.</summary>
    public static string ProviderAlias(string id) => id switch
    {
        "xai" => "grok",
        "zcode" => "glm",
        "kiro-color" => "kiro",
        "workbuddy-color" => "workbuddy",
        _ => id,
    };

    private static readonly (Regex Pattern, string Mark)[] ModelMarks =
    [
        (R("claude|anthropic"), "Claude.Color"),
        (R("gemma"), "Gemma.Color"),
        (R("gemini"), "Gemini.Color"),
        (R(@"\bgpt|\bo[1-9]\b|o[1-9]-|codex|openai|dall-e|whisper|chatgpt"), "OpenAI.Mono"),
        (R("deepseek"), "DeepSeek.Color"),
        (R("qwen|qwq|qvq"), "Qwen.Color"),
        (R("kimi|moonshot"), "Kimi.Color"),
        (R("glm|zhipu|chatglm"), "ChatGLM.Color"),
        (R("minimax|abab"), "Minimax.Color"),
        (R("grok|xai"), "Grok.Mono"),
        (R("llama|meta"), "Meta.Color"),
        (R("mistral|mixtral|codestral|devstral|magistral|pixtral"), "Mistral.Color"),
        (R(@"doubao|\bseed-"), "Doubao.Color"),
        (R("hunyuan"), "Hunyuan.Color"),
        (R("ernie|wenxin"), "Wenxin.Color"),
        (R(@"\byi-"), "Yi.Color"),
        (R("baichuan"), "Baichuan.Color"),
        (R("command|cohere"), "Cohere.Color"),
        (R("step-"), "Stepfun.Mono"),
        (R("mimo"), "XiaomiMiMo.Mono"),
        (R("longcat"), "LongCat.Color"),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The mark key of a model family guessed from its id (first matching pattern wins), or null.</summary>
    public static string? ModelMark(string? model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        foreach (var (pattern, mark) in ModelMarks)
            if (pattern.IsMatch(model)) return mark;
        return null;
    }

    /// <summary>The vendor mark of a wire protocol (<c>openai-chat</c>, <c>openai-responses</c>, <c>anthropic</c>, <c>gemini</c>), or null.</summary>
    public static string? ProtocolMark(string? protocol) => protocol switch
    {
        "openai-chat" or "openai-responses" => "OpenAI.Mono",
        "anthropic" => "Anthropic.Mono",
        "gemini" => "Gemini.Color",
        _ => null,
    };

    /// <summary>Every mark key the tables above can ask for (for tests).</summary>
    public static IEnumerable<string> AllMarkKeys()
    {
        foreach (var (_, mark) in ModelMarks) yield return mark;
        yield return "Anthropic.Mono";
    }
}
