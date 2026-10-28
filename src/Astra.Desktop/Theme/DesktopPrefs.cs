using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Platform;

namespace Astra.Desktop.Theme;

/// <summary>Light / dark choice, as the web's and Electron's <c>ThemeSource</c>.</summary>
public enum ThemeSource { System, Light, Dark }

/// <summary>
/// The desktop shell's own preferences: <c>~/.astra/desktop-prefs.json</c>, beside <c>config.json</c> (plan avalonia-client §17).
/// Read before Avalonia starts (the theme must be right on the first frame), so it is plain file I/O with no Avalonia and
/// no reflection serializer (AOT). A missing, unreadable or malformed file yields the defaults and is never fatal.
/// <para>
/// First launch (no file yet) carries the Electron app's state over (<see cref="ElectronPrefsMigration"/>, plan §5.4): its own
/// <c>desktop-prefs.json</c> keys (<c>tray</c>, <c>closeHintShown</c>, <c>updateReminder</c>, kept here as the same top-level keys in the
/// same shape) and what its renderer kept in localStorage (theme, accent, language, sidebar, the remote-access acknowledgement).
/// </para>
/// </summary>
public sealed class DesktopPrefs
{
    public const string FileName = "desktop-prefs.json";
    /// <summary>The web's <c>accentChoice</c> for "follow the OS".</summary>
    public const string SystemAccent = "system";

    private readonly string? _path;

    // Top-level keys this class does not model (the tray's "tray" / "closeHintShown", Electron-era fields), kept verbatim so a Save never drops them.
    private static readonly HashSet<string> Modeled = new(StringComparer.Ordinal) { "theme", "accent", "lastSystemAccent", "sidebarCollapsed", "window", "language" };
    private readonly Dictionary<string, JsonNode?> _extra = new(StringComparer.Ordinal);

    private DesktopPrefs(string? path) => _path = path;

    /// <summary>A key this class does not model (a copy), or null; the tray stores its prefs this way (Components/Tray, Electron's shape).</summary>
    public JsonNode? GetExtra(string key) => _extra.TryGetValue(key, out var v) ? v?.DeepClone() : null;

    /// <summary>Stores (null removes) a key this class does not model and saves the file.</summary>
    public void SetExtra(string key, JsonNode? value)
    {
        if (Modeled.Contains(key)) throw new ArgumentException($"'{key}' is a modeled preference", nameof(key));
        if (value is null) _extra.Remove(key);
        else _extra[key] = value.DeepClone();
        Save();
    }

    /// <summary>Defaults that are never read from or written to disk (tests and scenes: a shell they build must not touch a real prefs file or each other's).</summary>
    public static DesktopPrefs InMemory() => new(null);

    public ThemeSource Theme { get; set; } = ThemeSource.System;

    /// <summary><see cref="SystemAccent"/> or a <c>#rrggbb</c> color (one of the web's presets).</summary>
    public string Accent { get; set; } = SystemAccent;

    /// <summary>The OS accent seen last run, so the first frame already carries it (the OS is only readable once Avalonia is up).</summary>
    public string? LastSystemAccent { get; set; }

    public bool SidebarCollapsed { get; set; }

