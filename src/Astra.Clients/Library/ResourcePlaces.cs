using Astra.Clients.Config;
using Astra.Core.Clients;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>How a resource file is written for one client.</summary>
/// <param name="Dir">The directory the files go to.</param>
/// <param name="Extension">The file extension, dot included (<c>.md</c>, <c>.toml</c>, <c>.agent.md</c>).</param>
/// <param name="Format"><c>md</c> (Markdown with YAML front matter), <c>toml-role</c> (Codex's agent role), <c>toml-command</c> (Gemini's command).</param>
/// <param name="Args">The command's arguments style: <c>claude</c> (<c>$ARGUMENTS[n]</c>), <c>same</c> (<c>$ARGUMENTS</c> / <c>$n</c>), <c>braces</c> (<c>{{args}}</c>, no words by place).</param>
public sealed record ResourceFilePlace(string Dir, string Extension, string Format, string Args = "same");

/// <summary>A client's hook file and its event names (ported from magpie's <c>hookSpec</c>, MIT).</summary>
/// <param name="File">The JSON file whose <c>hooks.&lt;Event&gt;</c> arrays hold the entries.</param>
/// <param name="Events">Portable event → the client's own name.</param>
/// <param name="Tools">Portable tool kind (<c>shell</c>, <c>edit</c>) → the client's matcher.</param>
/// <param name="Milliseconds">The client's timeout is in milliseconds (Gemini CLI).</param>
public sealed record HookPlace(string File, IReadOnlyDictionary<string, string> Events, IReadOnlyDictionary<string, string> Tools, bool Milliseconds);

/// <summary>A client's environment-variable setting.</summary>
/// <param name="Format">Json (<c>env.NAME</c>) or Toml (<c>shell_environment_policy.set.NAME</c>).</param>
public sealed record EnvPlace(string File, ConfigFileFormat Format, string KeyPrefix);

/// <summary>
/// Where each client reads instructions, sub-agents, slash commands, hooks and environment variables, user-wide and in a project
/// (<c>.plan/resources-library.md</c>; magpie's <c>library</c> package, MIT, is the reference: each table cites it). A table of its own, not an adapter
/// method, for the same reason as <see cref="SkillPlaces"/>: it writes no client config, so there are no golden cases and no TypeScript port.
/// Paths follow each client's own documentation as magpie recorded it; <c>.agents/memory/skill-library-decisions.md</c> lists what is unverified.
/// </summary>
public static class ResourcePlaces
{
    // ---------------------------------------------------------------- instructions

    /// <summary>The clients Astra writes an instruction section for, in display order.</summary>
    public static IReadOnlyList<string> InstructionKinds { get; } =
    [
        ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli, ClientKinds.QwenCode, ClientKinds.OpenCode, ClientKinds.CopilotCli,
        ClientKinds.Droid, ClientKinds.GrokBuild, ClientKinds.Crush, ClientKinds.Pi, ClientKinds.Omp, ClientKinds.DeepSeekHarness,
        ClientKinds.ZCode, ClientKinds.Goose, ClientKinds.CommandCode, ClientKinds.AntigravityCli,
    ];

    private static string Env(ClientEnvironment env, string name) => env.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "";

