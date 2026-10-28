namespace Astra.Desktop.Core.Shell;

/// <summary>
/// The display names and list order of the client kinds, ported from web/src/components/icons.tsx (<c>CLIENT_META</c>, <c>CLIENT_ORDER</c>,
/// <c>clientName</c>). One table for every page and the shell (the connection banner names outdated clients with it); change it together
/// with the web file. Brand names are not translated, so there is no catalog key behind them.
/// </summary>
public static class ClientMeta
{
    /// <summary>Kind → display name (web <c>CLIENT_META</c>), in the web's declaration order.</summary>
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["codex"] = "Codex",
        ["claude-code"] = "Claude Code",
        ["gemini-cli"] = "Gemini CLI",
        ["opencode"] = "OpenCode",
        ["claude-desktop"] = "Claude Desktop",
        ["grok-build"] = "Grok Build",
        ["pi"] = "Pi",
        ["hermes-agent"] = "Hermes Agent",
        ["minimax-code"] = "MiniMax Code",
        ["copilot-cli"] = "Copilot CLI",
        ["vscode-copilot"] = "VS Code Copilot",
        ["crush"] = "Crush",
        ["qwen-code"] = "Qwen Code",
        ["droid"] = "Droid",
        ["kimi-code"] = "Kimi Code",
        ["zed"] = "Zed",
        ["vscode-insiders"] = "VS Code Insiders",
        ["vscodium"] = "VSCodium",
        ["omp"] = "omp (oh-my-pi)",
        ["mimo-code"] = "MiMo Code",
        ["deepseek-harness"] = "DeepSeek Harness",
        ["workbuddy"] = "WorkBuddy",
        ["workbuddy-ai"] = "WorkBuddy AI",
        ["muse-code"] = "Muse Code",
        ["nextcowork"] = "NextCoWork",
        ["paseo"] = "Paseo",
        ["zcode"] = "ZCode",
        ["tkcode"] = "TKCode",
    };

    /// <summary>The order clients are listed in (web <c>CLIENT_ORDER</c>).</summary>
    public static readonly IReadOnlyList<string> Order =
    [
        "codex", "claude-code", "gemini-cli", "opencode", "claude-desktop", "grok-build",
        "pi", "hermes-agent", "minimax-code", "copilot-cli", "vscode-copilot",
        "crush", "qwen-code", "droid", "kimi-code", "zed",
        "vscode-insiders", "vscodium", "omp", "mimo-code", "deepseek-harness", "workbuddy", "workbuddy-ai", "muse-code", "nextcowork", "paseo", "zcode", "tkcode",
    ];

    /// <summary>
    /// A client's display name (web <c>clientName</c>): the table's name; else, for an agent plugin's <c>plugin-&lt;id&gt;</c> or any kind the
    /// table does not know, <paramref name="fallback"/> (the name the server reports) when not empty, else the plugin id, else the kind.
    /// </summary>
    public static string Name(string kind, string? fallback = null)
    {
        if (Names.TryGetValue(kind, out var name)) return name;
        if (!string.IsNullOrEmpty(fallback)) return fallback;
        return kind.StartsWith("plugin-", StringComparison.Ordinal) ? kind["plugin-".Length..] : kind;
    }

    /// <summary>Position of <paramref name="kind"/> in <see cref="Order"/>; unknown kinds (agent plugins) sort after every known one.</summary>
    public static int OrderOf(string kind)
    {
        for (var i = 0; i < Order.Count; i++)
            if (Order[i] == kind) return i;
        return Order.Count;
    }
}