    /// <summary>The main window's normal (not maximized) bounds in screen units (see <c>WindowBounds.ToScreenUnits</c>), remembered like Electron's <c>lastMainWindow</c>; null = default size, centered.</summary>
    public Box? WindowBounds { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>"zh" / "en", or null to follow the OS UI language.</summary>
    public string? Language { get; set; }

    public static string PathIn(AstraPaths paths) => Path.Combine(paths.Root, FileName);

    /// <summary>The key of the remote-access risk acknowledgement (the web's <c>astra.remote.acknowledged</c> localStorage flag).</summary>
    public const string RemoteAckKey = "remoteAccessAcknowledged";

    /// <summary>
    /// Loads the prefs; on the first launch (no file) migrates the Electron app's state first. Only for the default data directory:
    /// Electron's userData pairs with <c>~/.astra</c>, and a custom <c>ASTRA_HOME</c> is a test or scratch home that must not pick up the
    /// developer's real Electron settings. <c>ASTRA_ELECTRON_USERDATA</c> names the Electron directory explicitly (manual checks).
    /// </summary>
    public static DesktopPrefs Load(AstraPaths paths)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        IReadOnlyList<string> candidates = Environment.GetEnvironmentVariable("ASTRA_ELECTRON_USERDATA") is { Length: > 0 } explicitDir
            ? [explicitDir]
            : string.Equals(Path.GetFullPath(paths.Root).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Path.Combine(home, ".astra")), StringComparison.OrdinalIgnoreCase)
                ? ElectronPrefsMigration.UserDataCandidates(OsName(), home, Environment.GetEnvironmentVariable)
                : [];
        return Load(paths, candidates);
    }

    /// <summary>Loads the prefs from <paramref name="paths"/>; when the file does not exist yet, migrates from the first Electron <c>userData</c> candidate that has state.</summary>
    public static DesktopPrefs Load(AstraPaths paths, IReadOnlyList<string> electronUserData)
    {
        var path = PathIn(paths);
        if (!File.Exists(path) && electronUserData.Count > 0 && ElectronPrefsMigration.Find(electronUserData) is { } electron)
        {
            var migrated = new DesktopPrefs(path);
            migrated.Apply(electron);
            migrated.Save();
            return migrated;
        }
        return Read(path);
    }

    private static string OsName() => OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsWindows() ? "win32" : "linux";

    /// <summary>Takes over what the Electron app stored (each value only when it was stored there).</summary>
    internal void Apply(ElectronPrefs e)
    {
        if (e.Theme is { } t) Theme = t switch { "light" => ThemeSource.Light, "dark" => ThemeSource.Dark, _ => ThemeSource.System };
        if (e.Accent is { } a) Accent = a;
        if (e.SidebarCollapsed is { } c) SidebarCollapsed = c;
        if (e.Language is { } l) Language = l;
        // Electron's own file order: closeHintShown, tray, updateReminder (JSON.stringify of DesktopPrefs).
        if (e.CloseHintShown is { } shown) _extra["closeHintShown"] = shown;
        if (e.Tray is { } tray) _extra["tray"] = tray.DeepClone();
        if (e.UpdateReminder is { } reminder) _extra[ElectronPrefsMigration.ReminderKey] = reminder.DeepClone();
        if (e.RemoteAcknowledged == true) _extra[RemoteAckKey] = true;
    }

    private static DesktopPrefs Read(string path)
    {
        var prefs = new DesktopPrefs(path);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(prefs._path!));
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return prefs;
            if (Str(o, "theme") is { } t)
                prefs.Theme = t switch { "light" => ThemeSource.Light, "dark" => ThemeSource.Dark, _ => ThemeSource.System };
            if (Str(o, "accent") is { } a && (a == SystemAccent || IsHex(a))) prefs.Accent = a;
            if (Str(o, "lastSystemAccent") is { } l && IsHex(l)) prefs.LastSystemAccent = l;
            if (o.TryGetProperty("sidebarCollapsed", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
                prefs.SidebarCollapsed = c.GetBoolean();
            if (o.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object
                && Num(w, "x") is { } wx && Num(w, "y") is { } wy && Num(w, "width") is { } ww && Num(w, "height") is { } wh)
            {
                prefs.WindowBounds = new Box(wx, wy, ww, wh);
                prefs.WindowMaximized = w.TryGetProperty("maximized", out var mx) && mx.ValueKind == JsonValueKind.True;
            }
            if (Str(o, "language") is "zh" or "en") prefs.Language = Str(o, "language");
            foreach (var p in o.EnumerateObject())
                if (!Modeled.Contains(p.Name)) prefs._extra[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // First run, or a damaged file: defaults.
        }
        return prefs;
    }

    /// <summary>Writes the file (temp + rename, so a crash never leaves half a file). Failures are swallowed: prefs are a convenience.</summary>
    public void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("theme", Theme switch { ThemeSource.Light => "light", ThemeSource.Dark => "dark", _ => "system" });
                w.WriteString("accent", Accent);
                if (LastSystemAccent is not null) w.WriteString("lastSystemAccent", LastSystemAccent);
                w.WriteBoolean("sidebarCollapsed", SidebarCollapsed);
                if (WindowBounds is { } b)
                {
                    w.WriteStartObject("window");
                    w.WriteNumber("x", b.X);
                    w.WriteNumber("y", b.Y);
                    w.WriteNumber("width", b.Width);
                    w.WriteNumber("height", b.Height);
                    w.WriteBoolean("maximized", WindowMaximized);
                    w.WriteEndObject();
                }
                if (Language is not null) w.WriteString("language", Language);
                foreach (var (key, value) in _extra)
                {
                    w.WritePropertyName(key);
                    if (value is null) w.WriteNullValue();
                    else value.WriteTo(w);
                }
                w.WriteEndObject();
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Read-only home etc.: the choice just does not survive a restart.
        }
    }

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    private static bool IsHex(string s)
    {
        if (s.Length != 7 || s[0] != '#') return false;
        foreach (var c in s.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
