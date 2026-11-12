using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>What a palette entry does: show a route (with its param), or run a plugin command.</summary>
public sealed record PaletteTarget(string? Route, string? Param, string? Plugin, string? Command);

/// <summary>One entry of the command palette (web <c>plugin-ui/palette.tsx</c> <c>PaletteItem</c>).</summary>
public sealed record PaletteItem(string Id, string Label, string Group, string? Keybinding, PaletteTarget Target);

/// <summary>A key chord from a command's keybinding ("mod+shift+g"); <c>Mod</c> is ⌘ on macOS, Ctrl elsewhere.</summary>
public sealed record Chord(bool Mod, bool Shift, bool Alt, string Key);

/// <summary>
/// The command palette and plugin keybindings (design/plugin-v2 05 §8), shared logic of the web's <c>palette.tsx</c>: the
/// app's pages, v2 plugin pages, the commands plugins add to <c>command.palette</c>, and every command with a keybinding.
/// </summary>
public static class CommandPalette
{
    /// <summary>The items: <paramref name="pages"/> (route, label) first, then what the plugins contribute.</summary>
    public static List<PaletteItem> Items(PluginV2ContributionsDto? c, IEnumerable<(string Route, string Label)> pages)
    {
        var items = pages.Select(p => new PaletteItem("nav:" + p.Route, p.Label, "astra", null, new PaletteTarget(p.Route, null, null, null))).ToList();
        if (c is null) return items;
        foreach (var p in c.Pages)
            items.Add(new PaletteItem($"page:{p.Plugin}/{p.Id}", p.Title, p.Plugin, null, new PaletteTarget("PluginPage", $"{p.Plugin}/{p.Id}", null, null)));
        var seen = new HashSet<string>();
        foreach (var m in c.Menus.Where(m => m.Menu == "command.palette"))
        {
            seen.Add(m.Plugin + ":" + m.Command);
            var key = c.Commands.FirstOrDefault(x => x.Plugin == m.Plugin && x.Id == m.Command)?.Keybinding;
            items.Add(new PaletteItem($"cmd:{m.Plugin}:{m.Command}", m.Title, m.Plugin, key, new PaletteTarget(null, null, m.Plugin, m.Command)));
        }
        foreach (var x in c.Commands.Where(x => !string.IsNullOrEmpty(x.Keybinding) && !seen.Contains(x.Plugin + ":" + x.Id)))
            items.Add(new PaletteItem($"cmd:{x.Plugin}:{x.Id}", x.Title, x.Plugin, x.Keybinding, new PaletteTarget(null, null, x.Plugin, x.Id)));
        return items;
    }

    /// <summary>Items whose label, group or id contain every word of <paramref name="query"/> (case-insensitive).</summary>
    public static List<PaletteItem> Filter(IEnumerable<PaletteItem> items, string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return items.Where(i => words.All(w => $"{i.Label} {i.Group} {i.Id}".Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>"mod+shift+g" → a chord; null when it isn't one (a chord needs mod or alt).</summary>
    public static Chord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 0) return null;
        var key = parts[^1];
        bool mod = false, shift = false, alt = false;
        foreach (var p in parts[..^1])
        {
            if (p is "mod" or "cmd" or "ctrl") mod = true;
            else if (p == "shift") shift = true;
            else if (p is "alt" or "option") alt = true;
            else return null;
        }
        return mod || alt ? new Chord(mod, shift, alt, key) : null;
    }

    /// <summary>Whether a key press matches: <paramref name="code"/> is the DOM code ("KeyG", "Digit3"); mod is Meta on macOS, Ctrl elsewhere.</summary>
    public static bool Matches(Chord chord, string code, bool ctrl, bool alt, bool shift, bool meta, bool mac)
    {
        var mod = mac ? meta && !ctrl : ctrl && !meta;
        if (mod != chord.Mod || shift != chord.Shift || alt != chord.Alt) return false;
        var key = code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4 ? code[3..].ToLowerInvariant()
            : code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6 ? code[5..] : code.ToLowerInvariant();
        return key == chord.Key;
    }

    /// <summary>The command a key press runs (the first whose keybinding matches), or null.</summary>
    public static PluginV2CommandDto? CommandFor(PluginV2ContributionsDto? c, string code, bool ctrl, bool alt, bool shift, bool meta, bool mac) =>
        c?.Commands.FirstOrDefault(x => Parse(x.Keybinding) is { } chord && Matches(chord, code, ctrl, alt, shift, meta, mac));

    /// <summary>How a chord reads on this platform ("⌘⇧G" / "Ctrl+Shift+G").</summary>
    public static string Label(string text, bool mac)
    {
        if (Parse(text) is not { } c) return text;
        var key = c.Key.Length == 1 ? c.Key.ToUpperInvariant() : c.Key;
        return mac
            ? (c.Mod ? "⌘" : "") + (c.Alt ? "⌥" : "") + (c.Shift ? "⇧" : "") + key
            : string.Join("+", new[] { c.Mod ? "Ctrl" : null, c.Alt ? "Alt" : null, c.Shift ? "Shift" : null, key }.OfType<string>());
    }
}
