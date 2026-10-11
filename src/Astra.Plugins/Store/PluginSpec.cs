using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Plugins.Store;

/// <summary>
/// What a plugin's spec names, by magpie's (and OpenCode's) rules: a path to a file or folder, a git
/// repository bun installs from, or an npm package. Ported from magpie's internal/plugin/store.go.
/// </summary>
public static partial class PluginSpec
{
    /// <summary>Whether <paramref name="spec"/> names a file or folder rather than a package.</summary>
    public static bool IsPath(string spec) =>
        spec.StartsWith("file://", StringComparison.Ordinal) || spec.StartsWith('.') || spec.StartsWith('~')
        || Path.IsPathRooted(spec) || DriveLetter().IsMatch(spec);

    /// <summary>
    /// Whether <paramref name="spec"/> names a git repository: github:owner/repo[#ref] (gitlab:,
    /// bitbucket: alike), a git+https:// / git+ssh:// / git:// URL, a GitHub/GitLab/Bitbucket page URL,
    /// or GitHub's owner/repo shorthand.
    /// </summary>
    public static bool IsGit(string spec) => !IsPath(spec) && GitSpec().IsMatch(spec);

    /// <summary>Whether <paramref name="name"/> is a valid npm package name (scoped or not).</summary>
    public static bool IsPackageName(string name) => PackageNamePattern().IsMatch(name);

    /// <summary>
    /// The package <paramref name="spec"/> names, without its version: "@scope/x" of "@scope/x@1.2";
    /// for a git spec, the dependency bun recorded it under in the packages folder's package.json
    /// (the spec itself until it is installed); a path as it is.
    /// </summary>
    public static string Name(string spec, string packagesDir)
    {
        if (IsPath(spec)) return spec;
        if (IsGit(spec)) return GitName(spec, packagesDir) ?? spec;
        var at = spec.LastIndexOf('@');
        return at > 0 ? spec[..at] : spec;
    }

    /// <summary>Whether an npm spec pins a version or tag ("x@1.2", "x@next") other than latest.</summary>
    public static bool Pinned(string spec) =>
        !IsPath(spec) && !IsGit(spec) && spec.LastIndexOf('@') is > 0 and var at && spec[(at + 1)..] != "latest";

    /// <summary>Where the plugin loads from: the installed package's folder, or the path.</summary>
    public static string Target(string spec, string packagesDir)
    {
        if (IsPath(spec)) return ExpandPath(spec);
        return Path.Combine(packagesDir, "node_modules", Name(spec, packagesDir).Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>A path spec as an absolute path (file:// and ~ taken off).</summary>
    public static string ExpandPath(string spec)
    {
        var p = spec.StartsWith("file://", StringComparison.Ordinal) ? spec["file://".Length..] : spec;
        if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal))
            p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.Length > 2 ? p[2..] : "");
        return Path.GetFullPath(p);
    }

    /// <summary>
    /// A package's name without its scope and the words every package of its kind has: model-map for
    /// @magpie-community/middleware-model-map, zed for @magpie-community/opencode-zed-auth.
    /// </summary>
    public static string ShortName(string package)
    {
        var slash = package.LastIndexOf('/');
        if (slash >= 0) package = package[(slash + 1)..];
        if (package.StartsWith("middleware-", StringComparison.Ordinal)) package = package["middleware-".Length..];
        if (package.StartsWith("opencode-", StringComparison.Ordinal)) package = package["opencode-".Length..];
        return package.EndsWith("-auth", StringComparison.Ordinal) ? package[..^"-auth".Length] : package;
    }

    /// <summary>The version in the package.json at (or beside) <paramref name="target"/>; null when there is none.</summary>
    public static string? Version(string target) => PackageJsonString(target, "version");

    /// <summary>The npm package a spec is: its name, or for a path, the name its package.json gives.</summary>
    public static string PackageName(string spec, string packagesDir) =>
        IsPath(spec) ? PackageJsonString(Target(spec, packagesDir), "name") ?? spec : Name(spec, packagesDir);

    private static string? PackageJsonString(string target, string key)
    {
        var dir = File.Exists(target) ? Path.GetDirectoryName(target)! : target;
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "package.json")))?[key] is JsonValue v
                   && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // The dependency of the packages folder's package.json bun added the git spec as (it keeps the spec as given).
    private static string? GitName(string spec, string packagesDir)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(packagesDir, "package.json")))?["dependencies"] is not JsonObject deps)
                return null;
            foreach (var (name, value) in deps)
                if (value is JsonValue v && v.TryGetValue<string>(out var s) && s == spec) return name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex DriveLetter();

    [GeneratedRegex(@"^(@[a-z0-9][a-z0-9._~-]*/)?[a-z0-9][a-z0-9._~-]*$")]
    private static partial Regex PackageNamePattern();

    [GeneratedRegex(@"^(?:(?:github|gitlab|bitbucket):|git\+[a-z]+://|git://)\S+$|^https?://(?:www\.)?(?:github\.com|gitlab\.com|bitbucket\.org)/\S+$|^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9_.-]+(?:#\S+)?$")]
    private static partial Regex GitSpec();
}
