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
        [ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli, ClientKinds.OpenCode, ClientKinds.CopilotCli, ClientKinds.Droid, ClientKinds.GrokBuild];

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
            default:
                return null;
        }
    }
}
