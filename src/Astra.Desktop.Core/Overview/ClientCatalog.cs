using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>
/// The client kinds the web knows by name and in which order it lists them (<c>CLIENT_META</c> / <c>CLIENT_ORDER</c> in
/// <c>web/src/components/icons.tsx</c>), and the overview's client filter rule (<c>web/src/lib/clientFilter.ts</c>).
/// Change the tables together with that file (and <c>BrandCatalog</c>, which holds the marks).
/// </summary>
public static class ClientCatalog
{
    /// <summary>The kinds in display order.</summary>
    public static readonly IReadOnlyList<string> Order =
    [
        "codex", "claude-code", "gemini-cli", "opencode", "claude-desktop", "grok-build",
        "pi", "hermes-agent", "minimax-code", "copilot-cli", "vscode-copilot",
        "crush", "qwen-code", "droid", "kimi-code", "zed",
        "vscode-insiders", "vscodium", "omp", "mimo-code", "deepseek-harness", "workbuddy", "workbuddy-ai", "muse-code", "nextcowork", "paseo", "zcode", "tkcode",
    ];

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

    /// <summary>Whether the catalog names <paramref name="kind"/>.</summary>
    public static bool Known(string kind) => Names.ContainsKey(kind);

    /// <summary>A client's display name; agent plugins ("plugin-&lt;id&gt;") show the reported name, else their plugin id (<c>clientName</c>).</summary>
    public static string Name(string kind, string? fallback = null)
    {
        if (Names.TryGetValue(kind, out var name)) return name;
        if (!string.IsNullOrEmpty(fallback)) return fallback;
        return kind.StartsWith("plugin-", StringComparison.Ordinal) ? kind["plugin-".Length..] : kind;
    }

    /// <summary>A client counts as present when it was detected, its install probe found it, or Astra manages its config (<c>isClientInstalled</c>).</summary>
    public static bool IsInstalled(ClientInfoDto c) => c.Detection.Installed || c.Install?.Installed == true || c.Enabled;

    /// <summary>
    /// The clients the overview filter offers: installed ones in <see cref="Order"/>, plus the selected one even when it is no
    /// longer installed, so the active filter never disappears. Empty while the list is loading (<c>filterableClients</c>).
    /// </summary>
    public static List<string> Filterable(IEnumerable<ClientInfoDto>? clients, string? selected)
    {
        var present = (clients ?? []).Where(IsInstalled).Select(c => c.Kind).ToHashSet(StringComparer.Ordinal);
        return Order.Where(k => present.Contains(k) || k == selected).ToList();
    }
}
