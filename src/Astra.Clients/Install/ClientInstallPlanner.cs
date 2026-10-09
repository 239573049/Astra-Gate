namespace Astra.Clients.Install;

/// <summary>
/// A command Astra runs for an install or update; arguments come only from the fixed catalog. <paramref name="Label"/>
/// overrides how it is shown (an elevated run is shown as the <c>sudo …</c> command it is equivalent to).
/// </summary>
public sealed record ToolCommand(string FileName, IReadOnlyList<string> Arguments, string? Label = null)
{
    /// <summary>The command as shown to the user ("npm install -g @openai/codex@latest").</summary>
    public string Display => Label ?? string.Join(' ', new[] { Path.GetFileNameWithoutExtension(FileName) }.Concat(Arguments));
}

/// <summary>What was found on the machine for one client (gathered by the server, consumed by the planner).</summary>
/// <param name="Installed">The client is present (its command was found, or the VS Code extension is installed).</param>
/// <param name="ExecutablePath">Where the client's command was found (null when missing).</param>
/// <param name="ResolvedPath">The same path with symlinks followed (the npm bin link points into the package).</param>
/// <param name="NpmPath">The npm executable, when available.</param>
/// <param name="NpmGlobalRoot">The output of <c>npm root -g</c> (the global node_modules directory).</param>
/// <param name="NpmPackagePresent">The client's package directory exists under <paramref name="NpmGlobalRoot"/>.</param>
public sealed record ClientToolState(
    bool Installed,
    string? ExecutablePath = null,
    string? ResolvedPath = null,
    string? NpmPath = null,
    string? NpmGlobalRoot = null,
    bool NpmPackagePresent = false);

/// <summary>
/// The install / update commands available for a client right now. <see cref="Blocker"/> says why an action
/// that would otherwise be offered is not: "npm-missing" (no npm to install with), "vscode-missing" (no
/// <c>code</c> command), "external-install" (installed some other way and the client has no updater of its own).
/// </summary>
public sealed record ClientInstallPlan(ToolCommand? Install, ToolCommand? Update, string? UpdateVia, string? Blocker)
{
    public static readonly ClientInstallPlan None = new(null, null, null, null);
}

/// <summary>Decides how a client can be installed or updated; pure, so every branch is unit-testable.</summary>
public static class ClientInstallPlanner
{
    public static ClientInstallPlan Plan(ClientInstallSpec spec, ClientToolState state, string os) => spec.Method switch
    {
        ClientInstallMethod.Npm => PlanNpm(spec, state, os),
        ClientInstallMethod.VsCodeExtension => PlanVsCode(spec, state),
        _ => ClientInstallPlan.None,
    };

    private static ClientInstallPlan PlanNpm(ClientInstallSpec spec, ClientToolState state, string os)
    {
        var npm = state.NpmPath is null ? null : new ToolCommand(state.NpmPath, ["install", "-g", $"{spec.Package}@latest"]);
        if (!state.Installed) return new ClientInstallPlan(npm, null, null, npm is null ? "npm-missing" : null);
        if (npm is not null && IsNpmManaged(spec, state, os)) return new ClientInstallPlan(null, npm, "npm", null);
        // A global npm install under another prefix (npm's prefix was changed since, e.g. to ~/.npm-global): update
        // that copy in place — installing into the new prefix would leave the old one first on PATH.
        if (state.NpmPath is { } npmPath && InstalledNpmPrefix(spec, state, os) is { } prefix)
            return new ClientInstallPlan(null, new ToolCommand(npmPath, ["install", "-g", "--prefix", prefix, $"{spec.Package}@latest"]), "npm", null);
        if (spec.SelfUpdateArgs is { } self && state.ExecutablePath is { } exe)
            return new ClientInstallPlan(null, new ToolCommand(exe, self), "self", null);
        return new ClientInstallPlan(null, null, null, "external-install");
    }

    /// <summary>
    /// The npm prefix that holds the installed copy when its command resolves into
    /// <c>&lt;prefix&gt;/lib/node_modules/&lt;package&gt;/</c> (the macOS / Linux layout of <c>npm install -g</c>),
    /// whatever npm's current prefix is; null otherwise (other package managers such as bun use other layouts).
    /// </summary>
    public static string? InstalledNpmPrefix(ClientInstallSpec spec, ClientToolState state, string os)
    {
        if (os == "windows" || spec.Package is null || state.ResolvedPath is not { } resolved) return null;
        var sep = Path.DirectorySeparatorChar;
        var marker = $"{sep}lib{sep}node_modules{sep}{spec.Package.Replace('/', sep)}{sep}";
        var index = resolved.IndexOf(marker, StringComparison.Ordinal);
        return index > 0 ? resolved[..index] : null;
    }

    private static ClientInstallPlan PlanVsCode(ClientInstallSpec spec, ClientToolState state)
    {
        // VS Code updates its extensions itself, so only installing is offered.
        if (state.Installed) return ClientInstallPlan.None;
        if (state.ExecutablePath is not { } code) return new ClientInstallPlan(null, null, null, "vscode-missing");
        return new ClientInstallPlan(new ToolCommand(code, ["--install-extension", spec.Package!]), null, null, null);
    }

    /// <summary>
    /// The plan's command for <paramref name="action"/> when it is an npm command — the only kind Astra will run
    /// with administrator rights (see <see cref="ClientElevation"/>); null otherwise.
    /// </summary>
    public static ToolCommand? NpmCommand(ClientInstallSpec spec, ClientInstallPlan plan, string action) => action switch
    {
        "install" when spec.Method == ClientInstallMethod.Npm => plan.Install,
        "update" when plan.UpdateVia == "npm" => plan.Update,
        _ => null,
    };

    /// <summary>
    /// True when the client command found on the machine belongs to the global npm install of its package: the
    /// bin link resolves into <c>&lt;root&gt;/&lt;package&gt;/</c> (macOS / Linux), or — Windows, where the bin
    /// entries are .cmd shims next to node_modules — the command sits in the npm prefix and the package is there.
    /// </summary>
    public static bool IsNpmManaged(ClientInstallSpec spec, ClientToolState state, string os)
    {
        if (spec.Package is null || state.NpmGlobalRoot is not { Length: > 0 } root || !state.NpmPackagePresent) return false;
        var windows = os == "windows";
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var packageDir = Path.TrimEndingDirectorySeparator(Path.Combine(root, spec.Package.Replace('/', Path.DirectorySeparatorChar)));
        if (state.ResolvedPath is { } resolved && resolved.StartsWith(packageDir + Path.DirectorySeparatorChar, comparison)) return true;
        if (windows && state.ExecutablePath is { } exe && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) is { } prefix)
            return string.Equals(Path.GetDirectoryName(exe), prefix, comparison);
        return false;
    }
}
