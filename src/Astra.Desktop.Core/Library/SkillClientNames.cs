namespace Astra.Desktop.Core.Library;

/// <summary>Display names of the clients the Skills page links into (brand names, so not part of the i18n catalog).</summary>
public static class SkillClientNames
{
    /// <summary>The name shown for a client kind.</summary>
    public static string Of(string kind) => kind switch
    {
        "claude-code" => "Claude Code",
        "codex" => "Codex",
        "gemini-cli" => "Gemini CLI",
        "opencode" => "OpenCode",
        "copilot-cli" => "Copilot CLI",
        "droid" => "Droid",
        "grok-build" => "Grok Build",
        _ => kind,
    };
}
