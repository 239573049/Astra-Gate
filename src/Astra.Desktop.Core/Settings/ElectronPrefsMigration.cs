using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// What the Electron app had stored that the Avalonia app carries over on its first launch (plan avalonia-client §5.4). Each field is
/// null when the Electron app never stored it, so the caller keeps its own default. <see cref="Tray"/> and <see cref="UpdateReminder"/>
/// are already in Electron's canonical shape (desktop/src/shared/prefs.ts, updateReminder.ts): the Avalonia prefs file keeps them as the
/// same top-level keys, byte-compatible with the Electron file (AGENTS.md linked update 22).
/// </summary>
/// <param name="Source">The Electron <c>userData</c> directory the values came from.</param>
/// <param name="Tray">The <c>tray</c> object (<c>{ title, sections }</c>).</param>
/// <param name="CloseHintShown">The one-time "still running in the tray" hint was shown.</param>
/// <param name="UpdateReminder">The update reminder circuit-breaker state (<c>{ version, count, lastRemindedAt, skippedVersion }</c>).</param>
/// <param name="Theme"><c>system</c> / <c>light</c> / <c>dark</c> (localStorage <c>astra.theme</c>).</param>
/// <param name="Accent"><c>system</c> or a <c>#rrggbb</c> color (localStorage <c>astra.accent</c>).</param>
/// <param name="SidebarCollapsed">localStorage <c>astra.sidebar</c> (<c>collapsed</c> / <c>expanded</c>).</param>
/// <param name="Language"><c>zh</c> / <c>en</c> (localStorage <c>astra.locale</c>).</param>
/// <param name="RemoteAcknowledged">The remote-access risk warning was accepted (localStorage <c>astra.remote.acknowledged</c>).</param>
public sealed record ElectronPrefs(
    string Source,
    JsonObject? Tray,
    bool? CloseHintShown,
    JsonObject? UpdateReminder,
    string? Theme,
    string? Accent,
    bool? SidebarCollapsed,
    string? Language,
    bool? RemoteAcknowledged)
{
    /// <summary>Nothing was found worth carrying over.</summary>
    public bool IsEmpty => Tray is null && CloseHintShown is null && UpdateReminder is null && Theme is null && Accent is null
        && SidebarCollapsed is null && Language is null && RemoteAcknowledged is null;
}

/// <summary>
/// Finds and reads the Electron-era desktop state. Electron kept two stores in its <c>userData</c> directory
/// (<c>app.getPath('userData')</c> = the OS app-data directory + <c>app.name</c>): <c>desktop-prefs.json</c> (main process: tray prefs,
/// <c>closeHintShown</c>, <c>updateReminder</c>; desktop/src/main.ts <c>prefsPath</c>) and the renderer's <c>localStorage</c> for
/// <c>app://astra</c> (theme, accent, language, sidebar, remote-access acknowledgement). The login item needs no migration: the
/// Avalonia app writes the very same entries (LoginItemService). Pure file reads with injectable roots, so it is tested with fake dirs.
/// </summary>
public static class ElectronPrefsMigration
{
    /// <summary>The renderer's origin (desktop/src/appProtocol.ts).</summary>
    public const string RendererOrigin = "app://astra";

    /// <summary>The Electron prefs file name inside <c>userData</c> (the same name the Avalonia app uses under <c>~/.astra</c>).</summary>
    public const string PrefsFileName = "desktop-prefs.json";

