using Astra.Core.Clients;

namespace Astra.Clients.Install;

/// <summary>How Astra can install, update and uninstall a client.</summary>
public enum ClientInstallMethod
{
    /// <summary>A global npm package (<c>npm install -g &lt;package&gt;@latest</c>); latest version from the npm registry; <c>npm uninstall -g &lt;package&gt;</c> removes it.</summary>
    Npm,

    /// <summary>A VS Code extension (<c>code --install-extension &lt;id&gt;</c>); VS Code keeps it updated itself; <c>code --uninstall-extension &lt;id&gt;</c> removes it.</summary>
    VsCodeExtension,

    /// <summary>The vendor's own install script (<see cref="ClientInstallSpec.PosixScriptUrl"/> /
    /// <see cref="ClientInstallSpec.WindowsScriptUrl"/>); run non-interactively once, then the client's own
    /// updater (<see cref="ClientInstallSpec.SelfUpdateArgs"/>) keeps it current. Never uninstalled by Astra: the script decides
    /// where its files go, so the homepage's instructions are the only reliable way to remove them.</summary>
    Script,

    /// <summary>A desktop app or a client with its own installer: Astra only links to the download page (and never uninstalls it).</summary>
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
        new(ClientKinds.Pi, ClientInstallMethod.Npm, "pi", "@earendil-works/pi-coding-agent", "https://www.npmjs.com/package/@earendil-works/pi-coding-agent"),
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
        new(ClientKinds.WorkBuddyAi, ClientInstallMethod.Manual, null, null, "https://www.workbuddy.ai/"),
        new(ClientKinds.MuseCode, ClientInstallMethod.Manual, "muse", null, "https://dev.meta.ai/docs/muse-code"),
        new(ClientKinds.NextCoWork, ClientInstallMethod.Manual, null, null, "https://nextco.work/"),
        new(ClientKinds.Paseo, ClientInstallMethod.Manual, "paseo", null, "https://paseo.sh/"),
        new(ClientKinds.ZCode, ClientInstallMethod.Manual, null, null, "https://zcode.z.ai/"),
        new(ClientKinds.TKCode, ClientInstallMethod.Manual, null, null, "https://github.com/AtomUICommunity/TKCode"),
        new(ClientKinds.OpenChamber, ClientInstallMethod.Manual, "openchamber", null, "https://github.com/btriapitsyn/openchamber"),
        new(ClientKinds.DevEcoCode, ClientInstallMethod.Npm, "deveco", "@deveco/deveco-code", "https://github.com/openharmony-sig/deveco-code"),
        new(ClientKinds.StepCode, ClientInstallMethod.Manual, null, null, "https://www.stepfun.com/"),
        new(ClientKinds.Aside, ClientInstallMethod.Manual, "aside", null, "https://aside.sh/"),
        new(ClientKinds.OmO, ClientInstallMethod.Npm, "omo", "omo-ai", "https://github.com/code-yeongyu/oh-my-openagent", ["update"]),
        new(ClientKinds.PrimeAgent, ClientInstallMethod.Manual, "prime-agent", null, "https://github.com/PrimeIntellect-ai/prime-agent"),
        new(ClientKinds.Mu, ClientInstallMethod.Manual, null, null, "https://github.com/qybaihe/mu"),
        new(ClientKinds.MothX, ClientInstallMethod.Npm, "mothx", "mothx-installer", "https://github.com/oschina/mothx"),
        new(ClientKinds.PenguinHarness, ClientInstallMethod.Npm, "penguin", "@prismshadow/penguin-cli", "https://github.com/Prism-Shadow/penguin-harness"),
        new(ClientKinds.AntigravityCli, ClientInstallMethod.Manual, "agy", null, "https://antigravity.google/"),
        new(ClientKinds.Dim, ClientInstallMethod.Npm, "dima", "dimcode-alpha", "https://github.com/arcships/dim"),
        new(ClientKinds.CommandCode, ClientInstallMethod.Manual, "command-code", null, "https://commandcode.ai/"),
        new(ClientKinds.Fx, ClientInstallMethod.Manual, null, null, "https://fx.sh/"),
        new(ClientKinds.Goose, ClientInstallMethod.Manual, "goose", null, "https://block.github.io/goose/"),
        new(ClientKinds.ZooCode, ClientInstallMethod.Manual, null, null, "https://marketplace.visualstudio.com/items?itemName=ZooCodeOrganization.zoo-code"),
        new(ClientKinds.JetBrainsAir, ClientInstallMethod.Manual, null, null, "https://www.jetbrains.com/air/"),
        new(ClientKinds.AirIde, ClientInstallMethod.Manual, null, null, "https://www.jetbrains.com/air/"),
        new(ClientKinds.CopilotJetBrains, ClientInstallMethod.Manual, null, null, "https://plugins.jetbrains.com/plugin/17718-github-copilot"),
        new(ClientKinds.CodeBuddy, ClientInstallMethod.Npm, "codebuddy", "@tencent-ai/codebuddy-code", "https://www.codebuddy.ai/"),
        new(ClientKinds.Qoder, ClientInstallMethod.Npm, "qodercli", "@qoder-ai/qodercli", "https://qoder.com/cli"),
        new(ClientKinds.QoderCn, ClientInstallMethod.Npm, "qoderclicn", "@qodercn-ai/qoderclicn", "https://qoder.com/cli"),
        new(ClientKinds.T3Code, ClientInstallMethod.Manual, null, null, "https://github.com/pingdotgg/t3code"),
        new(ClientKinds.Pencil, ClientInstallMethod.Manual, null, null, "https://pen.dev/"),
        new(ClientKinds.OpenPencil, ClientInstallMethod.Manual, null, null, "https://github.com/ZSeven-W/openpencil"),
        new(ClientKinds.Raycast, ClientInstallMethod.Manual, null, null, "https://www.raycast.com/"),
        new(ClientKinds.Obsidian, ClientInstallMethod.Manual, null, null, "https://obsidian.md/"),
        new(ClientKinds.Reasonix, ClientInstallMethod.Manual, "reasonix", null, "https://github.com/esengine/DeepSeek-Reasonix"),
        new(ClientKinds.Morph, ClientInstallMethod.Manual, null, null, "https://github.com/quailyquaily/mistermorph"),
        new(ClientKinds.Jcode, ClientInstallMethod.Manual, "jcode", null, "https://github.com/1jehuang/jcode"),
        new(ClientKinds.Vibe, ClientInstallMethod.Manual, "vibe", null, "https://github.com/mistralai/mistral-vibe"),
        new(ClientKinds.Empryo, ClientInstallMethod.Manual, "empryo", null, "https://github.com/ProxySoul/soulforge"),
        new(ClientKinds.Nanobot, ClientInstallMethod.Manual, "nanobot", null, "https://github.com/HKUDS/nanobot"),
        new(ClientKinds.MiniQ, ClientInstallMethod.Manual, "miniq", null, "https://github.com/LeadingThink/miniQ"),
        new(ClientKinds.Paimon, ClientInstallMethod.Manual, "paimon", null, "https://github.com/aisk/paimon"),
        new(ClientKinds.Cline, ClientInstallMethod.Npm, "cline", "cline", "https://cline.bot/"),
        new(ClientKinds.GrokApp, ClientInstallMethod.Manual, null, null, "https://github.com/RongleCat/grok-app"),
        new(ClientKinds.AtomCode, ClientInstallMethod.Npm, "atomcode", "@atomgit.com/atomcode", "https://atomgit.com/atomgit_atomcode/atomcode"),
        new(ClientKinds.Snow, ClientInstallMethod.Npm, "snow", "snow-ai", "https://github.com/MayDay-wpf/snow-cli"),
        new(ClientKinds.Ante, ClientInstallMethod.Manual, "ante", null, "https://antigma.ai/"),
        new(ClientKinds.CraftAgents, ClientInstallMethod.Manual, null, null, "https://github.com/lukilabs/craft-agents-oss"),
        new(ClientKinds.TuanjieCowork, ClientInstallMethod.Manual, null, null, "https://codely.tuanjie.cn/"),
        new(ClientKinds.LiveAgent, ClientInstallMethod.Manual, null, null, "https://github.com/Stack-Cairn/LiveAgent"),
        new(ClientKinds.OpenMausBot, ClientInstallMethod.Manual, null, null, "https://github.com/milind-soni/OpenMausBot"),
        new(ClientKinds.AstrBot, ClientInstallMethod.Manual, "astrbot", null, "https://github.com/AstrBotDevs/AstrBot"),
        new(ClientKinds.QwenPaw, ClientInstallMethod.Manual, "qwenpaw", null, "https://github.com/agentscope-ai/QwenPaw"),
        new(ClientKinds.OpenClaw, ClientInstallMethod.Npm, "openclaw", "openclaw", "https://github.com/openclaw/openclaw"),
        new(ClientKinds.OpenBot, ClientInstallMethod.Manual, null, null, "https://openbot.run/"),
    }.ToDictionary(s => s.Kind, StringComparer.Ordinal);

    /// <summary>The spec for a client kind, or null when Astra cannot install that client (unknown or not catalogued yet).</summary>
    public static ClientInstallSpec? Get(string kind) => Specs.GetValueOrDefault(kind);

    /// <summary>Every spec, in <see cref="ClientKinds.All"/> order; kinds without a spec are skipped.</summary>
    public static IEnumerable<ClientInstallSpec> All => ClientKinds.All.Where(Specs.ContainsKey).Select(k => Specs[k]);
}
