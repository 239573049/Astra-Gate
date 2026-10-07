using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Clients.Config;

/// <summary>Encoding helpers for the JSON value texts stored in <see cref="ConfigChange"/> and client_config_state.</summary>
public static class ConfigValueCodec
{
    /// <summary>
    /// Encodes a raw value source text as the JSON text stored in plans/state. For JSON and YAML files the
    /// value text is already JSON and is returned as-is; for TOML/.env the raw source text is JSON-quoted.
    /// </summary>
    public static string? Encode(ConfigFileFormat format, string? rawSourceText)
    {
        if (rawSourceText is null) return null;
        return IsJsonValued(format) ? rawSourceText : JsonSerializer.Serialize(rawSourceText);
    }

    /// <summary>Decodes stored JSON text back into the raw value source text to splice into the file.</summary>
    public static string? DecodeToSource(ConfigFileFormat format, string? jsonText)
    {
        if (jsonText is null) return null;
        return IsJsonValued(format) ? jsonText : JsonSerializer.Deserialize<string>(jsonText);
    }

    /// <summary>Compares the current value source text against the recorded JSON text (structure-aware for JSON and YAML).</summary>
    public static bool EqualsValue(ConfigFileFormat format, string? currentSource, string? recordedJson)
    {
        if (recordedJson is null) return currentSource is null;
        if (currentSource is null) return false;
        if (IsJsonValued(format))
        {
            var a = ParseNode(currentSource);
            var b = ParseNode(recordedJson);
            return a is not null && b is not null && JsonNode.DeepEquals(a, b);
        }
        return string.Equals(currentSource, DecodeToSource(format, recordedJson), StringComparison.Ordinal);
    }

    /// <summary>Formats whose editors exchange values as JSON text (JSON itself, and YAML through its JSON data model).</summary>
    public static bool IsJsonValued(ConfigFileFormat format) => format is ConfigFileFormat.Json or ConfigFileFormat.Yaml;

    private static JsonNode? ParseNode(string text)
    {
        try
        {
            return JsonNode.Parse(text, nodeOptions: new JsonNodeOptions(),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One key applied by <see cref="ClientConfigApplier.Apply"/>.</summary>
public sealed record AppliedKey(string FilePath, string KeyPath, bool WasAbsent, string? BeforeJson, string? AfterJson);

/// <summary>Result of <see cref="ClientConfigApplier.Apply"/>.</summary>
public sealed class ApplyReport
{
    public required string ClientKind { get; init; }
    public required IReadOnlyList<AppliedKey> Applied { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Outcome for one key during a restore.</summary>
public sealed record RestoredKey(string FilePath, string KeyPath, RestoreKeyOutcome Outcome, string? Detail = null);

public enum RestoreKeyOutcome
{
    /// <summary>The original value was written back (or the key removed because it was originally absent).</summary>
    Restored,

    /// <summary>The current value differs from what Astra wrote; left untouched.</summary>
    Drifted,

    /// <summary>No state entry existed for this key (never taken over, or already restored).</summary>
    NotTracked,
}

/// <summary>Result of a disable/force-restore/restore-all run.</summary>
public sealed class RestoreReport
{
    public required string ClientKind { get; init; }
    public required IReadOnlyList<RestoredKey> Keys { get; init; }

    /// <summary>Files deleted because Astra had created them and they became empty again.</summary>
    public required IReadOnlyList<string> DeletedFiles { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public bool HasDrift => Keys.Any(k => k.Outcome == RestoreKeyOutcome.Drifted);
}

/// <summary>Metadata for one backup directory.</summary>
public sealed record BackupInfo(string ClientKind, string BackupId, DateTimeOffset CreatedAt, bool IsFirstWrite,
    IReadOnlyList<BackupFileEntry> Files);

public sealed record BackupFileEntry(string OriginalPath, bool Existed, string? BackupFile);
