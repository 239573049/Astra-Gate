using System.Text.Json;

namespace Astra.Desktop.Core;

/// <summary>~/.astra/runtime.json as written by a running server.</summary>
public sealed record RuntimeInfo(
    int Pid,
    int Port,
    string? Host = null,
    string? Version = null,
    string? ApiVersion = null,
    string? StartedBy = null,
    string? StartedAt = null,
    string? RuntimeToken = null)
{
    /// <summary>Tolerant parse: null for missing or invalid content or without a positive integer pid; unknown fields are ignored.</summary>
    public static RuntimeInfo? Parse(string? text)
    {
        if (ServerFiles.Root(text) is not { } root) return null;
        using var doc = root;
        var o = doc.RootElement;
        if (!ServerFiles.Int(o, "pid", out var pid) || pid <= 0) return null;
        return new RuntimeInfo(
            pid,
            ServerFiles.Port(o, "port", ServerFiles.DefaultPort),
            ServerFiles.Str(o, "host"), ServerFiles.Str(o, "version"), ServerFiles.Str(o, "apiVersion"),
            ServerFiles.Str(o, "startedBy"), ServerFiles.Str(o, "startedAt"), ServerFiles.Str(o, "runtimeToken"));
    }

    /// <summary>
    /// True when runtime.json was written by the server spawned as <paramref name="pid"/> — or, for a wrapper
    /// (ASTRA_SERVER_BIN) that runs the server under another pid, by one that started no earlier than the spawn.
    /// An entry left by a server that was already running is neither.
    /// </summary>
    public bool IsFrom(int? pid, DateTimeOffset spawnedAt)
    {
        if (pid is { } p && Pid == p) return true;
        return DateTimeOffset.TryParse(StartedAt, out var started) && started >= spawnedAt;
    }
}

/// <summary>~/.astra/install.json as written by the CLI and the updaters (the desktop* fields are preserved elsewhere).</summary>
public sealed record InstallInfo(string? ServerPath, string? ServerVersion)
{
    public static InstallInfo? Parse(string? text)
    {
        if (ServerFiles.Root(text) is not { } root) return null;
        using var doc = root;
        return new InstallInfo(ServerFiles.Str(doc.RootElement, "serverPath"), ServerFiles.Str(doc.RootElement, "serverVersion"));
    }
}

/// <summary>The two fields of ~/.astra/config.json the desktop reads; defaults when the file is missing or malformed.</summary>
public sealed record ServerConfig(int Port, string Host)
{
    public static ServerConfig Default { get; } = new(ServerFiles.DefaultPort, "127.0.0.1");

    public static ServerConfig Parse(string? text)
    {
        if (ServerFiles.Root(text) is not { } root) return Default;
        using var doc = root;
        var host = ServerFiles.Str(doc.RootElement, "host") is { } h && !string.IsNullOrWhiteSpace(h) ? h : null;
        return new ServerConfig(ServerFiles.Port(doc.RootElement, "port", ServerFiles.DefaultPort), host ?? Default.Host);
    }
}

internal static class ServerFiles
{
    /// <summary>Default gateway port, matching config.json's default.</summary>
    public const int DefaultPort = 17321;

    public static JsonDocument? Root(string? text)
    {
        if (text is null) return null;
        try
        {
            var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    public static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public static bool Int(JsonElement o, string name, out int value)
    {
        value = 0;
        return o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out value);
    }

    /// <summary>An integer in [1, 65535] (a number or a numeric string), else <paramref name="fallback"/>.</summary>
    public static int Port(JsonElement o, string name, int fallback)
    {
        if (!o.TryGetProperty(name, out var v)) return fallback;
        var ok = v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? (true, n) : (false, 0),
            JsonValueKind.String => int.TryParse(v.GetString(), out var s) ? (true, s) : (false, 0),
            _ => (false, 0),
        };
        return ok.Item1 && ok.Item2 is >= 1 and <= 65535 ? ok.Item2 : fallback;
    }
}
