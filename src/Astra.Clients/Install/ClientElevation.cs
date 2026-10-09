namespace Astra.Clients.Install;

/// <summary>
/// Runs an npm install / update with administrator rights when npm's global directories belong to root (the usual
/// result of an earlier <c>sudo npm install -g</c>, or of Node installed by the official macOS package). The
/// system's own authorization prompt is used — macOS: <c>osascript … with administrator privileges</c>; Linux:
/// <c>pkexec</c> (polkit) — so Astra never sees the password. Windows is not covered (npm's global prefix is in
/// %APPDATA% there, which the user owns).
///
/// Only npm commands are elevated: running a client's own updater as root would leave root-owned files in the
/// user's home directory. The root npm gets a cache directory of its own so it cannot leave root-owned files in
/// <c>~/.npm</c> either.
/// </summary>
public static class ClientElevation
{
    /// <summary>The npm cache used by elevated runs (outside the user's home, see the class remarks).</summary>
    public const string RootNpmCache = "/tmp/astra-npm-root-cache";

    /// <summary>osascript on macOS; null where Astra cannot ask for admin rights.</summary>
    public static string? MacOsascript(string os) => os == "osx" && File.Exists("/usr/bin/osascript") ? "/usr/bin/osascript" : null;

    /// <summary>
    /// The npm command wrapped so it runs as root. <paramref name="environment"/> is passed explicitly because both
    /// <c>do shell script</c> and <c>pkexec</c> start from a minimal environment. <paramref name="elevator"/> is
    /// osascript (macOS) or pkexec (Linux); null when unavailable. The label shown to the user is
    /// <c>sudo &lt;command&gt;</c>, which is what the user would type to do the same thing by hand.
    /// </summary>
    public static ToolCommand? Wrap(ToolCommand npm, string os, string? elevator, IReadOnlyList<KeyValuePair<string, string>> environment, string prompt)
    {
        if (elevator is null) return null;
        var label = "sudo " + npm.Display;
        if (os == "osx")
        {
            var exports = string.Join(' ', environment.Select(kv => $"{kv.Key}={ShellQuote(kv.Value)}"));
            var shell = $"export {exports}; {string.Join(' ', new[] { npm.FileName }.Concat(npm.Arguments).Select(ShellQuote))} 2>&1";
            var script = $"do shell script {AppleScriptString(shell)} with prompt {AppleScriptString(prompt)} with administrator privileges without altering line endings";
            return new ToolCommand(elevator, ["-e", script], label);
        }
        if (os == "linux")
        {
            // pkexec clears the environment; env(1) puts back what npm needs.
            var args = new List<string> { "/usr/bin/env" };
            args.AddRange(environment.Select(kv => $"{kv.Key}={kv.Value}"));
            args.Add(npm.FileName);
            args.AddRange(npm.Arguments);
            return new ToolCommand(elevator, args, label);
        }
        return null;
    }

    /// <summary>A POSIX shell single-quoted word ("it's" → 'it'\''s').</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>An AppleScript string literal (backslashes and double quotes escaped).</summary>
    public static string AppleScriptString(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Directories npm must be able to write for this command: the global root, the scope directory and the package
    /// itself (npm renames the old copy aside, then deletes it), and the bin directory for the command links.
    /// </summary>
    public static IReadOnlyList<string> NpmWriteTargets(ClientInstallSpec spec, string npmRoot, string? binDirectory)
    {
        var targets = new List<string> { npmRoot };
        if (spec.Package is { } package)
        {
            var parts = package.Split('/');
            if (parts.Length == 2) targets.Add(Path.Combine(npmRoot, parts[0]));
            targets.Add(Path.Combine([npmRoot, .. parts]));
        }
        if (binDirectory is not null) targets.Add(binDirectory);
        return targets;
    }
}
