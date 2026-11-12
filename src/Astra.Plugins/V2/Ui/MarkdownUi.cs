using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Plugins.V2.Ui;

/// <summary>
/// The <c>Markdown</c> component, parsed by the host into ordinary nodes (design/plugin-v2 04 §7): headings, paragraphs,
/// emphasis, strikethrough, inline code, lists (nested, task items), links, quotes, rules, fenced code, GFM tables and
/// alert blocks. Raw HTML is shown as text, remote images are dropped (only the plugin's own and data: images), and a
/// link is kept only for https, astra: and in-plugin routes. One parser for both shells, so they can't disagree.
/// </summary>
public static partial class MarkdownUi
{
    /// <summary>The nodes for <paramref name="markdown"/> (a <c>Stack</c> of blocks); at most <paramref name="maxBlocks"/> blocks.</summary>
    public static JsonObject Render(string markdown, string? linkAction = null, int maxBlocks = 2000, string size = "md")
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new JsonArray();
        var i = 0;
        var truncated = false;
        Blocks(lines, ref i, lines.Length, blocks, linkAction, 0);
        if (blocks.Count > maxBlocks)
        {
            while (blocks.Count > maxBlocks) blocks.RemoveAt(blocks.Count - 1);
            truncated = true;
        }
        if (truncated)
            blocks.AddNode(new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = "…", ["tone"] = "neutral" } });
        return Node("Stack", new JsonObject { ["gap"] = size == "sm" ? "2" : "3" }, blocks);
    }

    private static void Blocks(string[] lines, ref int i, int end, JsonArray into, string? linkAction, int depth)
    {
        while (i < end)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                i++;
                continue;
            }
            // fenced code
            var fence = FencePattern().Match(line);
            if (fence.Success)
            {
                var marker = fence.Groups[1].Value;
                var lang = fence.Groups[2].Value.Trim();
                var code = new StringBuilder();
                i++;
                while (i < end && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[i]);
                    i++;
                }
                i++; // closing fence (or the end: an open block is still a code block)
                var props = new JsonObject { ["value"] = code.ToString(), ["copy"] = true };
                if (lang.Length > 0) props["language"] = lang.Split(' ')[0];
                into.AddNode(Node("Code", props));
                continue;
            }
            // heading
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var level = Math.Min(heading.Groups[1].Length, 4);
                into.AddNode(Node("Heading", new JsonObject { ["level"] = level, ["value"] = Plain(heading.Groups[2].Value.TrimEnd('#', ' ')) }));
                i++;
                continue;
            }
            // rule
            if (RulePattern().IsMatch(line))
            {
                into.AddNode(Node("Divider", new JsonObject()));
                i++;
                continue;
            }
            // quote / alert
            if (line.TrimStart().StartsWith('>'))
            {
                var quoted = new List<string>();
                while (i < end && lines[i].TrimStart().StartsWith('>'))
                {
                    var q = lines[i].TrimStart()[1..];
                    quoted.Add(q.StartsWith(' ') ? q[1..] : q);
                    i++;
                }
                var alert = quoted.Count > 0 ? AlertPattern().Match(quoted[0]) : Match.Empty;
                if (alert.Success)
                {
                    var tone = alert.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "TIP" => "success",
                        "WARNING" or "IMPORTANT" => "warning",
                        "CAUTION" => "danger",
                        _ => "accent",
                    };
                    var body = string.Join(' ', quoted.Skip(1).Select(q => q.Trim()).Where(q => q.Length > 0));
                    into.AddNode(Node("Alert", new JsonObject { ["tone"] = tone, ["title"] = alert.Groups[1].Value[..1] + alert.Groups[1].Value[1..].ToLowerInvariant(), ["message"] = Plain(body) }));
                    continue;
                }
                var inner = new JsonArray();
                var j = 0;
                var arr = quoted.ToArray();
                Blocks(arr, ref j, arr.Length, inner, linkAction, depth + 1);
                into.AddNode(Node("Box", new JsonObject { ["surface"] = "sunken", ["padding"] = "3" }, [Node("Stack", new JsonObject { ["gap"] = "2" }, inner)]));
                continue;
            }
            // table
            if (line.Contains('|') && i + 1 < end && TableRulePattern().IsMatch(lines[i + 1]))
            {
                into.AddNode(Table(lines, ref i, end, linkAction));
                continue;
            }
            // list
            if (ListPattern().IsMatch(line))
            {
                into.AddNode(List(lines, ref i, end, linkAction, depth));
                continue;
            }
            // paragraph: lines until a blank line or another block starts
            var para = new StringBuilder(line.Trim());
            i++;
            while (i < end && !string.IsNullOrWhiteSpace(lines[i]) && !StartsBlock(lines, i, end))
            {
                para.Append(lines[i].EndsWith("  ", StringComparison.Ordinal) ? "\n" : " ").Append(lines[i].Trim());
                i++;
            }
            into.AddNode(Rich(para.ToString(), linkAction));
        }
    }

    private static bool StartsBlock(string[] lines, int i, int end) =>
        FencePattern().IsMatch(lines[i]) || HeadingPattern().IsMatch(lines[i]) || RulePattern().IsMatch(lines[i])
        || lines[i].TrimStart().StartsWith('>') || ListPattern().IsMatch(lines[i])
        || (lines[i].Contains('|') && i + 1 < end && TableRulePattern().IsMatch(lines[i + 1]));

    private static JsonObject List(string[] lines, ref int i, int end, string? linkAction, int depth)
    {
        var first = ListPattern().Match(lines[i]);
        var indent = first.Groups[1].Length;
        var ordered = char.IsDigit(first.Groups[2].Value[0]);
        var items = new JsonArray();
        var number = ordered && int.TryParse(first.Groups[2].Value.TrimEnd('.', ')'), out var n) ? n : 1;
        while (i < end)
        {
            var m = ListPattern().Match(lines[i]);
            if (!m.Success || m.Groups[1].Length != indent) break;
            var text = m.Groups[3].Value;
            bool? task = null;
            var t = TaskPattern().Match(text);
            if (t.Success)
            {
                task = t.Groups[1].Value is "x" or "X";
                text = t.Groups[2].Value;
            }
            i++;
            // continuation lines and nested lists (more indented)
            var nested = new JsonArray();
            while (i < end && !string.IsNullOrWhiteSpace(lines[i]) && LeadingSpaces(lines[i]) > indent)
            {
                if (ListPattern().IsMatch(lines[i])) nested.AddNode(List(lines, ref i, end, linkAction, depth + 1));
                else
                {
                    text += " " + lines[i].Trim();
                    i++;
                }
            }
            var marker = task is { } done ? (done ? "☑" : "☐") : ordered ? number++ + "." : depth % 2 == 0 ? "•" : "◦";
            var row = Node("Stack", new JsonObject { ["direction"] = "row", ["gap"] = "2", ["align"] = "start" },
                [Node("Text", new JsonObject { ["value"] = marker, ["tone"] = "neutral" }), Rich(text, linkAction)]);
            if (nested.Count > 0)
                items.AddNode(Node("Stack", new JsonObject { ["gap"] = "1" }, [row, Node("Box", new JsonObject { ["padding"] = "0" }, [Node("Stack", new JsonObject { ["gap"] = "1", ["padding"] = "4" }, nested)])]));
            else items.AddNode(row);
            if (i < end && string.IsNullOrWhiteSpace(lines[i]) && i + 1 < end && ListPattern().Match(lines[i + 1]) is { Success: true } next && next.Groups[1].Length == indent) i++;
        }
        return Node("Stack", new JsonObject { ["gap"] = "1" }, items);
    }

    private static JsonObject Table(string[] lines, ref int i, int end, string? linkAction)
    {
        var header = Cells(lines[i]);
        var aligns = Cells(lines[i + 1]).Select(c => c.StartsWith(':') && c.EndsWith(':') ? "center" : c.EndsWith(':') ? "end" : "start").ToList();
        i += 2;
        var columns = new JsonArray();
        for (var c = 0; c < header.Count; c++)
            columns.AddNode(new JsonObject { ["key"] = "c" + c, ["title"] = Plain(header[c]), ["align"] = c < aligns.Count ? aligns[c] : "start" });
        var rows = new JsonArray();
        var r = 0;
        while (i < end && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
        {
            var cells = Cells(lines[i]);
            var row = new JsonObject { ["id"] = "r" + r++ };
            for (var c = 0; c < header.Count; c++) row["c" + c] = c < cells.Count ? Plain(cells[c]) : "";
            rows.AddNode(row);
            i++;
        }
        _ = linkAction;
        return Node("Table", new JsonObject { ["columns"] = columns, ["rows"] = rows, ["rowKey"] = "id", ["dense"] = true });
    }

    private static List<string> Cells(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|", StringComparison.Ordinal)) t = t[..^1];
        return [.. CellSplit().Split(t).Select(c => c.Replace("\\|", "|").Trim())];
    }

    private static int LeadingSpaces(string s)
    {
        var n = 0;
        foreach (var c in s)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    /// <summary>A paragraph's inline markup as a <c>RichText</c> node.</summary>
    public static JsonObject Rich(string text, string? linkAction = null) =>
        Node("RichText", new JsonObject { ["spans"] = Spans(text, linkAction) });

    /// <summary>Inline markup → spans: **bold**, *italic* / _italic_, ~~strike~~, `code`, [text](url), &lt;autolinks&gt;, \escapes.</summary>
    public static JsonArray Spans(string text, string? linkAction = null)
    {
        var spans = new JsonArray();
        Inline(text, false, false, false, null, spans, linkAction);
        return spans;
    }

    private static void Inline(string text, bool bold, bool italic, bool strike, string? link, JsonArray into, string? linkAction)
    {
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            into.AddNode(Span(plain.ToString(), bold, italic, strike, false, link, linkAction));
            plain.Clear();
        }
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && "\\`*_{}[]()#+-.!|~<>".Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    Flush();
                    into.AddNode(Span(text[(i + 1)..close], bold, italic, strike, true, link, linkAction));
                    i = close + 1;
                    continue;
                }
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[' && LinkAt(text, i + 1, out var alt, out _, out var imgEnd))
            {
                // images are not inline content: keep the alt text
                plain.Append(alt);
                i = imgEnd;
                continue;
            }
            if (c == '[' && link is null && LinkAt(text, i, out var label, out var url, out var linkEnd))
            {
                Flush();
                var safe = SafeLink(url);
                Inline(label, bold, italic, strike, safe ?? "", into, safe is null ? null : linkAction);
                i = linkEnd;
                continue;
            }
            if (c == '<' && link is null)
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i && SafeLink(text[(i + 1)..close]) is { } auto && auto.StartsWith("https://", StringComparison.Ordinal))
                {
                    Flush();
                    into.AddNode(Span(auto, bold, italic, strike, false, auto, linkAction));
                    i = close + 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                var close = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush();
                    Inline(text[(i + 2)..close], true, italic, strike, link, into, linkAction);
                    i = close + 2;
                    continue;
                }
            }
            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    Flush();
                    Inline(text[(i + 2)..close], bold, italic, true, link, into, linkAction);
                    i = close + 2;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var close = text.IndexOf(c, i + 1);
                while (close > 0 && close + 1 < text.Length && text[close + 1] == c) close = text.IndexOf(c, close + 2);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]) && (c == '*' || close + 1 >= text.Length || !char.IsLetterOrDigit(text[close + 1])))
                {
                    Flush();
                    Inline(text[(i + 1)..close], bold, true, strike, link, into, linkAction);
                    i = close + 1;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        Flush();
    }

    private static bool LinkAt(string text, int open, out string label, out string url, out int end)
    {
        label = url = "";
        end = open;
        var depth = 0;
        var close = -1;
        for (var k = open; k < text.Length; k++)
        {
            if (text[k] == '[') depth++;
            else if (text[k] == ']' && --depth == 0)
            {
                close = k;
                break;
            }
        }
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;
        var paren = -1;
        var depthParen = 0;
        for (var k = close + 1; k < text.Length; k++)
        {
            if (text[k] == '(') depthParen++;
            else if (text[k] == ')' && --depthParen == 0)
            {
                paren = k;
                break;
            }
        }
        if (paren < 0) return false;
        label = text[(open + 1)..close];
        url = text[(close + 2)..paren].Trim().Split(' ')[0].Trim('<', '>');
        end = paren + 1;
        return true;
    }

    /// <summary>The link a Markdown link may keep: https, astra:, an in-plugin route (#… or /…); null for anything else.</summary>
    public static string? SafeLink(string url)
    {
        url = url.Trim();
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        if (url.StartsWith("astra://", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.StartsWith('#') || (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal))) return url;
        return null;
    }

    private static JsonObject Span(string text, bool bold, bool italic, bool strike, bool code, string? link, string? linkAction)
    {
        var s = new JsonObject { ["text"] = text };
        if (bold) s["bold"] = true;
        if (italic) s["italic"] = true;
        if (strike) s["strike"] = true;
        if (code) s["code"] = true;
        if (!string.IsNullOrEmpty(link))
        {
            s["link"] = link;
            if (linkAction is not null) s["action"] = linkAction;
        }
        return s;
    }

    private static string Plain(string text)
    {
        var sb = new StringBuilder();
        foreach (var span in Spans(text)) sb.Append(span!["text"]!.GetValue<string>());
        return sb.ToString();
    }

    private static JsonObject Node(string type, JsonObject props, IEnumerable<JsonNode?>? children = null)
    {
        var n = new JsonObject { ["type"] = type, ["props"] = props };
        if (children is not null)
        {
            var list = new JsonArray();
            foreach (var c in children) list.Add(c?.Parent is null ? c : c.DeepClone());
            if (list.Count > 0) n["children"] = list;
        }
        return n;
    }

    [GeneratedRegex("^\\s{0,3}(```+|~~~+)(.*)$")]
    private static partial Regex FencePattern();

    [GeneratedRegex("^\\s{0,3}(#{1,6})\\s+(.*)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex("^\\s{0,3}([-*_])(\\s*\\1){2,}\\s*$")]
    private static partial Regex RulePattern();

    [GeneratedRegex("^\\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\\]\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex AlertPattern();

    [GeneratedRegex("^\\s*\\|?\\s*:?-+:?\\s*(\\|\\s*:?-+:?\\s*)*\\|?\\s*$")]
    private static partial Regex TableRulePattern();

    [GeneratedRegex("^(\\s*)([-*+]|\\d{1,9}[.)])\\s+(.*)$")]
    private static partial Regex ListPattern();

    [GeneratedRegex("^\\[([ xX])\\]\\s+(.*)$")]
    private static partial Regex TaskPattern();

    [GeneratedRegex("(?<!\\\\)\\|")]
    private static partial Regex CellSplit();
}