    private static string ClaudeDir(ClientEnvironment env) => Env(env, "CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : env.Combine(".claude");

    private static string CodexDir(ClientEnvironment env) => Env(env, "CODEX_HOME") is { Length: > 0 } c ? c : env.Combine(".codex");

    private static string XdgConfig(ClientEnvironment env) => Env(env, "XDG_CONFIG_HOME") is { Length: > 0 } x ? x : env.Combine(".config");

    private static string CopilotDir(ClientEnvironment env) => Env(env, "COPILOT_HOME") is { Length: > 0 } c ? c : env.Combine(".copilot");

    private static string GrokDir(ClientEnvironment env) => Env(env, "GROK_HOME") is { Length: > 0 } c ? c : env.Combine(".grok");

    private static string PiDir(ClientEnvironment env) => Env(env, "PI_CODING_AGENT_DIR") is { Length: > 0 } c ? c : env.Combine(".pi", "agent");

    private static string OmpDir(ClientEnvironment env) => Env(env, "PI_CODING_AGENT_DIR") is { Length: > 0 } c ? c : env.Combine(".omp", "agent");

    private static string FirstThere(ClientEnvironment env, string dir, params string[] names) =>
        Path.Combine(dir, names.FirstOrDefault(n => env.FileExists(Path.Combine(dir, n))) ?? names[0]);

    /// <summary>The user-wide instruction file of a client, or null when Astra writes none for it.</summary>
    public static string? InstructionFile(ClientEnvironment env, string kind) => kind switch
    {
        ClientKinds.ClaudeCode => Path.Combine(ClaudeDir(env), "CLAUDE.md"),
        ClientKinds.Codex => Path.Combine(CodexDir(env), "AGENTS.md"),
        ClientKinds.GeminiCli => env.Combine(".gemini", "GEMINI.md"),
        ClientKinds.QwenCode => env.Combine(".qwen", "QWEN.md"),
        ClientKinds.OpenCode => Path.Combine(XdgConfig(env), "opencode", "AGENTS.md"),
        ClientKinds.CopilotCli => Path.Combine(CopilotDir(env), "copilot-instructions.md"),
        ClientKinds.Droid => env.Combine(".factory", "AGENTS.md"),
        ClientKinds.GrokBuild => Path.Combine(GrokDir(env), "AGENTS.md"),
        ClientKinds.Crush => Path.Combine(XdgConfig(env), "crush", "CRUSH.md"),
        // Pi and oh-my-pi read the first there is of these (loadContextFileFromDir)
        ClientKinds.Pi => FirstThere(env, PiDir(env), "AGENTS.md", "AGENTS.MD", "CLAUDE.md", "CLAUDE.MD"),
        ClientKinds.Omp => Path.Combine(OmpDir(env), "AGENTS.md"),
        ClientKinds.DeepSeekHarness => Path.Combine(Env(env, "DSH_HOME") is { Length: > 0 } d ? d : env.Combine(".dsh"), "AGENTS.md"),
        ClientKinds.ZCode => Path.Combine(Env(env, "ZCODE_DESKTOP_HOME_DIR") is { Length: > 0 } z ? z : env.Combine(), ".zcode", "AGENTS.md"),
        ClientKinds.Goose => Path.Combine(env.Os == "windows" && Env(env, "APPDATA") is { Length: > 0 } app
            ? Path.Combine(app, "Block", "goose", "config") : Path.Combine(XdgConfig(env), "goose"), ".goosehints"),
        ClientKinds.CommandCode => env.Combine(".commandcode", "AGENTS.md"),
        // agy reads its user-wide customisations from ~/.gemini/config (magpie's note; unverified against the CLI here)
        ClientKinds.AntigravityCli => env.Combine(".gemini", "config", "GEMINI.md"),
        _ => null,
    };

    /// <summary>The instruction file of a client inside a project directory, or null.</summary>
    public static string? ProjectInstructionFile(string project, string kind) => kind switch
    {
        ClientKinds.ClaudeCode => Path.Combine(project, "CLAUDE.md"),
        ClientKinds.GeminiCli or ClientKinds.AntigravityCli => Path.Combine(project, "GEMINI.md"),
        ClientKinds.QwenCode => Path.Combine(project, "QWEN.md"),
        ClientKinds.Codex or ClientKinds.OpenCode or ClientKinds.Droid or ClientKinds.GrokBuild or ClientKinds.Pi or ClientKinds.Omp
            or ClientKinds.CommandCode or ClientKinds.DeepSeekHarness or ClientKinds.ZCode => Path.Combine(project, "AGENTS.md"),
        ClientKinds.CopilotCli => Path.Combine(project, ".github", "copilot-instructions.md"),
        ClientKinds.Crush => Path.Combine(project, "CRUSH.md"),
        ClientKinds.Goose => Path.Combine(project, ".goosehints"),
        _ => null,
    };

    // ---------------------------------------------------------------- sub-agents

    public static IReadOnlyList<string> AgentKinds { get; } =
    [
        ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli, ClientKinds.QwenCode, ClientKinds.OpenCode,
        ClientKinds.Droid, ClientKinds.GrokBuild, ClientKinds.CopilotCli,
    ];

    /// <summary>Where and how a client's sub-agent files are written, or null (magpie's <c>subagentSpecOf</c>).</summary>
    public static ResourceFilePlace? AgentPlace(ClientEnvironment env, string kind, string? project)
    {
        string Rel(string rel) => project is null ? "" : Path.Combine(project, rel);
        return kind switch
        {
            ClientKinds.ClaudeCode => new(project is null ? Path.Combine(ClaudeDir(env), "agents") : Rel(Path.Combine(".claude", "agents")), ".md", "md"),
            ClientKinds.Codex => new(project is null ? Path.Combine(CodexDir(env), "agents") : Rel(Path.Combine(".codex", "agents")), ".toml", "toml-role"),
            ClientKinds.OpenCode => new(project is null ? Path.Combine(XdgConfig(env), "opencode", "agents") : Rel(Path.Combine(".opencode", "agents")), ".md", "md-subagent"),
            ClientKinds.GeminiCli => new(project is null ? env.Combine(".gemini", "agents") : Rel(Path.Combine(".gemini", "agents")), ".md", "md"),
            ClientKinds.QwenCode => new(project is null ? env.Combine(".qwen", "agents") : Rel(Path.Combine(".qwen", "agents")), ".md", "md"),
            ClientKinds.Droid => new(project is null ? env.Combine(".factory", "droids") : Rel(Path.Combine(".factory", "droids")), ".md", "md"),
            ClientKinds.GrokBuild => project is null ? new(Path.Combine(GrokDir(env), "agents"), ".md", "md") : null,
            ClientKinds.CopilotCli => new(project is null ? Path.Combine(CopilotDir(env), "agents") : Rel(Path.Combine(".github", "agents")), ".agent.md", "md"),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- slash commands

    public static IReadOnlyList<string> CommandKinds { get; } =
        [ClientKinds.ClaudeCode, ClientKinds.OpenCode, ClientKinds.Pi, ClientKinds.GeminiCli, ClientKinds.QwenCode, ClientKinds.Droid];

    /// <summary>Where and how a client's command files are written, or null (magpie's <c>commandSpecOf</c>; Codex has no loader of prompts any more).</summary>
    public static ResourceFilePlace? CommandPlace(ClientEnvironment env, string kind, string? project)
    {
        // Project folders below are the clients' own documented ones (Claude Code, OpenCode, Gemini CLI, Qwen Code, Droid);
        // magpie writes commands user-wide only, so the project paths come from each client's docs, not from magpie.
        string Rel(string rel) => project is null ? "" : Path.Combine(project, rel);
        return kind switch
        {
            ClientKinds.ClaudeCode => new(project is null ? Path.Combine(ClaudeDir(env), "commands") : Rel(Path.Combine(".claude", "commands")), ".md", "md", "claude"),
            ClientKinds.OpenCode => new(project is null ? Path.Combine(XdgConfig(env), "opencode", "commands") : Rel(Path.Combine(".opencode", "commands")), ".md", "md"),
            ClientKinds.Pi => project is null ? new(Path.Combine(PiDir(env), "prompts"), ".md", "md") : null,
            ClientKinds.GeminiCli => new(project is null ? env.Combine(".gemini", "commands") : Rel(Path.Combine(".gemini", "commands")), ".toml", "toml-command", "braces"),
            ClientKinds.QwenCode => new(project is null ? env.Combine(".qwen", "commands") : Rel(Path.Combine(".qwen", "commands")), ".md", "md", "braces"),
            ClientKinds.Droid => new(project is null ? env.Combine(".factory", "commands") : Rel(Path.Combine(".factory", "commands")), ".md", "md", "noplace"),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- hooks

    public static IReadOnlyList<string> HookKinds { get; } = [ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli];

    /// <summary>The portable hook events, in display order.</summary>
    public static IReadOnlyList<string> HookEvents { get; } = ["turn-end", "after-tool", "before-tool", "prompt", "session-start"];

    /// <summary>The kinds of tool a portable tool event can be narrowed to.</summary>
    public static IReadOnlyList<string> HookTools { get; } = ["shell", "edit"];

    private static readonly IReadOnlyDictionary<string, string> ClaudeEvents = new Dictionary<string, string>
    {
        ["turn-end"] = "Stop", ["after-tool"] = "PostToolUse", ["before-tool"] = "PreToolUse", ["prompt"] = "UserPromptSubmit", ["session-start"] = "SessionStart",
    };

    private static readonly IReadOnlyDictionary<string, string> GeminiEvents = new Dictionary<string, string>
    {
        ["turn-end"] = "AfterAgent", ["after-tool"] = "AfterTool", ["before-tool"] = "BeforeTool", ["prompt"] = "BeforeAgent", ["session-start"] = "SessionStart",
    };

    /// <summary>The hook file of a client, or null (magpie's <c>hookSpecFor</c>: Claude Code settings.json, Codex hooks.json, Gemini CLI settings.json).</summary>
    public static HookPlace? Hook(ClientEnvironment env, string kind) => kind switch
    {
        ClientKinds.ClaudeCode => new(Path.Combine(ClaudeDir(env), "settings.json"), ClaudeEvents,
            new Dictionary<string, string> { ["shell"] = "Bash", ["edit"] = "Edit|Write|MultiEdit" }, false),
        ClientKinds.Codex => new(Path.Combine(CodexDir(env), "hooks.json"), ClaudeEvents,
            new Dictionary<string, string> { ["shell"] = "Bash", ["edit"] = "apply_patch" }, false),
        ClientKinds.GeminiCli => new(env.Combine(".gemini", "settings.json"), GeminiEvents,
            new Dictionary<string, string> { ["shell"] = "run_shell_command", ["edit"] = "write_file|replace" }, true),
        _ => null,
    };

    // ---------------------------------------------------------------- environment variables

    public static IReadOnlyList<string> EnvKinds { get; } = [ClientKinds.ClaudeCode, ClientKinds.Codex];

    /// <summary>Where a client keeps the variables it sets for what it runs (Claude Code <c>env</c>, Codex <c>shell_environment_policy.set</c>).</summary>
    public static EnvPlace? EnvSetting(ClientEnvironment env, string kind) => kind switch
    {
        ClientKinds.ClaudeCode => new(Path.Combine(ClaudeDir(env), "settings.json"), ConfigFileFormat.Json, "env."),
        ClientKinds.Codex => new(Path.Combine(CodexDir(env), "config.toml"), ConfigFileFormat.Toml, "shell_environment_policy.set."),
        _ => null,
    };

    /// <summary>The clients that can take an item of <paramref name="kind"/>.</summary>
    public static IReadOnlyList<string> KindsFor(string kind) => kind switch
    {
        ResourceKinds.Instruction => InstructionKinds,
        ResourceKinds.Agent => AgentKinds,
        ResourceKinds.Command => CommandKinds,
        ResourceKinds.Hook => HookKinds,
        ResourceKinds.Env => EnvKinds,
        _ => [],
    };

    /// <summary>Whether items of <paramref name="kind"/> can also go into a project directory.</summary>
    public static bool SupportsProject(string kind) => kind is ResourceKinds.Instruction or ResourceKinds.Agent or ResourceKinds.Command;

    /// <summary>The client's config directory (what "detected" means for it): whether it has been used on this machine.</summary>
    public static bool Detected(ClientEnvironment env, string kind, string kindOfResource)
    {
        string? path = kindOfResource switch
        {
            ResourceKinds.Instruction => InstructionFile(env, kind),
            ResourceKinds.Agent => AgentPlace(env, kind, null)?.Dir,
            ResourceKinds.Command => CommandPlace(env, kind, null)?.Dir,
            ResourceKinds.Hook => Hook(env, kind)?.File,
            ResourceKinds.Env => EnvSetting(env, kind)?.File,
            _ => null,
        };
        return path is not null && env.DirectoryExists(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar))!);
    }
}
