using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Clients.Install;

/// <summary>Version text helpers: pull a version out of <c>--version</c> output and compare two versions.</summary>
public static partial class ClientVersions
{
    [GeneratedRegex(@"(?<![\w.])v?(\d+\.\d+(?:\.\d+)*(?:-[0-9A-Za-z.-]+)?)(?![\w.])")]
    private static partial Regex VersionPattern();

    /// <summary>
    /// The first version-looking token in a tool's <c>--version</c> output ("codex-cli 0.160.1" → "0.160.1",
    /// "2.1.281 (Claude Code)" → "2.1.281"), or null when there is none.
    /// </summary>
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            var match = VersionPattern().Match(line);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Semver-style comparison: numeric dot parts first, then a release beats any prerelease of the same core
    /// ("1.2.0" &gt; "1.2.0-beta.1"); prerelease tags compare ordinally. Unparsable input never counts as newer.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (Split(candidate) is not { } c || Split(current) is not { } cur) return false;
        var length = Math.Max(c.Core.Length, cur.Core.Length);
        for (var i = 0; i < length; i++)
        {
            var a = i < c.Core.Length ? c.Core[i] : 0;
            var b = i < cur.Core.Length ? cur.Core[i] : 0;
            if (a != b) return a > b;
        }
        if (c.Pre is null) return cur.Pre is not null;
        if (cur.Pre is null) return false;
        return string.CompareOrdinal(c.Pre, cur.Pre) > 0;
    }

    private static (long[] Core, string? Pre)? Split(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim().TrimStart('v').Split('+', 2)[0];
        var parts = text.Split('-', 2);
        var core = new List<long>();
        foreach (var piece in parts[0].Split('.'))
        {
            if (!long.TryParse(piece, out var n)) return null;
            core.Add(n);
        }
        return (core.ToArray(), parts.Length > 1 ? parts[1] : null);
    }

    /// <summary>
    /// The installed version of a VS Code extension from VS Code's <c>extensions.json</c>
    /// (<c>[{ "identifier": { "id": "github.copilot-chat" }, "version": "0.32.1" }]</c>); ids match
    /// case-insensitively. Null when the extension is absent or the file is unreadable.
    /// </summary>
    public static string? VsCodeExtensionVersion(string? extensionsJson, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionsJson)) return null;
        try
        {
            if (JsonNode.Parse(extensionsJson) is not JsonArray entries) return null;
            string? best = null;
            foreach (var entry in entries)
            {
                if (entry is not JsonObject e) continue;
                var id = (e["identifier"]?["id"] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
                if (!string.Equals(id, extensionId, StringComparison.OrdinalIgnoreCase)) continue;
                var version = (e["version"] as JsonValue)?.TryGetValue(out string? v) == true ? v : null;
                // Several versions can be listed while an update is pending; the newest is the one that loads.
                if (version is not null && (best is null || IsNewer(version, best))) best = version;
            }
            return best;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The <c>CFBundleShortVersionString</c> of an XML <c>Info.plist</c> (macOS app bundles), or null.</summary>
    public static string? PlistShortVersion(string? plistXml)
    {
        if (string.IsNullOrWhiteSpace(plistXml)) return null;
        var match = Regex.Match(plistXml, @"<key>\s*CFBundleShortVersionString\s*</key>\s*<string>\s*([^<]+?)\s*</string>");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The version in an extension's <c>package.json</c> when its <c>publisher.name</c> matches
    /// <paramref name="extensionId"/> (case-insensitive) — used for extensions VS Code ships built in.
    /// </summary>
    public static string? PackageJsonVersion(string? packageJson, string extensionId)
    {
        if (string.IsNullOrWhiteSpace(packageJson)) return null;
        try
        {
            if (JsonNode.Parse(packageJson) is not JsonObject p) return null;
            var publisher = (p["publisher"] as JsonValue)?.TryGetValue(out string? pub) == true ? pub : null;
            var name = (p["name"] as JsonValue)?.TryGetValue(out string? n) == true ? n : null;
            if (!string.Equals($"{publisher}.{name}", extensionId, StringComparison.OrdinalIgnoreCase)) return null;
            return (p["version"] as JsonValue)?.TryGetValue(out string? v) == true ? v : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
