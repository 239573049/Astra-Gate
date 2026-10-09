using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Clients.Config;
using Astra.Clients.Install;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>Install / version state of one client, embedded in <see cref="ClientInfoDto"/>.</summary>
/// <param name="Method">"npm", "vscode-extension" or "manual".</param>
/// <param name="Installed">The client's command (or VS Code extension / desktop app) was found.</param>
/// <param name="UpdateAvailable">The registry has a newer version than the installed one.</param>
/// <param name="InstallCommand">What "install" would run; null when installing is not offered.</param>
/// <param name="UpdateCommand">What "update" would run; null when updating is not offered.</param>
/// <param name="UpdateVia">"npm" (global npm package) or "self" (the client's own updater).</param>
/// <param name="Blocker">
/// Why an action is unavailable: "npm-missing", "vscode-missing", "external-install", or "shadowed" (the latest version
/// is already installed behind the copy that runs — another update would change nothing; see <c>OtherCopies</c>).
/// </param>
/// <param name="Busy">An install / update of this client is running.</param>
/// <param name="NeedsAdmin">The npm install / update would fail for the current user: npm's global directories are not writable (typically root-owned).</param>
/// <param name="AdminAvailable">The npm command on offer can be run with administrator rights through the system prompt (macOS, or Linux with pkexec).</param>
/// <param name="AdminFixCommand">
/// When admin rights are needed: the one-time terminal command that gives npm's global directories back to the
/// user (<c>sudo chown -R "$(whoami)" …</c>), after which installs no longer need them.
/// </param>
/// <param name="OtherCopies">
/// Other copies of the client's command found after the one that runs (terminal PATH order), with their versions.
/// A newer one means an earlier install or update went where the terminal does not look first.
/// </param>
/// <param name="NotOnPath">The running copy was found outside the user's terminal PATH (e.g. only in ~/.npm-global/bin).</param>
public sealed record ClientInstallDto(
    string Method, string? Package, string HomepageUrl, bool Installed, string? Executable, string? Version,
    string? LatestVersion, DateTimeOffset? LatestCheckedAt, string? LatestError, bool UpdateAvailable,
    string? InstallCommand, string? UpdateCommand, string? UpdateVia, string? Blocker, bool Busy,
    bool NeedsAdmin = false, bool AdminAvailable = false, string? AdminFixCommand = null,
    IReadOnlyList<ClientCopyDto>? OtherCopies = null, bool NotOnPath = false);

/// <summary>One copy of a client's command, the version it reports, and whether that is newer than the copy that runs.</summary>
public sealed record ClientCopyDto(string Path, string Version, bool Newer);

/// <summary>A running or finished install / update. <see cref="State"/>: "running", "succeeded", "failed", "cancelled".</summary>
/// <param name="Hint">
/// A known cause for the UI. Failed runs: "permission-denied" (npm -g cannot write its global directories),
/// "admin-cancelled" (the administrator prompt was dismissed), "timeout". Succeeded runs that did not take effect:
/// "shadowed" (the new version sits behind an older copy on PATH), "unchanged", "not-on-path".
/// </param>
/// <param name="Elevated">The command ran with administrator rights (its output may only arrive when it ends; it cannot be stopped).</param>
public sealed record ClientInstallJobDto(
    string Kind, string Action, string State, string Command, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> Log, string? Hint, bool Elevated = false);

/// <summary>
/// Body of POST /api/clients/{kind}/install: <c>action</c> is "install" or "update"; <c>elevated</c> runs the npm
/// command with administrator rights (system authorization prompt).
/// </summary>
public sealed record ClientInstallRequest(string? Action, bool? Elevated = null);

/// <summary>Body of POST /api/clients/check-updates: <c>force</c> skips the freshness window.</summary>
public sealed record ClientUpdateCheckRequest(bool? Force);

