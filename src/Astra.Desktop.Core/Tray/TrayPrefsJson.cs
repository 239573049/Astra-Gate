using System.Text.Json.Nodes;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>
/// The tray part of <c>desktop-prefs.json</c> in Electron's shape (desktop/src/shared/prefs.ts): <c>"tray": { "title": "requests",
/// "sections": { "overview": true, … } }</c> with the sections in <c>TRAY_SECTIONS</c> order, plus the top-level <c>closeHintShown</c>.
/// Keeping the shape means an Electron-era file can be copied over as is and an Avalonia-written one still reads in the Electron app
/// (AGENTS.md linked update 22). Parsing is as tolerant as <c>parseTrayPrefs</c>: an unknown title or a missing / non-boolean flag
/// falls back to the default one field at a time.
/// </summary>
public static class TrayPrefsJson
{
    /// <summary>The key of the tray object in the prefs file.</summary>
    public const string TrayKey = "tray";

    /// <summary>The key of the one-time "still running in the tray" hint flag.</summary>
    public const string CloseHintKey = "closeHintShown";

    /// <summary>Every section in display order (<c>TRAY_SECTIONS</c>).</summary>
    public static readonly TraySection[] Sections =
    [
        TraySection.Overview, TraySection.Cost, TraySection.Chart, TraySection.TopModel,
        TraySection.CompareYesterday, TraySection.CacheHitRate, TraySection.Quota, TraySection.Clients,
    ];

    /// <summary>Every title mode in order (<c>TRAY_TITLE_MODES</c>).</summary>
    public static readonly TrayTitleMode[] TitleModes =
        [TrayTitleMode.None, TrayTitleMode.Requests, TrayTitleMode.Tokens, TrayTitleMode.Cost, TrayTitleMode.Quota, TrayTitleMode.Alerts];

    /// <summary>The web / Electron name of a section (shared with Settings › Tray).</summary>
    public static string Name(TraySection s) => Settings.TrayLogic.Name(s);

    /// <summary>The web / Electron name of a title mode.</summary>
    public static string Name(TrayTitleMode m) => Settings.TrayLogic.Name(m);

    /// <summary>Reads the tray object (any JSON, or null) the way <c>parseTrayPrefs</c> does.</summary>
    public static TrayPrefs Parse(JsonNode? raw)
    {
        var o = raw as JsonObject;
        var title = TrayPrefs.Default.Title;
        if (o?["title"] is JsonValue tv && tv.TryGetValue<string>(out var t))
            foreach (var mode in TitleModes)
                if (Name(mode) == t) title = mode;
        var sections = new Dictionary<TraySection, bool>(TrayPrefs.Default.Sections);
        if (o?["sections"] is JsonObject so)
            foreach (var s in Sections)
                if (so[Name(s)] is JsonValue v && v.TryGetValue<bool>(out var on)) sections[s] = on;
        return new TrayPrefs(title, sections);
    }

    /// <summary>The tray object as Electron writes it: <c>title</c> then every section in order (all eight, always).</summary>
    public static JsonObject ToJson(TrayPrefs prefs)
    {
        var sections = new JsonObject();
        foreach (var s in Sections) sections[Name(s)] = prefs.Sections.TryGetValue(s, out var on) ? on : TrayPrefs.Default.Sections[s];
        return new JsonObject { ["title"] = Name(prefs.Title), ["sections"] = sections };
    }

    /// <summary>Whether two prefs say the same (records over dictionaries compare by reference).</summary>
    public static bool Same(TrayPrefs a, TrayPrefs b)
        => a.Title == b.Title && Sections.All(s => a.Sections.GetValueOrDefault(s) == b.Sections.GetValueOrDefault(s));
}

/// <summary>
/// Where the tray prefs live: a top-level key of the desktop prefs file. The app passes its <c>DesktopPrefs</c> (which keeps keys it does
/// not model and writes the file); tests pass <see cref="InMemoryPrefsFile"/>.
/// </summary>
public interface ITrayPrefsFile
{
    /// <summary>The raw JSON stored under <paramref name="key"/>, or null.</summary>
    JsonNode? Get(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> and saves the file.</summary>
    void Set(string key, JsonNode? value);
}

/// <summary>An <see cref="ITrayPrefsFile"/> that only lives in memory.</summary>
public sealed class InMemoryPrefsFile : ITrayPrefsFile
{
    /// <summary>What was stored, by key.</summary>
    public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public JsonNode? Get(string key) => Values.GetValueOrDefault(key)?.DeepClone();

    /// <inheritdoc />
    public void Set(string key, JsonNode? value) => Values[key] = value?.DeepClone();
}
