using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;


namespace Astra.Gateway.Plugins;

/// <summary>
/// Where the agent that asked works, told to a plugin's fetch (magpie's tellWorkPlace): a plugin runs in the
/// plugin host, whose working directory is Astra's, so one that tells its vendor the user's environment (ZCode's
/// Start Plan does) would say Astra's folder. The folder goes as <c>x-magpie-work-dir</c> (percent-encoded) and
/// whether it is in a git repository as <c>x-magpie-work-git</c> (yes / no); each is left off when unknown. The
/// host takes both off whatever the plugin sends on, so they reach no vendor.
/// </summary>
public static partial class WorkPlace
{
    public const string DirHeader = "x-magpie-work-dir";
    public const string GitHeader = "x-magpie-work-git";

    /// <summary>Puts the agent's folder (as the request body says it) on a request to a plugin's provider.</summary>
    public static void Tell(HttpRequestMessage request, string body)
    {
        request.Headers.Remove(DirHeader);
        request.Headers.Remove(GitHeader);
        var (dir, git) = Of(body);
        if (dir is null) return;
        git ??= GitOnDisk(dir);
        request.Headers.TryAddWithoutValidation(DirHeader, Uri.EscapeDataString(dir));
        if (git is not null) request.Headers.TryAddWithoutValidation(GitHeader, git);
    }

    /// <summary>
    /// The folder a request says its agent works in, and whether it is a git repository as the text that says
    /// the folder says it ("yes", "no", null when it doesn't). Each agent says it its own way:
    /// Claude Code " - Primary working directory: /x" (a move adds " (was /y)"), Codex's and Pi's &lt;cwd&gt;,
    /// OpenCode's "Working directory: /x" inside &lt;env&gt;. Of those said, the last goes. The model's own turns don't count.
    /// </summary>
    public static (string? Dir, string? Git) Of(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return (null, null);
        }
        string? dir = null, git = null;
        Walk(root, false, text =>
        {
            if (DirIn(text) is { } d)
            {
                dir = d;
                git = GitSaid(text);
            }
        });
        return (dir, git);
    }

    // Every string of the request in document order, but those of an assistant's (the model's) turn.
    private static void Walk(JsonNode? node, bool assistant, Action<string> look)
    {
        switch (node)
        {
            case JsonObject o:
                var role = o["role"] is JsonValue r && r.TryGetValue<string>(out var s) ? s : null;
                var mine = assistant || role is "assistant" or "model";
                foreach (var (_, child) in o) Walk(child, mine, look);
                break;
            case JsonArray a:
                foreach (var child in a) Walk(child, assistant, look);
                break;
            case JsonValue v when !assistant && v.TryGetValue<string>(out var text):
                look(text);
                break;
        }
    }

    private static string? DirIn(string text)
    {
        if (!text.Contains("irectory: ", StringComparison.Ordinal) && !text.Contains("<cwd>", StringComparison.Ordinal)) return null;
        var at = -1;
        string? dir = null;
        void Take(int i, string v)
        {
            v = WebUtility.HtmlDecode(v).Trim();
            if (v.Length > 0 && i > at)
            {
                at = i;
                dir = v;
            }
        }
        foreach (Match m in ClaudeCodeDir().Matches(text)) Take(m.Index, WasDir().Replace(m.Groups[1].Value, ""));
        foreach (Match m in CwdTag().Matches(text)) Take(m.Index, m.Groups[1].Value);
        // OpenCode's, only within its <env>
        for (var off = 0; ;)
        {
            var i = text.IndexOf("<env>", off, StringComparison.Ordinal);
            if (i < 0) break;
            var j = text.IndexOf("</env>", i, StringComparison.Ordinal);
            if (j < 0) break;
            var block = text[i..j];
            if (EnvDir().Match(block) is { Success: true } m) Take(i + m.Index, m.Groups[1].Value);
            off = j;
        }
        return dir;
    }

    private static string? GitSaid(string text)
    {
        var all = GitSaidPattern().Matches(text);
        if (all.Count == 0) return null;
        return all[^1].Groups[1].Value.ToLowerInvariant() is "true" or "yes" ? "yes" : "no";
    }

    // A folder on this computer: a .git in it or above it. Unknown (null), never "no", for one not here.
    private static string? GitOnDisk(string dir)
    {
        if (!Path.IsPathRooted(dir) || !Directory.Exists(dir)) return null;
        for (var d = Path.GetFullPath(dir); d is not null; d = Path.GetDirectoryName(d))
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return "yes";
        return "no";
    }

    [GeneratedRegex(@"^(?: - )?Primary working directory: (.+)$", RegexOptions.Multiline)]
    private static partial Regex ClaudeCodeDir();

    [GeneratedRegex(@"<cwd>\s*([^<\n]+?)\s*</cwd>")]
    private static partial Regex CwdTag();

    [GeneratedRegex(@"^\s*Working directory: (.+)$", RegexOptions.Multiline)]
    private static partial Regex EnvDir();

    [GeneratedRegex(@" \(was .*\)$")]
    private static partial Regex WasDir();

    [GeneratedRegex(@"^\s*(?:- )?Is (?:a git repository|directory a git repo): (true|false|yes|no)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GitSaidPattern();
}