/// <summary>
/// Client versions, update checks and installs. Installed versions come from <c>&lt;command&gt; --version</c>
/// (VS Code's extensions.json, the app bundle for Claude Desktop), cached per executable fingerprint so listing
/// clients does not spawn processes every time. Latest versions come from the npm registry the user's npm uses
/// (only on <see cref="CheckUpdatesAsync"/>, never while listing). Installs and updates run one at a time, with
/// commands taken only from <see cref="ClientInstallCatalog"/>; output is kept in memory for the UI to poll.
/// </summary>
public sealed partial class ClientInstallService(ClientEnvironment env, IHttpClientFactory http, ILogger<ClientInstallService> logger)
    : IDisposable
{
    public const string HttpClientName = "client-updates";

    /// <summary>Latest versions older than this are re-fetched by a non-forced check.</summary>
    private static readonly TimeSpan LatestTtl = TimeSpan.FromHours(6);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan JobTimeout = TimeSpan.FromMinutes(15);
    private const int MaxLogLines = 2000;

    private readonly ConcurrentDictionary<string, (string Fingerprint, string? Version, DateTimeOffset At)> _probes = new();
    private readonly ConcurrentDictionary<string, LatestEntry> _latest = new();
    private readonly SemaphoreSlim _checkMutex = new(1, 1);
    private readonly Lock _jobLock = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _npmLock = new();
    private Job? _running;
    private (string NpmPath, string? Root, DateTimeOffset At)? _npm;

    private sealed record LatestEntry(string? Version, DateTimeOffset CheckedAt, string? Error);

    // ------------------------------------------------------------------ state

    /// <summary>Install state of every client (probes run in parallel; cached probes cost one stat each).</summary>
    public async Task<IReadOnlyDictionary<string, ClientInstallDto>> DescribeAllAsync(CancellationToken ct)
    {
        var tasks = ClientInstallCatalog.All.Select(spec => Task.Run(() => (spec.Kind, Dto: Describe(spec)), ct)).ToList();
        var result = new Dictionary<string, ClientInstallDto>(StringComparer.Ordinal);
        foreach (var (kind, dto) in await Task.WhenAll(tasks)) result[kind] = dto;
        return result;
    }

    /// <summary>Install state of one client; null for a client Astra has no install spec for.</summary>
    public ClientInstallDto? Describe(string kind) =>
        ClientInstallCatalog.Get(kind) is { } spec ? Describe(spec) : null;

    private ClientInstallDto Describe(ClientInstallSpec spec)
    {
        var (state, version) = Inspect(spec);
        var plan = ClientInstallPlanner.Plan(spec, state, env.Os);
        var latest = _latest.GetValueOrDefault(spec.Kind);
        var updateAvailable = state.Installed && version is not null && ClientVersions.IsNewer(latest?.Version, version);
        var others = OtherCopies(spec, state, version);
        // The latest version is already installed, just behind the copy that runs: updating again would change
        // nothing the terminal sees (the "updated, but still the old version" loop). The fix is PATH, not another run.
        if (updateAvailable && others.Any(c => c.Newer && !ClientVersions.IsNewer(latest!.Version, c.Version)))
            plan = plan with { Update = null, UpdateVia = null, Blocker = "shadowed" };
        bool busy;
        lock (_jobLock) busy = _running?.Kind == spec.Kind;
        // Admin rights only matter for the npm command that is on offer right now.
        var npmAction = state.Installed ? "update" : "install";
        var npmCommand = ClientInstallPlanner.NpmCommand(spec, plan, npmAction);
        var needsAdmin = npmCommand is not null && !NpmWritable(spec, state);
        return new ClientInstallDto(
            MethodName(spec.Method), spec.Package, spec.HomepageUrl, state.Installed, state.ExecutablePath, version,
            latest?.Version, latest?.CheckedAt, latest?.Error, updateAvailable,
            plan.Install?.Display, plan.Update?.Display, plan.UpdateVia, plan.Blocker, busy,
            needsAdmin, npmCommand is not null && Elevator() is not null, needsAdmin ? AdminFixCommand(spec, state) : null,
            others, NotOnTerminalPath(state));
    }

    /// <summary>
    /// <c>sudo chown -R "$(whoami)" &lt;global node_modules&gt; &lt;bin&gt;</c>: hands npm's global directories back to the
    /// user once, so later installs and updates run without administrator rights.
    /// </summary>
    private string? AdminFixCommand(ClientInstallSpec spec, ClientToolState state)
    {
        if (TargetNpmRoot(spec, state) is not { } root) return null;
        var dirs = new[] { root, NpmBinDirectory(root) }.OfType<string>().Where(Directory.Exists).Select(ClientElevation.ShellQuote);
        return $"sudo chown -R \"$(whoami)\" {string.Join(' ', dirs)}";
    }

    private static string MethodName(ClientInstallMethod method) => method switch
    {
        ClientInstallMethod.Npm => "npm",
        ClientInstallMethod.VsCodeExtension => "vscode-extension",
        _ => "manual",
    };

    /// <summary>What is on the machine for this client, plus its installed version.</summary>
    private (ClientToolState State, string? Version) Inspect(ClientInstallSpec spec)
    {
        switch (spec.Kind)
        {
            case ClientKinds.ClaudeDesktop:
                return InspectClaudeDesktop();
            case ClientKinds.Zed when InspectZedApp() is { } zed:
                return zed;
            case ClientKinds.VsCodeCopilot:
            {
                var code = env.FindTool("code", VsCodeBinDirectories());
                var extensions = Path.Combine(env.GetEnvironmentVariable("VSCODE_EXTENSIONS") is { Length: > 0 } d ? d : env.Combine(".vscode", "extensions"),
                    "extensions.json");
                // A user-installed copy wins; otherwise recent VS Code ships Copilot Chat built in.
                var extensionVersion = ClientVersions.VsCodeExtensionVersion(env.ReadTextOrNull(extensions), spec.Package!)
                                       ?? (code is null ? null : BuiltInExtensionVersion(code, "copilot", spec.Package!));
                return (new ClientToolState(extensionVersion is not null, code), extensionVersion);
            }
        }

        var npm = NpmInfo();
        var exe = Copies(spec, npm.Root).FirstOrDefault();
        var resolved = exe is null ? null : ResolveLinks(exe);
        var version = exe is null ? null : ProbeVersion(exe, resolved!);
        // Every catalogued client prints its version for --version; a command that does not is a placeholder (e.g.
        // VS Code's "copilot" shim, which says the CLI is missing, offers to install it and exits 0).
        var installed = version is not null;
        var packagePresent = spec.Package is not null && npm.Root is not null
                             && Directory.Exists(Path.Combine(npm.Root, spec.Package.Replace('/', Path.DirectorySeparatorChar)));
        return (new ClientToolState(installed, installed ? exe : null, installed ? resolved : null, npm.Path, npm.Root, packagePresent), version);
    }

    /// <summary>
    /// Every copy of the client's command, in the order a terminal finds them (login-shell PATH first, see
    /// <see cref="ClientEnvironment.ToolSearchPath"/>), plus npm's global bin directory. The first one is what runs.
    /// </summary>
    private IReadOnlyList<string> Copies(ClientInstallSpec spec, string? npmRoot)
    {
        if (spec.Executable is null) return [];
        string[] extra = NpmBinDirectory(npmRoot) is { } npmBin ? [npmBin] : [];
        var copies = env.FindAllTools(spec.Executable, extra);
        // Some Linux packages ship Zed's command as "zeditor".
        return copies.Count == 0 && spec.Kind == ClientKinds.Zed ? env.FindAllTools("zeditor") : copies;
    }

    /// <summary>
    /// Copies of the client other than the one that runs, with their versions (placeholders without a version and
    /// links to the running copy are left out). A newer copy here means an install or update went somewhere the
    /// terminal does not look first — the classic "updated, but still the old version".
    /// </summary>
    private List<ClientCopyDto> OtherCopies(ClientInstallSpec spec, ClientToolState state, string? runningVersion)
    {
        if (!state.Installed || spec.Method == ClientInstallMethod.VsCodeExtension) return [];
        var result = new List<ClientCopyDto>();
        foreach (var path in Copies(spec, state.NpmGlobalRoot).Skip(1))
        {
            var resolved = ResolveLinks(path);
            if (resolved == state.ResolvedPath) continue;
            if (ProbeVersion(path, resolved) is { } version) result.Add(new ClientCopyDto(path, version, ClientVersions.IsNewer(version, runningVersion)));
        }
        return result;
    }

    /// <summary>
    /// The running copy sits in a directory the user's terminal does not search (it was found only through npm's bin
    /// directory or Astra's extra directories). Unknown — false — when the login-shell PATH could not be read.
    /// </summary>
    private bool NotOnTerminalPath(ClientToolState state)
    {
        var shellPath = env.LoginShellPath();
        return state.ExecutablePath is { } exe && shellPath.Count > 0 && !shellPath.Contains(Path.GetDirectoryName(exe));
    }

    /// <summary>
    /// Version of an extension bundled with VS Code itself: <c>&lt;app&gt;/extensions/&lt;folder&gt;</c> next to the
    /// <c>bin</c> directory of the <c>code</c> command (macOS), or <c>&lt;install&gt;/resources/app/extensions</c> (Windows / Linux).
    /// </summary>
    private string? BuiltInExtensionVersion(string code, string folder, string extensionId)
    {
        if (Path.GetDirectoryName(Path.GetDirectoryName(ResolveLinks(code))) is not { } root) return null;
        foreach (var extensions in new[] { Path.Combine(root, "extensions"), Path.Combine(root, "resources", "app", "extensions") })
        {
            var version = ClientVersions.PackageJsonVersion(env.ReadTextOrNull(Path.Combine(extensions, folder, "package.json")), extensionId);
            if (version is not null) return version;
        }
        return null;
    }

    private (ClientToolState, string?) InspectClaudeDesktop()
    {
        if (env.Os == "osx")
        {
            foreach (var apps in env.ApplicationDirectories)
            {
                var plist = Path.Combine(apps, "Claude.app", "Contents", "Info.plist");
                if (!File.Exists(plist)) continue;
                return (new ClientToolState(true, Path.Combine(apps, "Claude.app")), ClientVersions.PlistShortVersion(env.ReadTextOrNull(plist)));
            }
        }
        else if (env.Os == "windows")
        {
            // Squirrel layout: %LOCALAPPDATA%\AnthropicClaude\app-<version>\claude.exe
            foreach (var apps in env.ApplicationDirectories)
            {
                var root = Path.Combine(apps, "AnthropicClaude");
                if (!Directory.Exists(root)) continue;
                var newest = Directory.GetDirectories(root, "app-*").Select(d => Path.GetFileName(d)[4..])
                    .Aggregate((string?)null, (best, v) => best is null || ClientVersions.IsNewer(v, best) ? v : best);
                if (newest is not null) return (new ClientToolState(true, root), newest);
            }
        }
        return (new ClientToolState(false), null);
    }

    /// <summary>The macOS app bundle of Zed (version from its Info.plist); null elsewhere, where <c>zed --version</c> is probed.</summary>
    private (ClientToolState, string?)? InspectZedApp()
    {
        if (env.Os != "osx") return null;
        foreach (var apps in env.ApplicationDirectories)
        {
            var plist = Path.Combine(apps, "Zed.app", "Contents", "Info.plist");
            if (!File.Exists(plist)) continue;
            return (new ClientToolState(true, Path.Combine(apps, "Zed.app")), ClientVersions.PlistShortVersion(env.ReadTextOrNull(plist)));
        }
        return null;
    }

    private string[] VsCodeBinDirectories() => env.Os == "osx"
        ? env.ApplicationDirectories.Select(a => Path.Combine(a, "Visual Studio Code.app", "Contents", "Resources", "app", "bin")).ToArray()
        : [];

    /// <summary>
    /// <c>&lt;command&gt; --version</c>, cached per executable path until it changes (size or mtime of the resolved
    /// file) and for at most ten minutes — a wrapper script (like a ~/.local/bin launcher) does not change when the
    /// copy it starts is updated. Null unless the command exits with 0 and prints a version (an error such as
    /// "requires node 18.0.0" must not read as the client's version).
    /// </summary>
    private string? ProbeVersion(string exe, string resolved)
    {
        var fingerprint = Fingerprint(exe, resolved);
        if (_probes.TryGetValue(exe, out var cached) && cached.Fingerprint == fingerprint
            && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.Version;
        string? version = null;
        try
        {
            var (exit, output) = RunCapture(new ToolCommand(exe, ["--version"]), ProbeTimeout);
            if (exit == 0) version = ClientVersions.Parse(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogDebug(ex, "版本探测失败：{Exe}", exe);
        }
        _probes[exe] = (fingerprint, version, DateTimeOffset.UtcNow);
        return version;
    }

    private static string Fingerprint(string exe, string resolved)
    {
        var info = new FileInfo(resolved);
        return info.Exists ? $"{exe}|{resolved}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : exe;
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return path;
        }
    }

    /// <summary>npm and its global root (<c>npm root -g</c>, cached for 10 minutes or until npm moves).</summary>
    private (string? Path, string? Root) NpmInfo()
    {
        var npm = env.FindTool("npm");
        if (npm is null) return (null, null);
        // Listing describes every client in parallel; only one of them should spawn npm on a cold cache.
        lock (_npmLock)
        {
            if (_npm is { } c && c.NpmPath == npm && DateTimeOffset.UtcNow - c.At < TimeSpan.FromMinutes(10)) return (npm, c.Root);
            string? root = null;
            try
            {
                var (exit, output) = RunCapture(new ToolCommand(npm, ["root", "-g"]), TimeSpan.FromSeconds(15));
                var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(Path.IsPathRooted);
                if (exit == 0) root = line;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                logger.LogDebug(ex, "npm root -g 失败");
            }
            _npm = (npm, root, DateTimeOffset.UtcNow);
            return (npm, root);
        }
    }

    /// <summary>Where npm puts global commands: <c>&lt;prefix&gt;/bin</c> on macOS / Linux, the prefix itself on Windows.</summary>
    private string? NpmBinDirectory(string? root)
    {
        if (root is null || Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)) is not { } parent) return null;
        if (env.Os == "windows") return parent; // <prefix>\node_modules
        return Path.GetDirectoryName(parent) is { } prefix ? Path.Combine(prefix, "bin") : null; // <prefix>/lib/node_modules
    }

    // ------------------------------------------------------------------ administrator rights

    /// <summary>
    /// Whether the current user can write every directory the npm command touches (see
    /// <see cref="ClientElevation.NpmWriteTargets"/>). Directories that do not exist yet are skipped (npm creates
    /// them inside a parent that is checked). Results are cached for 30 seconds — listing runs this per client.
    /// </summary>
    private bool NpmWritable(ClientInstallSpec spec, ClientToolState state)
    {
        if (env.Os == "windows" || TargetNpmRoot(spec, state) is not { } root) return true;
        return ClientElevation.NpmWriteTargets(spec, root, NpmBinDirectory(root)).Where(Directory.Exists).All(DirectoryWritable);
    }

    /// <summary>
    /// The global node_modules the npm command writes: for an update of a copy installed under another prefix
    /// (see <see cref="ClientInstallPlanner.InstalledNpmPrefix"/>) that prefix's, otherwise npm's current one.
    /// </summary>
    private string? TargetNpmRoot(ClientInstallSpec spec, ClientToolState state) =>
        state.Installed && !ClientInstallPlanner.IsNpmManaged(spec, state, env.Os)
                        && ClientInstallPlanner.InstalledNpmPrefix(spec, state, env.Os) is { } prefix
            ? Path.Combine(prefix, "lib", "node_modules")
            : state.NpmGlobalRoot;

    private readonly ConcurrentDictionary<string, (bool Writable, DateTimeOffset At)> _writable = new();

    /// <summary>Tries to create (and immediately delete) a file: the only check that also honours ACLs and read-only mounts.</summary>
    private bool DirectoryWritable(string dir)
    {
        if (_writable.TryGetValue(dir, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(30)) return cached.Writable;
        bool writable;
        try
        {
            using (new FileStream(Path.Combine(dir, $".astra-write-test-{Guid.NewGuid():N}"), FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            writable = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            writable = false;
        }
        _writable[dir] = (writable, DateTimeOffset.UtcNow);
        return writable;
    }

    /// <summary>osascript (macOS) or pkexec (Linux) — whatever shows the system's administrator prompt; null when neither is usable.</summary>
    private string? Elevator() => env.Os switch
    {
        "osx" => ClientElevation.MacOsascript(env.Os),
        "linux" => env.FindTool("pkexec", "/usr/bin"),
        _ => null,
    };

    /// <summary>The npm command run as root, with the environment npm needs spelled out (the elevated shell starts empty).</summary>
    private ToolCommand? Elevate(ToolCommand npm, ClientToolState state, string kind)
    {
        var environment = new List<KeyValuePair<string, string>>
        {
            new("PATH", string.Join(Path.PathSeparator, env.ToolSearchPath(ExtraPathFor(state)).Concat(["/usr/bin", "/bin", "/usr/sbin", "/sbin"]).Distinct())),
            new("NO_COLOR", "1"),
            new("npm_config_fund", "false"),
            new("npm_config_audit", "false"),
            new("npm_config_update_notifier", "false"),
            new("npm_config_progress", "false"),
            // Root's npm must not write into ~/.npm (it would leave root-owned files the user's npm then trips over),
            // and it would not read the user's ~/.npmrc, so the registry is passed along.
            new("npm_config_cache", ClientElevation.RootNpmCache),
            new("npm_config_registry", NpmRegistry()),
        };
        // The root shell starts without the user's environment: pass the proxy explicitly — the inherited
        // variables when the server has them, otherwise the system proxy (or the one chosen in settings).
        if (SystemProxy.Mode == SystemProxy.ModeSystem)
            environment.AddRange(ProxyVariables.Select(v => (v, env.GetEnvironmentVariable(v)))
                .Where(p => p.Item2 is { Length: > 0 }).Select(p => new KeyValuePair<string, string>(p.v, p.Item2!)));
        environment.AddRange(ProxyEnvironment());
        var name = ClientInstallCatalog.Get(kind) is { } spec ? spec.Executable ?? kind : kind;
        return ClientElevation.Wrap(npm, env.Os, Elevator(), environment, $"Astra 需要管理员权限来安装或更新 {name}（npm 全局目录属于系统管理员）。");
    }

    // ------------------------------------------------------------------ update check

    /// <summary>
    /// Fetches the latest version of every npm-installed client from the registry npm itself uses. Without
    /// <paramref name="force"/>, versions fetched within the last six hours are kept. Failures are recorded per client.
    /// </summary>
    public async Task CheckUpdatesAsync(bool force, CancellationToken ct)
    {
        await _checkMutex.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var registry = NpmRegistry();
            var due = ClientInstallCatalog.All
                .Where(s => s.Method == ClientInstallMethod.Npm)
                .Where(s => force || !_latest.TryGetValue(s.Kind, out var e) || now - e.CheckedAt > LatestTtl)
                .ToList();
            await Task.WhenAll(due.Select(async spec =>
            {
                try
                {
                    _latest[spec.Kind] = new LatestEntry(await FetchLatestAsync(registry, spec.Package!, ct), DateTimeOffset.UtcNow, null);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
                {
                    if (ct.IsCancellationRequested) throw;
                    logger.LogWarning("客户端更新检查失败（{Package}）：{Error}", spec.Package, ex.Message);
                    var previous = _latest.GetValueOrDefault(spec.Kind)?.Version;
                    _latest[spec.Kind] = new LatestEntry(previous, DateTimeOffset.UtcNow, ex.Message);
                }
            }));
        }
        finally
        {
            _checkMutex.Release();
        }
    }

    private async Task<string> FetchLatestAsync(string registry, string package, CancellationToken ct)
    {
        // Scoped names keep their "@" and encode the slash ("@openai%2fcodex"), which every registry accepts.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{registry.TrimEnd('/')}/{package.Replace("/", "%2f")}/latest");
        req.Headers.UserAgent.ParseAdd($"Astra/{ServerOptions.Version}");
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await http.CreateClient(HttpClientName).SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        return (doc?["version"] as JsonValue)?.TryGetValue(out string? v) == true && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new InvalidOperationException("registry response has no version");
    }

    /// <summary>
    /// The registry npm would install from: <c>npm_config_registry</c>, else the <c>registry=</c> line of
    /// <c>~/.npmrc</c>, else the public registry. Only that one line is read — .npmrc may hold auth tokens.
    /// </summary>
    private string NpmRegistry()
    {
        foreach (var name in new[] { "npm_config_registry", "NPM_CONFIG_REGISTRY" })
            if (env.GetEnvironmentVariable(name) is { Length: > 0 } fromEnv && IsHttpUrl(fromEnv)) return fromEnv;
        var npmrc = env.ReadTextOrNull(env.Combine(".npmrc"));
        if (npmrc is not null && RegistryLine().Match(npmrc) is { Success: true } m && IsHttpUrl(m.Groups[1].Value)) return m.Groups[1].Value;
        return "https://registry.npmjs.org";
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    [GeneratedRegex(@"^\s*registry\s*=\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex RegistryLine();

    // ------------------------------------------------------------------ install / update jobs

    /// <summary>
    /// Starts an install or update. Only one runs at a time; the command comes from the planner. With
    /// <paramref name="elevated"/> the npm command runs with administrator rights (system prompt); other commands
    /// are never elevated.
    /// </summary>
    public ClientInstallJobDto Start(string kind, string? action, bool elevated = false)
    {
        var spec = ClientInstallCatalog.Get(kind) ?? (ClientKinds.All.Contains(kind)
            ? throw new AdminApiException(409, "该客户端暂不支持在 Astra 中安装")
            : throw new AdminApiException(404, "Unknown client kind"));
        if (action is not ("install" or "update")) throw new AdminApiException(400, "action must be \"install\" or \"update\"");
        if (spec.Method == ClientInstallMethod.Manual)
            throw new AdminApiException(409, $"该客户端需要手动安装：{spec.HomepageUrl}");
        var (state, previousVersion) = Inspect(spec);
        var plan = ClientInstallPlanner.Plan(spec, state, env.Os);
        var command = action == "install" ? plan.Install : plan.Update;
        if (command is null)
        {
            throw new AdminApiException(409, plan.Blocker switch
            {
                "npm-missing" => "没有找到 npm，请先安装 Node.js",
                "vscode-missing" => "没有找到 VS Code 的 code 命令",
                "external-install" => $"{state.ExecutablePath} 不是通过 npm 安装的，请用原来的方式更新",
                _ => action == "install" ? "客户端已经安装" : "客户端尚未安装",
            });
        }
        if (elevated)
        {
            if (ClientInstallPlanner.NpmCommand(spec, plan, action) is null)
                throw new AdminApiException(409, "只有 npm 安装和更新可以使用管理员权限运行");
            command = Elevate(command, state, kind)
                      ?? throw new AdminApiException(409, $"这里无法弹出管理员授权，请在终端运行：sudo {command.Display}");
        }

        Job job;
        lock (_jobLock)
        {
            if (_running is { } busy)
                throw new AdminApiException(409, $"正在{(busy.Action == "install" ? "安装" : "更新")} {busy.Kind}，请等它完成");
            job = new Job(kind, action, command, elevated);
            _jobs[kind] = job;
            _running = job;
        }
        _ = Task.Run(() => RunJobAsync(job, state, spec, previousVersion));
        return job.ToDto();
    }

    /// <summary>The current or last job of a client; 404 when there was none since the server started.</summary>
    public ClientInstallJobDto Get(string kind)
    {
        lock (_jobLock)
            return _jobs.TryGetValue(kind, out var job) ? job.ToDto() : throw new AdminApiException(404, "No install job");
    }

    public ClientInstallJobDto Cancel(string kind)
    {
        Job job;
        lock (_jobLock)
            job = _jobs.TryGetValue(kind, out var j) ? j : throw new AdminApiException(404, "No install job");
        // The elevated command runs as root under osascript / pkexec; killing our side would not stop it.
        if (job.Elevated && job.State == "running") throw new AdminApiException(409, "以管理员权限运行的命令无法中途停止，请等它结束");
        job.Cancel();
        return job.ToDto();
    }

    private async Task RunJobAsync(Job job, ClientToolState state, ClientInstallSpec spec, string? previousVersion)
    {
        job.Append($"$ {job.Command.Display}");
        if (job.Elevated)
        {
            job.Append(env.Os == "osx"
                ? "正在请求管理员权限，请在系统弹出的授权框中输入密码。以管理员身份运行时，输出会在命令结束后一次显示。"
                : "正在请求管理员权限，请在系统弹出的授权框中确认。");
        }
        try
        {
            using var process = CreateProcess(job.Command, ExtraPathFor(state));
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) job.Append(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) job.Append(e.Data); };
            process.Start();
            process.StandardInput.Close(); // installers must never wait on a prompt
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            job.Attach(process);
            using var timeout = new CancellationTokenSource(JobTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                job.Kill();
                job.Finish("failed", null, "timeout");
                return;
            }
            process.WaitForExit(); // flush the async output readers
            var exit = process.ExitCode;
            if (job.Cancelled) job.Finish("cancelled", exit, null);
            else if (exit == 0) job.Finish("succeeded", exit, VerifyOutcome(job, spec, previousVersion));
            else job.Finish("failed", exit, FailureHint(job, exit));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            job.Append(ex.Message);
            job.Finish("failed", null, null);
        }
        finally
        {
            _probes.Clear();
            _npm = null;
            _writable.Clear();
            lock (_jobLock) if (_running == job) _running = null;
            logger.LogInformation("客户端{Action}结束：{Kind} {State}", job.Action, job.Kind, job.State);
        }
    }

    /// <summary>
    /// Known causes of a failed run: the administrator prompt was dismissed (osascript error -128; pkexec exits 126),
    /// or npm could not write its global directories.
    /// </summary>
    private string? FailureHint(Job job, int exit)
    {
        if (job.Elevated && ((env.Os == "osx" && job.LogContains("(-128)")) || (env.Os == "linux" && exit == 126))) return "admin-cancelled";
        return job.LogContains("EACCES") || job.LogContains("EPERM") ? "permission-denied" : null;
    }

    /// <summary>
    /// After a run that exited 0, checks what the user will actually run — a command can succeed and change nothing
    /// visible (e.g. <c>claude update</c> installs into ~/.npm-global while ~/.local/bin/claude keeps launching the
    /// old copy). Explains in the log and returns a hint: "shadowed" (a newer copy exists behind the one that runs),
    /// "unchanged" (an update left the version as it was), "not-on-path" (installed, but the terminal's PATH does not
    /// include its directory); null when the result is what was asked for. Only for clients with a command.
    /// </summary>
    private string? VerifyOutcome(Job job, ClientInstallSpec spec, string? previousVersion)
    {
        if (spec.Executable is not { } name || spec.Method == ClientInstallMethod.VsCodeExtension) return null;
        _probes.Clear();
        _npm = null;
        env.RefreshLoginShellPath();
        var (state, version) = Inspect(spec);
        var newer = OtherCopies(spec, state, version).Where(c => c.Newer).MaxBy(c => c.Version, VersionComparer);
        if (!state.Installed)
        {
            job.Append("");
            job.Append($"注意：命令执行成功，但没有找到可运行的 {name}。");
            return "not-on-path";
        }
        if (job.Action == "update" && !ClientVersions.IsNewer(version, previousVersion))
        {
            job.Append("");
            if (newer is not null)
            {
                job.Append($"注意：命令执行成功，但终端里运行的 {name} 仍是 {state.ExecutablePath}（{version}）。");
                job.Append($"新版本 {newer.Version} 装在了 {newer.Path}，它在 PATH 中排在后面，或者不在 PATH 里。");
                job.Append($"把 {Path.GetDirectoryName(newer.Path)} 放到 PATH 最前面，或删掉旧的 {state.ExecutablePath}，就会用上新版本。");
                return "shadowed";
            }
            job.Append($"注意：命令执行成功，但 {name} 的版本仍是 {version}。");
            return "unchanged";
        }
        if (NotOnTerminalPath(state))
        {
            var dir = Path.GetDirectoryName(state.ExecutablePath!);
            job.Append("");
            job.Append($"注意：{name} 装在了 {dir}，但终端的 PATH 里没有这个目录，需要把它加入 PATH 才能在终端直接运行 {name}。");
            return "not-on-path";
        }
        return null;
    }

    private static readonly Comparer<string?> VersionComparer =
        Comparer<string?>.Create((a, b) => ClientVersions.IsNewer(a, b) ? 1 : ClientVersions.IsNewer(b, a) ? -1 : 0);

    /// <summary>npm's own directory (node usually sits next to it) goes on the child's PATH, so npm finds node.</summary>
    private static string[] ExtraPathFor(ClientToolState state) =>
        state.NpmPath is { } npm && Path.GetDirectoryName(npm) is { } dir ? [dir] : [];

    // ------------------------------------------------------------------ processes

    private Process CreateProcess(ToolCommand command, string[] extraPath)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Directory.Exists(env.HomeDirectory)) info.WorkingDirectory = env.HomeDirectory;
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        info.Environment["PATH"] = string.Join(Path.PathSeparator, env.ToolSearchPath(extraPath));
        info.Environment["NO_COLOR"] = "1";
        info.Environment["npm_config_fund"] = "false";
        info.Environment["npm_config_audit"] = "false";
        info.Environment["npm_config_update_notifier"] = "false";
        info.Environment["npm_config_progress"] = "false";
        foreach (var (key, value) in ProxyEnvironment()) info.Environment[key] = value;
        return new Process { StartInfo = info };
    }

    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    /// <summary>
    /// The system proxy as environment variables for child processes. npm and the clients' own updaters (Node)
    /// only read HTTPS_PROXY / HTTP_PROXY, never the macOS / Windows proxy settings, and a server started by the
    /// desktop app has none of those variables — without this, "claude update" and npm installs cannot reach the
    /// registry behind a proxy even though Astra's own update check (which uses <see cref="SystemProxy"/>) can.
    /// Nothing is added when the server already has proxy variables (the child inherits them) or there is no proxy.
    /// A proxy chosen in settings overrides inherited variables: "custom" sets them, "direct" blanks them.
    /// </summary>
    private List<KeyValuePair<string, string>> ProxyEnvironment()
    {
        if (SystemProxy.Mode == SystemProxy.ModeDirect)
            return [.. ProxyVariables.Select(v => new KeyValuePair<string, string>(v, ""))];
        if (SystemProxy.CustomForChildren is var (url, bypass))
        {
            var noProxy = string.Join(',', new[] { "localhost", "127.0.0.1", "::1" }
                .Concat((bypass ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            return [.. ProxyVariables.Select(v => new KeyValuePair<string, string>(v, url)), new("NO_PROXY", noProxy), new("no_proxy", noProxy)];
        }
        if (ProxyVariables.Any(v => env.GetEnvironmentVariable(v) is { Length: > 0 })) return [];
        if (SystemProxy.ProxyUrlFor(new Uri(NpmRegistry())) is not { } proxy) return [];
        var result = ProxyVariables.Take(4).Select(v => new KeyValuePair<string, string>(v, proxy)).ToList();
        if (env.GetEnvironmentVariable("NO_PROXY") is not { Length: > 0 } && env.GetEnvironmentVariable("no_proxy") is not { Length: > 0 })
        {
            result.Add(new("NO_PROXY", "localhost,127.0.0.1,::1"));
            result.Add(new("no_proxy", "localhost,127.0.0.1,::1"));
        }
        return result;
    }

    /// <summary>Runs a short read-only command (version probe, npm root) and returns its exit code and combined output.</summary>
    private (int Exit, string Output) RunCapture(ToolCommand command, TimeSpan timeout)
    {
        using var process = CreateProcess(command, []);
        process.Start();
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return (-1, "");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        lock (_jobLock) _running?.Kill();
        _checkMutex.Dispose();
    }

    /// <summary>One install / update run; the log is bounded and every member is thread-safe.</summary>
    private sealed partial class Job(string kind, string action, ToolCommand command, bool elevated)
    {
        private readonly Lock _lock = new();
        private readonly List<string> _log = [];
        private Process? _process;

        public string Kind { get; } = kind;
        public string Action { get; } = action;
        public ToolCommand Command { get; } = command;
        public bool Elevated { get; } = elevated;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public string State { get; private set; } = "running";
        public bool Cancelled { get; private set; }
        private DateTimeOffset? _finishedAt;
        private int? _exitCode;
        private string? _hint;

        [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
        private static partial Regex Ansi();

        public void Append(string line)
        {
            var clean = Ansi().Replace(line, "").TrimEnd('\r');
            lock (_lock)
            {
                _log.Add(clean);
                if (_log.Count > MaxLogLines) _log.RemoveRange(0, _log.Count - MaxLogLines);
            }
        }

        public bool LogContains(string text)
        {
            lock (_lock) return _log.Any(l => l.Contains(text, StringComparison.Ordinal));
        }

        public void Attach(Process process)
        {
            lock (_lock) _process = process;
        }

        public void Cancel()
        {
            lock (_lock)
            {
                if (State != "running") return;
                Cancelled = true;
            }
            Kill();
        }

        public void Kill()
        {
            Process? process;
            lock (_lock) process = _process;
            try { process?.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Finish(string state, int? exitCode, string? hint)
        {
            lock (_lock)
            {
                State = state;
                _exitCode = exitCode;
                _hint = hint;
                _finishedAt = DateTimeOffset.UtcNow;
                _process = null;
            }
        }

        public ClientInstallJobDto ToDto()
        {
            lock (_lock)
                return new ClientInstallJobDto(Kind, Action, State, Command.Display, StartedAt, _finishedAt, _exitCode, _log.ToList(), _hint, Elevated);
        }
    }
}
