using Astra.Clients.Config;
using Astra.Core.Clients;

namespace Astra.Clients.Library;

/// <summary>Where one client reads Skills from.</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="SkillsDir">The client's own Skills directory.</param>
/// <param name="ReadsShared">Whether it also reads the shared <c>~/.agents/skills</c>.</param>
/// <param name="AlsoReads">Other clients whose directory it reads too (OpenCode reads Claude Code's).</param>
public sealed record SkillPlace(string Kind, string SkillsDir, bool ReadsShared, IReadOnlyList<string> AlsoReads);

/// <summary>
/// The Skills directories of the clients Astra links into (<c>.plan/resources-library.md</c> §3). A table of its own rather than a
/// method on every adapter: it writes no client config, so the adapters and their golden cases stay as they are. Only clients whose
/// location is known are listed; the others show as unsupported.
/// </summary>
public static class SkillPlaces
{
    /// <summary>The directory several clients read (the Agent Skills convention).</summary>
    public static string SharedDir(ClientEnvironment env) => env.Combine(".agents", "skills");

    /// <summary>Every supported client kind, in display order.</summary>
    public static IReadOnlyList<string> SupportedKinds { get; } =
        [
            ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli, ClientKinds.OpenCode, ClientKinds.CopilotCli, ClientKinds.Droid, ClientKinds.GrokBuild,
            // added with the rest of the library (paths from magpie's library/targets.go, MIT; see .agents/memory/skill-library-decisions.md)
            ClientKinds.Pi, ClientKinds.Omp, ClientKinds.MiniMaxCode, ClientKinds.HermesAgent, ClientKinds.DeepSeekHarness, ClientKinds.CommandCode,
            ClientKinds.Crush, ClientKinds.ZCode, ClientKinds.AntigravityCli,
        ];

    public static bool Supports(string kind) => SupportedKinds.Contains(kind);

    /// <summary>Why a client has no Skills directory Astra links into, or null when it has one.</summary>
    public static string? UnsupportedReason(string kind) => Supports(kind) ? null : "unsupported";

    public static SkillPlace? For(ClientEnvironment env, string kind)
    {
        string Env(string name) => env.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "";
        switch (kind)
        {
            case ClientKinds.ClaudeCode:
                return new(kind, Path.Combine(Env("CLAUDE_CONFIG_DIR") is { Length: > 0 } c ? c : env.Combine(".claude"), "skills"), false, []);
            case ClientKinds.Codex:
                return new(kind, Path.Combine(Env("CODEX_HOME") is { Length: > 0 } x ? x : env.Combine(".codex"), "skills"), true, []);
            case ClientKinds.GeminiCli:
                return new(kind, env.Combine(".gemini", "skills"), true, []);
            case ClientKinds.OpenCode:
                var xdg = Env("XDG_CONFIG_HOME");
                return new(kind, Path.Combine(xdg.Length > 0 ? xdg : env.Combine(".config"), "opencode", "skills"), true, [ClientKinds.ClaudeCode]);
            case ClientKinds.CopilotCli:
                return new(kind, Path.Combine(Env("COPILOT_HOME") is { Length: > 0 } p ? p : env.Combine(".copilot"), "skills"), false, []);
            case ClientKinds.Droid:
                return new(kind, env.Combine(".factory", "skills"), true, []);
            case ClientKinds.GrokBuild:
                return new(kind, env.Combine(".grok", "skills"), true, []);
            case ClientKinds.Pi:
                return new(kind, Path.Combine(Env("PI_CODING_AGENT_DIR") is { Length: > 0 } pi ? pi : env.Combine(".pi", "agent"), "skills"), false, []);
            case ClientKinds.Omp:
                return new(kind, Path.Combine(Env("PI_CODING_AGENT_DIR") is { Length: > 0 } omp ? omp : env.Combine(".omp", "agent"), "skills"), false, []);
            case ClientKinds.MiniMaxCode:
                var minimax = Env("MINIMAX_DATA_DIR") is { Length: > 0 } m ? m : Env("MAVIS_DATA_DIR") is { Length: > 0 } mv ? mv : env.Combine(".minimax");
                return new(kind, Path.Combine(minimax, "skills"), false, []);
            case ClientKinds.HermesAgent:
                var hermes = Env("HERMES_HOME") is { Length: > 0 } h ? h
                    : env.Os == "windows" && Env("LOCALAPPDATA") is { Length: > 0 } local ? Path.Combine(local, "hermes") : env.Combine(".hermes");
                return new(kind, Path.Combine(hermes, "skills"), false, []);
            case ClientKinds.DeepSeekHarness:
                return new(kind, Path.Combine(Env("DSH_HOME") is { Length: > 0 } dsh ? dsh : env.Combine(".dsh"), "skills"), false, []);
            case ClientKinds.CommandCode:
                return new(kind, env.Combine(".commandcode", "skills"), false, []);
            case ClientKinds.Crush:
                var crush = Env("CRUSH_GLOBAL_CONFIG") is { Length: > 0 } cg ? cg : Path.Combine(xdgConfig(), "crush");
                return new(kind, Path.Combine(crush, "skills"), false, [ClientKinds.ClaudeCode]);
            case ClientKinds.ZCode:
                return new(kind, Path.Combine(Env("ZCODE_DESKTOP_HOME_DIR") is { Length: > 0 } z ? z : env.Combine(), ".zcode", "skills"), false, []);
            case ClientKinds.AntigravityCli:
                // agy reads its user-wide customisations from ~/.gemini/config (magpie's note)
                return new(kind, env.Combine(".gemini", "config", "skills"), false, []);
            default:
                return null;
        }
        string xdgConfig() => Env("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : env.Combine(".config");
    }

    /// <summary>
    /// The directory, relative to a project, a client reads the project's own Skills from (magpie's <c>projectSkillsDirs</c>): Claude Code
    /// <c>.claude/skills</c>, Command Code <c>.commandcode/skills</c>, the clients that read the shared convention <c>.agents/skills</c>; null
    /// for a client with no project folder to rely on.
    /// </summary>
    public static string? ProjectDir(string kind) => kind switch
    {
        ClientKinds.ClaudeCode => Path.Combine(".claude", "skills"),
        ClientKinds.CommandCode => Path.Combine(".commandcode", "skills"),
        ClientKinds.Codex or ClientKinds.GeminiCli or ClientKinds.OpenCode or ClientKinds.Pi or ClientKinds.CopilotCli
            or ClientKinds.AntigravityCli or ClientKinds.GrokBuild or ClientKinds.Droid or ClientKinds.DeepSeekHarness => Path.Combine(".agents", "skills"),
        _ => null,
    };

    /// <summary>The distinct project directories (relative) and the clients that read each.</summary>
    public static IReadOnlyList<(string Dir, IReadOnlyList<string> Clients)> ProjectDirs { get; } =
        [.. SupportedKinds.Where(k => ProjectDir(k) is not null).GroupBy(k => ProjectDir(k)!).Select(g => (g.Key, (IReadOnlyList<string>)g.ToList()))];
}
