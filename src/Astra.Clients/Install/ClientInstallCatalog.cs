using Astra.Core.Clients;

namespace Astra.Clients.Install;

/// <summary>How Astra can install and update a client.</summary>
public enum ClientInstallMethod
{
    /// <summary>A global npm package (<c>npm install -g &lt;package&gt;@latest</c>); latest version from the npm registry.</summary>
    Npm,

    /// <summary>A VS Code extension (<c>code --install-extension &lt;id&gt;</c>); VS Code keeps it updated itself.</summary>
    VsCodeExtension,

    /// <summary>The vendor's own install script (<see cref="ClientInstallSpec.PosixScriptUrl"/> /
    /// <see cref="ClientInstallSpec.WindowsScriptUrl"/>); run non-interactively once, then the client's own
    /// updater (<see cref="ClientInstallSpec.SelfUpdateArgs"/>) keeps it current.</summary>
    Script,

    /// <summary>A desktop app or a client with its own installer: Astra only links to the download page.</summary>
    Manual,
}

/// <summary>
/// Install facts for one client. <see cref="Executable"/> is the command the user runs (probed with
/// <c>--version</c>); <see cref="Package"/> is the npm package or VS Code extension id;
/// <see cref="SelfUpdateArgs"/> is the client's own updater, used when the installed copy did not come from
/// npm (e.g. Claude Code's native installer). <see cref="LatestUrl"/> is a JSON document naming the newest
/// version (<c>version</c> or <c>tag_name</c>) for clients the npm registry does not cover.
/// </summary>
public sealed record ClientInstallSpec(
    string Kind,
    ClientInstallMethod Method,
    string? Executable,
    string? Package,
    string HomepageUrl,
    IReadOnlyList<string>? SelfUpdateArgs = null,
    string? PosixScriptUrl = null,
    string? WindowsScriptUrl = null,
    string? LatestUrl = null);

/// <summary>
/// Install facts per client kind. npm package names were checked against the npm registry (the <c>bin</c>
/// entry of each package matches <see cref="ClientInstallSpec.Executable"/>); Grok Build is xAI's official
/// <c>@xai-official/grok</c>. Hermes Agent ships a Python installer, Claude Desktop, Zed, VS Code Insiders, VSCodium and WorkBuddy
/// native apps, and omp runs on Bun (<c>engines.bun</c>), so these are
/// <see cref="ClientInstallMethod.Manual"/>: Astra shows the installed version and the homepage, and never runs a command.
/// Droid's <c>droid update</c> only applies to its own installer (an npm install has it disabled, so npm updates win there).
/// </summary>
public static class ClientInstallCatalog
{
    private static readonly Dictionary<string, ClientInstallSpec> Specs = new ClientInstallSpec[]
    {
        new(ClientKinds.Codex, ClientInstallMethod.Npm, "codex", "@openai/codex", "https://developers.openai.com/codex/cli"),
        new(ClientKinds.ClaudeCode, ClientInstallMethod.Npm, "claude", "@anthropic-ai/claude-code",
            "https://docs.claude.com/en/docs/claude-code/setup", ["update"]),
        new(ClientKinds.GeminiCli, ClientInstallMethod.Npm, "gemini", "@google/gemini-cli", "https://github.com/google-gemini/gemini-cli"),
        new(ClientKinds.OpenCode, ClientInstallMethod.Npm, "opencode", "opencode-ai", "https://opencode.ai/docs", ["upgrade"]),
        new(ClientKinds.ClaudeDesktop, ClientInstallMethod.Manual, null, null, "https://claude.ai/download"),
        new(ClientKinds.GrokBuild, ClientInstallMethod.Npm, "grok", "@xai-official/grok", "https://www.npmjs.com/package/@xai-official/grok"),
        new(ClientKinds.Pi, ClientInstallMethod.Npm, "pi", "@mariozechner/pi-coding-agent", "https://www.npmjs.com/package/@mariozechner/pi-coding-agent"),
        new(ClientKinds.HermesAgent, ClientInstallMethod.Script, "hermes", null, "https://github.com/NousResearch/hermes-agent",
            SelfUpdateArgs: ["update"],
            PosixScriptUrl: "https://hermes-agent.nousresearch.com/install.sh",
            WindowsScriptUrl: "https://hermes-agent.nousresearch.com/install.ps1",
            LatestUrl: "https://api.github.com/repos/NousResearch/hermes-agent/releases/latest"),
        new(ClientKinds.MiniMaxCode, ClientInstallMethod.Npm, "mcode", "@minimax-ai/code", "https://www.npmjs.com/package/@minimax-ai/code"),
        new(ClientKinds.CopilotCli, ClientInstallMethod.Npm, "copilot", "@github/copilot",
            "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli"),
        new(ClientKinds.VsCodeCopilot, ClientInstallMethod.VsCodeExtension, "code", "GitHub.copilot-chat",
            "https://code.visualstudio.com/docs/copilot/setup"),
        new(ClientKinds.Crush, ClientInstallMethod.Npm, "crush", "@charmland/crush", "https://github.com/charmbracelet/crush"),
        new(ClientKinds.QwenCode, ClientInstallMethod.Npm, "qwen", "@qwen-code/qwen-code", "https://github.com/QwenLM/qwen-code"),
        new(ClientKinds.Droid, ClientInstallMethod.Npm, "droid", "droid", "https://docs.factory.ai/cli/getting-started/quickstart", ["update"]),
        new(ClientKinds.KimiCode, ClientInstallMethod.Npm, "kimi", "@moonshot-ai/kimi-code", "https://moonshotai.github.io/kimi-code/"),
        new(ClientKinds.Zed, ClientInstallMethod.Manual, "zed", null, "https://zed.dev/download"),
        new(ClientKinds.VsCodeInsiders, ClientInstallMethod.Manual, "code-insiders", null, "https://code.visualstudio.com/insiders"),
        new(ClientKinds.VsCodium, ClientInstallMethod.Manual, "codium", null, "https://vscodium.com/"),
        new(ClientKinds.Omp, ClientInstallMethod.Manual, "omp", null, "https://github.com/can1357/oh-my-pi"),
        new(ClientKinds.DeepSeekHarness, ClientInstallMethod.Manual, "dsh", null, "https://www.deepseek.com/en/harness/"),
        new(ClientKinds.MiMoCode, ClientInstallMethod.Npm, "mimo", "@mimo-ai/cli", "https://github.com/XiaomiMiMo/MiMo-Code"),
        new(ClientKinds.WorkBuddy, ClientInstallMethod.Manual, null, null, "https://www.workbuddy.cn/"),
        new(ClientKinds.MuseCode, ClientInstallMethod.Manual, "muse", null, "https://dev.meta.ai/docs/muse-code"),
        new(ClientKinds.NextCoWork, ClientInstallMethod.Manual, null, null, "https://nextco.work/"),
    }.ToDictionary(s => s.Kind, StringComparer.Ordinal);

    /// <summary>The spec for a client kind, or null when Astra cannot install that client (unknown or not catalogued yet).</summary>
    public static ClientInstallSpec? Get(string kind) => Specs.GetValueOrDefault(kind);

    /// <summary>Every spec, in <see cref="ClientKinds.All"/> order; kinds without a spec are skipped.</summary>
    public static IEnumerable<ClientInstallSpec> All => ClientKinds.All.Where(Specs.ContainsKey).Select(k => Specs[k]);
}
