using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>Where the <c>claude</c> program is and what it says of itself.</summary>
public sealed record ClaudeCodeInfo(string Path, string? Version, string Source);

/// <summary>Who <c>claude auth status --json</c> says is signed in (config directory <paramref name="ConfigDir"/>).</summary>
public sealed record ClaudeSignIn(bool Known, bool LoggedIn, string? Email, string? Plan, string? Method, string? Organization, string? Error);

/// <summary>
/// Finds the <c>claude</c> program (Claude Code) the bridge drives: the path the settings name, else every folder of the search path
/// the client layer uses for tools (the PATH and where installers put programs), else where Claude Code's own installers put it. The newest
/// of several is run, as magpie's <c>claudecode.Own</c> does (MIT): an old npm install first on the PATH and the one <c>claude update</c> updated
/// after it are two programs, and the old one has no <c>auth login --claudeai</c>.
/// </summary>
public sealed partial class ClaudeCodeLocator(Func<IReadOnlyList<string>> searchPath, Func<string?> configured, Func<string> home)
{
    private readonly ConcurrentDictionary<string, (string? Version, DateTimeOffset At)> _versions = new(StringComparer.Ordinal);

    /// <summary>The program to run, or null when there is none.</summary>
    public ClaudeCodeInfo? Find()
    {
        if (configured() is { Length: > 0 } own)
            return File.Exists(own) ? new ClaudeCodeInfo(own, VersionOf(own), "settings") : null;
        var found = Candidates().ToList();
        if (found.Count == 0) return null;
        if (found.Count == 1) return new ClaudeCodeInfo(found[0].Path, VersionOf(found[0].Path), found[0].Source);
        // the newest by a version read where it is installed, running nothing; only when one is known that way is another asked --version
        var best = found[0];
        var bestVersion = InstalledVersion(best.Path);
        foreach (var c in found.Skip(1))
        {
            var v = InstalledVersion(c.Path) ?? (bestVersion is not null ? VersionOf(c.Path) : null);
            if (v is not null && (bestVersion is null || Compare(v, bestVersion) > 0)) (best, bestVersion) = (c, v);
        }
        return new ClaudeCodeInfo(best.Path, bestVersion ?? VersionOf(best.Path), best.Source);
    }

    private IEnumerable<(string Path, string Source)> Candidates()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        foreach (var dir in searchPath())
            foreach (var name in names)
            {
                var p = System.IO.Path.Combine(dir, name);
                if (File.Exists(p) && seen.Add(System.IO.Path.GetFullPath(p))) yield return (p, "path");
            }
        var h = home();
        string[] places = OperatingSystem.IsWindows()
            ? [System.IO.Path.Combine(h, ".local", "bin", "claude.exe"), System.IO.Path.Combine(h, ".claude", "local", "claude.exe")]
            : [System.IO.Path.Combine(h, ".local", "bin", "claude"), System.IO.Path.Combine(h, ".claude", "local", "claude"),
               "/opt/homebrew/bin/claude", "/usr/local/bin/claude", "/usr/bin/claude"];
        foreach (var p in places)
            if (File.Exists(p) && seen.Add(System.IO.Path.GetFullPath(p))) yield return (p, "installer");
    }

    /// <summary>The version a program's own install says (npm's package.json, the native installer's <c>versions/&lt;version&gt;</c>), running nothing.</summary>
    private static string? InstalledVersion(string path)
    {
        try
        {
            var real = new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path;
            var dir = System.IO.Path.GetDirectoryName(real) ?? "";
            if (System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(real) ?? "") == "versions" && Version().Match(System.IO.Path.GetFileName(real)) is { Success: true } m)
                return m.Value;
            for (var d = dir; d is { Length: > 0 }; d = System.IO.Path.GetDirectoryName(d))
            {
                var pkg = System.IO.Path.Combine(d, "package.json");
                if (File.Exists(pkg) && JsonNode.Parse(File.ReadAllText(pkg)) is JsonObject o && o["name"]?.GetValue<string>() == "@anthropic-ai/claude-code")
                    return o["version"]?.GetValue<string>();
                if (d.EndsWith("node_modules", StringComparison.Ordinal)) break;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        return null;
    }

    /// <summary><c>claude --version</c>, remembered for a minute (an unreadable one too).</summary>
    public string? VersionOf(string path)
    {
        if (_versions.TryGetValue(path, out var kept) && DateTimeOffset.UtcNow - kept.At < TimeSpan.FromMinutes(1)) return kept.Version;
        string? version = null;
        try
        {
            using var p = Start(path, ["--version"], null, null);
            if (p is not null)
            {
                var read = p.StandardOutput.ReadToEndAsync();
                if (p.WaitForExit(10_000) && Version().Match(read.GetAwaiter().GetResult()) is { Success: true } m) version = m.Value;
                else if (!p.HasExited) p.Kill(true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
        _versions[path] = (version, DateTimeOffset.UtcNow);
        return version;
    }

    /// <summary><c>claude auth status --json</c> in a config directory: who is signed in. Signed out, the program exits 1 but still prints the JSON.</summary>
    public async Task<ClaudeSignIn> SignInAsync(string path, string configDir, string? proxy, CancellationToken ct)
    {
        try
        {
            using var p = Start(path, ["auth", "status", "--json"], configDir, proxy);
            if (p is null) return new ClaudeSignIn(false, false, null, null, null, null, "claude did not start");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var output = await p.StandardOutput.ReadToEndAsync(timeout.Token);
            await p.WaitForExitAsync(timeout.Token);
            if (JsonNode.Parse(output) is not JsonObject o || o["loggedIn"] is not JsonValue li || !li.TryGetValue<bool>(out var loggedIn))
                return new ClaudeSignIn(false, false, null, null, null, null, "claude auth status said nothing it could be read from (an old claude?)");
            return new ClaudeSignIn(true, loggedIn, Text(o, "email"), Text(o, "subscriptionType"), Text(o, "authMethod"), Text(o, "orgName"), null);
        }
        catch (Exception e) when (e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new ClaudeSignIn(false, false, null, null, null, null, "claude auth status took too long");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new ClaudeSignIn(false, false, null, null, null, null, e.Message);
        }
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Starts the program for a short question (version, status): the run's clean environment, one config directory, the account's proxy.</summary>
    internal static Process? Start(string path, IReadOnlyList<string> args, string? configDir, string? proxy)
    {
        var psi = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        ClaudeProcess.ApplyEnvironment(psi, configDir, proxy, run: false);
        var p = Process.Start(psi);
        p?.StandardInput.Close();
        return p;
    }

    /// <summary>Compares two dotted versions numerically.</summary>
    public static int Compare(string a, string b)
    {
        var x = a.Split('.', '-', '+').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var y = b.Split('.', '-', '+').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            var d = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
            if (d != 0) return d;
        }
        return 0;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex Version();
}
