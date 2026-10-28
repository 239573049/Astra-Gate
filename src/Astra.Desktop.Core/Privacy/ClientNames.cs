namespace Astra.Desktop.Core.Privacy;

/// <summary>The display names of the client kinds (web/src/components/icons.tsx <c>CLIENT_META</c>); an unknown kind shows as itself.</summary>
public static class ClientNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
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

    /// <summary>The name of a client kind; "—" for none.</summary>
    public static string Of(string? kind) => string.IsNullOrEmpty(kind) ? "—" : Names.TryGetValue(kind, out var name) ? name : kind;
}
