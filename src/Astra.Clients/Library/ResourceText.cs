using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>
/// The text side of the resource library: the marked section Astra owns inside an instruction file (the idea and the marker discipline are
/// magpie's, MIT: <c>library/instructions.go</c>), front matter and TOML rendering of sub-agents and commands, and the item JSON helpers.
/// Pure functions, so they are tested without a disk.
/// </summary>
public static partial class ResourceText
{
    // ---------------------------------------------------------------- the marked section

    public static string BeginMarker(string id) => $"<!-- astra:begin {id} -->";

    public static string EndMarker(string id) => $"<!-- astra:end {id} -->";

    /// <summary>The section's body (trimmed), or null when the file has no section for <paramref name="id"/>.</summary>
    public static string? GetBlock(string file, string id)
    {
        if (Find(file, id) is not { } f) return null;
        return NormalizeBody(file[f.BodyStart..f.BodyEnd]);
    }

    /// <summary>The file with the section set to <paramref name="body"/> (appended after the user's text when it is new), user text untouched.</summary>
    public static string SetBlock(string file, string id, string body)
    {
        var nl = file.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = BeginMarker(id) + nl + NormalizeBody(body).Replace("\n", nl) + nl + EndMarker(id);
        if (Find(file, id) is { } f) return file[..f.Start] + block + file[f.End..];
        var trimmed = file.TrimEnd('\r', '\n');
        return (trimmed.Length == 0 ? "" : trimmed + nl + nl) + block + nl;
    }

    /// <summary>The file without the section (and the blank line that separated it); the user's text is kept as it was.</summary>
    public static string RemoveBlock(string file, string id)
    {
        if (Find(file, id) is not { } f) return file;
        var before = file[..f.Start];
        var after = file[f.End..];
        // take out the line break that ended the end marker, and one blank line we added before the section
        if (after.StartsWith("\r\n", StringComparison.Ordinal)) after = after[2..];
        else if (after.StartsWith('\n')) after = after[1..];
        var nl = file.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        if (before.EndsWith(nl + nl, StringComparison.Ordinal) && after.Length == 0) before = before[..^nl.Length];
        else if (before.EndsWith(nl + nl, StringComparison.Ordinal) && (after.StartsWith(nl, StringComparison.Ordinal))) before = before[..^nl.Length];
        return before + after;
    }

    /// <summary>The ids of every Astra section in a file.</summary>
    public static IReadOnlyList<string> BlockIds(string file) =>
        [.. BeginPattern().Matches(file).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    [GeneratedRegex(@"<!-- astra:begin ([A-Za-z0-9._-]+) -->")]
    private static partial Regex BeginPattern();

    private readonly record struct Span(int Start, int End, int BodyStart, int BodyEnd);

    private static Span? Find(string file, string id)
    {
        var begin = BeginMarker(id);
        var end = EndMarker(id);
        var s = file.IndexOf(begin, StringComparison.Ordinal);
        if (s < 0) return null;
        var bodyStart = s + begin.Length;
        var e = file.IndexOf(end, bodyStart, StringComparison.Ordinal);
        if (e < 0) return null;
        return new Span(s, e + end.Length, bodyStart, e);
    }

    public static string NormalizeBody(string body) => body.Replace("\r\n", "\n").Trim('\n', '\r', ' ', '\t');

    public static string Hash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- scalars

    /// <summary>A string as a YAML / JSON double-quoted scalar (a JSON string is one).</summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A TOML multi-line basic string holding <paramref name="text"/>.</summary>
    public static string TomlMultiline(string text) =>
        "\"\"\"\n" + text.TrimEnd('\n', '\r').Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"\"\"", "\"\"\\\"", StringComparison.Ordinal) + "\n\"\"\"";

    public static string FrontMatter(IEnumerable<(string Key, string Value)> pairs, string body)
    {
        var sb = new StringBuilder("---\n");
        foreach (var (k, v) in pairs) sb.Append(k).Append(": ").Append(v).Append('\n');
        sb.Append("---\n\n").Append(body.TrimEnd('\n', '\r')).Append('\n');
        return sb.ToString();
    }

    // ---------------------------------------------------------------- slash command arguments

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex PositionalPattern();

    /// <summary>Whether a prompt uses <c>$1</c>, <c>$2</c>… (the words typed after the command, by place).</summary>
    public static bool UsesPositional(string prompt) => PositionalPattern().IsMatch(prompt);

    /// <summary>The prompt as a client reads arguments: <c>claude</c> counts from <c>$ARGUMENTS[0]</c>, <c>braces</c> says <c>{{args}}</c>.</summary>
    public static string Arguments(string prompt, string style) => style switch
    {
        "claude" => PositionalPattern().Replace(prompt, m =>
            int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is var n && n > 0 ? $"$ARGUMENTS[{n - 1}]" : "\\" + m.Value),
        "braces" => prompt.Replace("$ARGUMENTS", "{{args}}", StringComparison.Ordinal),
        _ => prompt,
    };

    // ---------------------------------------------------------------- item JSON

    public static IReadOnlyList<LibraryTarget> ParseTargets(string json)
    {
        var result = new List<LibraryTarget>();
        try
        {
            if (JsonNode.Parse(json) is JsonArray array)
                foreach (var n in array)
                    if (n is JsonObject o && o["client"] is JsonValue c && c.TryGetValue<string>(out var client) && client.Length > 0)
                        result.Add(new LibraryTarget(client, o["project"] is JsonValue p && p.TryGetValue<string>(out var proj) ? proj : ""));
        }
        catch (System.Text.Json.JsonException)
        {
            // unreadable targets read as none
        }
        return result;
    }

    public static string FormatTargets(IEnumerable<LibraryTarget> targets) =>
        new JsonArray([.. targets.Distinct().Select(t => (JsonNode)new JsonObject { ["client"] = t.Client, ["project"] = t.Project })]).ToJsonString();

    public static JsonObject Extra(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static string? ExtraString(JsonObject extra, string key) =>
        extra[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static int ExtraInt(JsonObject extra, string key) =>
        extra[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
}
