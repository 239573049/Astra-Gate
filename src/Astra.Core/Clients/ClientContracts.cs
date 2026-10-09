namespace Astra.Core.Clients;

/// <summary>Supported client kinds (fixed set shown in the client tabs).</summary>
public static class ClientKinds
{
    public const string Codex = "codex";
    public const string ClaudeCode = "claude-code";
    public const string GeminiCli = "gemini-cli";
    public const string OpenCode = "opencode";
    public const string ClaudeDesktop = "claude-desktop";
    public const string GrokBuild = "grok-build";
    public const string Pi = "pi";
    public const string HermesAgent = "hermes-agent";
    public const string MiniMaxCode = "minimax-code";
    public const string CopilotCli = "copilot-cli";
    public const string VsCodeCopilot = "vscode-copilot";
    public const string Crush = "crush";
    public const string QwenCode = "qwen-code";
    public const string Droid = "droid";
    public const string KimiCode = "kimi-code";
    public const string Zed = "zed";
    public const string VsCodeInsiders = "vscode-insiders";
    public const string VsCodium = "vscodium";
    public const string Omp = "omp";
    public const string MiMoCode = "mimo-code";
    public const string DeepSeekHarness = "deepseek-harness";
    public const string WorkBuddy = "workbuddy";
    public const string MuseCode = "muse-code";
    public const string NextCoWork = "nextcowork";

    public static readonly IReadOnlyList<string> All =
    [
        Codex, ClaudeCode, GeminiCli, OpenCode, ClaudeDesktop, GrokBuild, Pi, HermesAgent, MiniMaxCode, CopilotCli, VsCodeCopilot,
        Crush, QwenCode, Droid, KimiCode, Zed, VsCodeInsiders, VsCodium, Omp, MiMoCode,
        DeepSeekHarness, WorkBuddy, MuseCode, NextCoWork,
    ];

    /// <summary>
    /// Clients whose configuration lists the bound provider's models (kept current when that list changes). Muse Code
    /// writes no list, but takes its default model from it when none is selected (its model catalog needs one).
    /// </summary>
    public static readonly IReadOnlyList<string> WithModelList =
    [
        OpenCode, Pi, MiniMaxCode, CopilotCli, VsCodeCopilot,
        Crush, QwenCode, Droid, KimiCode, Zed, VsCodeInsiders, VsCodium, Omp, MiMoCode, DeepSeekHarness, WorkBuddy, NextCoWork,
        MuseCode,
    ];

    /// <summary>The inbound protocol each client speaks to the gateway.</summary>
    public static ApiProtocol ProtocolOf(string kind) => kind switch
    {
        Codex or GrokBuild or MuseCode => ApiProtocol.OpenAIResponses,
        ClaudeCode or ClaudeDesktop => ApiProtocol.Anthropic,
        GeminiCli => ApiProtocol.Gemini,
        _ => ApiProtocol.OpenAIChat,
    };
}

/// <summary>A client known to Astra (row of the <c>clients</c> table).</summary>
public sealed class ClientRecord
{
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>The token written into this client's configuration (as "&lt;token&gt;.&lt;kind&gt;"); null = the default token.</summary>
    public string? TokenId { get; set; }

    public string? SelectedModel { get; set; }

    /// <summary>Client-specific extras (Claude Code small model, Claude Desktop role map …) as JSON.</summary>
    public string? ExtraJson { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }
}

public sealed class ClientBinding
{
    public string ClientKind { get; set; } = "";
    public string ProviderId { get; set; } = "";

    /// <summary>
    /// 0 = primary. A client may bind several providers: the gateway routes a request to the first one (lowest
    /// priority) that serves the requested model id — one of its enabled models, or its model mapping onto one
    /// (<c>Provider.ModelMap</c>) — else to the primary.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Pins a concrete subscription account of the bound provider; null = the provider's default
    /// (first active) account. Only meaningful for providers with scheme "oauth-subscription".
    /// </summary>
    public string? AccountId { get; set; }
}

/// <summary>
/// The original and applied value of one key Astra wrote into a client config file.
/// Values are JSON text; <see cref="OriginalAbsent"/> means the key did not exist before we wrote it.
/// </summary>
public sealed class ClientConfigStateEntry
{
    public string ClientKind { get; set; } = "";
    public string FilePath { get; set; } = "";

    /// <summary>Dotted key path inside the file, e.g. "env.ANTHROPIC_BASE_URL" or "model_providers.astra".</summary>
    public string KeyPath { get; set; } = "";

    public bool OriginalAbsent { get; set; }
    public string? OriginalValueJson { get; set; }
    public string? AppliedValueJson { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>Persistence for <see cref="ClientConfigStateEntry"/> (implemented with Dapper in Astra.Data).</summary>
public interface IClientConfigStateStore
{
    IReadOnlyList<ClientConfigStateEntry> List(string clientKind);
    ClientConfigStateEntry? Get(string clientKind, string filePath, string keyPath);

    /// <summary>Insert or update by (client_kind, file_path, key_path).</summary>
    void Upsert(ClientConfigStateEntry entry);

    void Delete(string clientKind, string filePath, string keyPath);
    void DeleteAll(string clientKind);
}

/// <summary>Encrypts secrets at rest (API keys, gateway tokens). Implemented with ASP.NET DataProtection in the server.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}
