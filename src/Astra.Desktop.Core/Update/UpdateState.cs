using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Astra.Desktop.Core.Update;

/// <summary>Executor phases, persisted verbatim (kebab-case) so the CLI, Electron app and this client render the same progress.</summary>
public enum UpdatePhase { Idle, Checking, Downloading, Swapping, Restarting, RollingBack, Failed }

/// <summary>
/// ~/.astra/update-state.json. Same shape as packages/update-core/src/state.ts. Unlike the TS, a null <c>Error</c> is
/// omitted rather than written as <c>"error": null</c>; every reader treats both as "no error".
/// </summary>
public sealed record UpdateState(
    UpdatePhase Phase,
    int FailedCount,
    string UpdatedAt,
    string? TargetVersion = null,
    string? PreviousVersion = null,
    string? Step = null,
    string? Error = null)
{
    /// <summary>After this many consecutive failed runs an update requires an explicit retry.</summary>
    public const int MaxConsecutiveFailures = 2;

    public static UpdateState Initial(Func<DateTimeOffset>? now = null) => new(UpdatePhase.Idle, 0, Stamp(now));

    /// <summary>The ISO-8601 UTC millisecond form JavaScript's <c>toISOString()</c> writes, so both writers produce one format.</summary>
    public static string Stamp(Func<DateTimeOffset>? now = null)
        => (now?.Invoke() ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string PhaseName(UpdatePhase p) => p switch
    {
        UpdatePhase.Idle => "idle",
        UpdatePhase.Checking => "checking",
        UpdatePhase.Downloading => "downloading",
        UpdatePhase.Swapping => "swapping",
        UpdatePhase.Restarting => "restarting",
        UpdatePhase.RollingBack => "rolling-back",
        _ => "failed",
    };

    private static UpdatePhase? ParsePhase(string? s) => s switch
    {
        "idle" => UpdatePhase.Idle,
        "checking" => UpdatePhase.Checking,
        "downloading" => UpdatePhase.Downloading,
        "swapping" => UpdatePhase.Swapping,
        "restarting" => UpdatePhase.Restarting,
        "rolling-back" => UpdatePhase.RollingBack,
        "failed" => UpdatePhase.Failed,
        _ => null,
    };

    /// <summary>Tolerant read: a missing, empty, corrupt or unknown-phase file is "no state".</summary>
    public static UpdateState? Read(string file)
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
            if (o.ValueKind != JsonValueKind.Object || ParsePhase(Str(o, "phase")) is not { } phase) return null;
            return new UpdateState(
                phase,
                o.TryGetProperty("failedCount", out var fc) && fc.ValueKind == JsonValueKind.Number && fc.TryGetInt32(out var n) ? n : 0,
                Str(o, "updatedAt") ?? Stamp(),
                Str(o, "targetVersion"), Str(o, "previousVersion"), Str(o, "step"), Str(o, "error"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes via tmp + rename so a crash mid-write never leaves a half file; stamps <c>updatedAt</c> with <paramref name="now"/>.</summary>
    public static void Write(string file, UpdateState state, Func<DateTimeOffset>? now = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var stream = File.Create(tmp))
        {
            using var w = new Utf8JsonWriter(stream, JsonFile.WriterOptions);
            w.WriteStartObject();
            w.WriteString("phase", PhaseName(state.Phase));
            if (state.TargetVersion is not null) w.WriteString("targetVersion", state.TargetVersion);
            if (state.PreviousVersion is not null) w.WriteString("previousVersion", state.PreviousVersion);
            if (state.Step is not null) w.WriteString("step", state.Step);
            if (state.Error is not null) w.WriteString("error", state.Error);
            w.WriteNumber("failedCount", state.FailedCount);
            w.WriteString("updatedAt", Stamp(now));
            w.WriteEndObject();
            w.Flush();
            stream.WriteByte((byte)'\n');
        }
        File.Move(tmp, file, overwrite: true);
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

internal static class JsonFile
{
    /// <summary>Two-space indent, LF, relaxed escaping: the bytes <c>JSON.stringify(x, null, 2)</c> writes for these shapes.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
