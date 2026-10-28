using System.Text.Json;

namespace Astra.Desktop.Core.Update;

/// <summary>
/// ~/.astra/install.json in full — the cross-process anchor the CLI, the Electron app and this client share. The
/// narrow <see cref="InstallInfo"/> is what the binary resolver reads; the updater needs every field because it
/// rewrites the file and must preserve the <c>desktop*</c> ones another updater owns. Port of install-info.ts: like the TS,
/// only the five known non-empty string fields survive a read-modify-write.
/// </summary>
public sealed record InstallJson(
    string? ServerPath = null,
    string? ServerVersion = null,
    string? DesktopPath = null,
    string? DesktopVersion = null,
    string? UpdatedAt = null)
{
    public static InstallJson? Read(string file)
    {
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            return new InstallJson(Str(o, "serverPath"), Str(o, "serverVersion"), Str(o, "desktopPath"), Str(o, "desktopVersion"), Str(o, "updatedAt"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var stream = File.Create(file);
        using (var w = new Utf8JsonWriter(stream, JsonFile.WriterOptions))
        {
            w.WriteStartObject();
            if (ServerPath is not null) w.WriteString("serverPath", ServerPath);
            if (ServerVersion is not null) w.WriteString("serverVersion", ServerVersion);
            if (DesktopPath is not null) w.WriteString("desktopPath", DesktopPath);
            if (DesktopVersion is not null) w.WriteString("desktopVersion", DesktopVersion);
            if (UpdatedAt is not null) w.WriteString("updatedAt", UpdatedAt);
            w.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
    }

    /// <summary>Points at a new server, keeping every other field (<c>applyServerInfo</c>).</summary>
    public static InstallJson WithServer(InstallJson? existing, string serverPath, string? serverVersion, Func<DateTimeOffset>? now = null)
        => (existing ?? new InstallJson()) with
        {
            ServerPath = serverPath,
            ServerVersion = string.IsNullOrEmpty(serverVersion) ? existing?.ServerVersion : serverVersion,
            UpdatedAt = UpdateState.Stamp(now),
        };

    /// <summary>
    /// Undoes <see cref="WithServer"/> for installs that had no managed server (the one bundled with the app): without a
    /// serverPath the resolvers fall back to the bundled binary. Deletes the file when nothing else was recorded in it.
    /// </summary>
    public static void ClearServer(string file, InstallJson? existing, Func<DateTimeOffset>? now = null)
    {
        var rest = (existing ?? new InstallJson()) with { ServerPath = null, ServerVersion = null, UpdatedAt = null };
        if (rest.DesktopPath is not null || rest.DesktopVersion is not null)
            (rest with { UpdatedAt = UpdateState.Stamp(now) }).Write(file);
        else
            File.Delete(file);
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
