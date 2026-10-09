using Astra.Core.Models;

namespace Astra.Core.Import;

/// <summary>Ids of the applications providers can be imported from (plan: provider import).</summary>
public static class ImportSourceIds
{
    public const string CcSwitch = "cc-switch";
    public const string Alma = "alma";
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
    public const string Magpie = "magpie";

    public static readonly IReadOnlyList<string> All = [CcSwitch, Alma, ClaudeCode, Codex, Magpie];
}

/// <summary>Why a candidate cannot be imported. Codes, not text: the web UI and CLI localize them.</summary>
public static class ImportSkipReasons
{
    /// <summary>An official login (Claude / ChatGPT / Copilot ...), not an API key: tokens are bound to the source app.</summary>
    public const string OfficialLogin = "official-login";
    public const string NoBaseUrl = "no-base-url";
    public const string UnsupportedProtocol = "unsupported-protocol";

    /// <summary>The base URL points back at this Astra gateway.</summary>
    public const string PointsToAstra = "points-to-astra";

    public const string InvalidBaseUrl = "invalid-base-url";

    /// <summary>A Codex provider that names an env var for its key instead of carrying one; env vars are never read.</summary>
    public const string NoInlineKey = "no-inline-key";
}

public sealed record ImportEndpoint(ApiProtocol Protocol, string BaseUrl);

/// <summary>
/// One provider found in another application. <see cref="ApiKey"/> is plaintext: this type lives only in memory
/// between a reader and <c>ImportService</c> and is deliberately NOT registered with any JSON context, so it can
/// never be serialized into an API response or a log line. The API exposes a masked projection instead.
/// </summary>
public sealed class ImportCandidate
{
    /// <summary>Stable id within a source, so a commit can re-find the entry after re-reading the source.</summary>
    public required string Ref { get; init; }

    public required string Source { get; init; }
    public required string Name { get; set; }

    /// <summary>The sub-kind in the source app (CC Switch's app type, Alma's provider type); display only.</summary>
    public string? FromApp { get; set; }

    public List<ImportEndpoint> Endpoints { get; set; } = [];
    public string AuthScheme { get; set; } = AuthSchemes.Bearer;
    public string? ApiKey { get; set; }
    public List<string> Models { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Proxy { get; set; }
    public string? Website { get; set; }

    /// <summary>The source app has it switched off; shown unticked by default.</summary>
    public bool Off { get; set; }

    /// <summary>Set when it cannot be imported; one of <see cref="ImportSkipReasons"/>.</summary>
    public string? SkipReason { get; set; }

    /// <summary>Source fields that were dropped (reserved headers, ...), by name.</summary>
    public List<string> IgnoredFields { get; set; } = [];
}

/// <summary>What one source yielded: where it looked, whether it exists, and its candidates.</summary>
public sealed class ImportSourceResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>The path(s) actually read; shown so the user can see what was touched.</summary>
    public string? Path { get; init; }

    public bool Found { get; set; }

    /// <summary>The source exists but could not be read (corrupt file, unknown schema, locked database).</summary>
    public string? Error { get; set; }

    public List<ImportCandidate> Items { get; } = [];
}

/// <summary>Thrown by <see cref="IForeignDbOpener"/> when another application's database cannot be opened.</summary>
public sealed class ForeignDbException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Read-only view of another application's SQLite database. Rows are plain strings keyed by column name.</summary>
public interface IForeignDb : IDisposable
{
    long UserVersion { get; }
    bool HasTable(string table);
    IReadOnlyList<string> Columns(string table);

    /// <summary>All rows of a table; NULL and BLOB cells come back as null.</summary>
    IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows(string table);
}

public interface IForeignDbOpener
{
    /// <summary>Opens read-only; never creates or modifies the file. Throws <see cref="ForeignDbException"/>.</summary>
    IForeignDb Open(string path);
}