    /// <summary>
    /// The Electron app's <c>userData</c> directories to look in, most likely first. <c>app.name</c> is package.json's <c>productName</c>
    /// or else its <c>name</c>; desktop/package.json has no productName (electron-builder's <c>productName: Astra</c> names the bundle, not
    /// <c>app.name</c>), so it is <c>@aidotnet/desktop</c> — a nested directory. <c>Astra</c> is kept as a fallback in case a build set it.
    /// </summary>
    /// <param name="platform">"darwin", "win32" or "linux" (Node's names, as <see cref="Shell.IDesktopHost.Platform"/>).</param>
    /// <param name="home">The user's home directory.</param>
    /// <param name="env">Environment lookup (<c>APPDATA</c>, <c>XDG_CONFIG_HOME</c>).</param>
    public static IReadOnlyList<string> UserDataCandidates(string platform, string home, Func<string, string?> env)
    {
        var appData = platform switch
        {
            "darwin" => Path.Combine(home, "Library", "Application Support"),
            "win32" => NonEmpty(env("APPDATA")) ?? Path.Combine(home, "AppData", "Roaming"),
            _ => NonEmpty(env("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config"),
        };
        return [Path.Combine(appData, "@aidotnet", "desktop"), Path.Combine(appData, "Astra")];
    }

    /// <summary>The first candidate holding Electron state, read; null when none does (a fresh install, or never ran Electron).</summary>
    public static ElectronPrefs? Find(IEnumerable<string> candidates)
    {
        foreach (var dir in candidates)
        {
            var found = Read(dir);
            if (found is not null && !found.IsEmpty) return found;
        }
        return null;
    }

    /// <summary>Reads one <c>userData</c> directory; null when it does not exist. Never throws.</summary>
    public static ElectronPrefs? Read(string userData)
    {
        if (!Directory.Exists(userData)) return null;
        JsonObject? tray = null, reminder = null;
        bool? closeHint = null;
        try
        {
            var path = Path.Combine(userData, PrefsFileName);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
            {
                // parsePrefs: an absent key is the default, so only what Electron actually wrote is carried over.
                if (o.ContainsKey(TrayPrefsJson.TrayKey)) tray = TrayPrefsJson.ToJson(TrayPrefsJson.Parse(o[TrayPrefsJson.TrayKey]));
                if (o[TrayPrefsJson.CloseHintKey] is JsonValue ch && ch.TryGetValue<bool>(out var shown)) closeHint = shown;
                if (o.ContainsKey(ReminderKey)) reminder = ParseReminder(o[ReminderKey]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged Electron file: carry over what localStorage has.
        }

        var ls = ChromiumLocalStorage.Read(ChromiumLocalStorage.LevelDbDir(userData), RendererOrigin);
        string? theme = ls.GetValueOrDefault("astra.theme") is "system" or "light" or "dark" ? ls["astra.theme"] : null;
        string? accent = ls.GetValueOrDefault("astra.accent") is { } a && (a == "system" || IsHex(a)) ? a.ToLowerInvariant() : null;
        bool? sidebar = ls.GetValueOrDefault("astra.sidebar") switch { "collapsed" => true, "expanded" => false, _ => null };
        string? language = ls.GetValueOrDefault("astra.locale") is "zh" or "en" ? ls["astra.locale"] : null;
        bool? ack = ls.GetValueOrDefault("astra.remote.acknowledged") == "1" ? true : null;
        return new ElectronPrefs(userData, tray, closeHint, reminder, theme, accent, sidebar, language, ack);
    }

    /// <summary>The key of the update reminder state in the prefs file.</summary>
    public const string ReminderKey = "updateReminder";

    /// <summary>
    /// <c>parseReminderState</c>: malformed or missing fields fall back to the empty state one by one; written in the TS field order
    /// (<c>version, count, lastRemindedAt, skippedVersion</c>) so the object reads back identically in the Electron app.
    /// </summary>
    public static JsonObject ParseReminder(JsonNode? raw)
    {
        var o = raw as JsonObject;
        string? Str(string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) && s != "" ? s : null;
        // A node built in code holds its CLR type (long from Record), one parsed from text a JsonElement: read both.
        double? Num(string k) => o?[k] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null
            : null;
        var count = Num("count");
        var last = Num("lastRemindedAt");
        return new JsonObject
        {
            ["version"] = Str("version"),
            ["count"] = count is >= 0 ? (long)Math.Floor(count.Value) : 0,
            // Epoch ms; integral in practice (Date.now()), kept integral so the JSON text matches what Node wrote.
            ["lastRemindedAt"] = last is { } l ? (l == Math.Floor(l) && Math.Abs(l) < 9e15 ? JsonValue.Create((long)l) : JsonValue.Create(l)) : null,
            ["skippedVersion"] = Str("skippedVersion"),
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static bool IsHex(string s)
    {
        if (s.Length != 7 || s[0] != '#') return false;
        foreach (var c in s.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
