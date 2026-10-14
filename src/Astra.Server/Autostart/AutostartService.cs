using System.Text;
using System.Text.RegularExpressions;
using Astra.Core;

namespace Astra.Server.Autostart;

/// <summary>An autostart entry could not be written or removed (a command failed); the message says which.</summary>
public sealed class AutostartException(string message) : Exception(message);

/// <param name="Supported">Whether this machine has a login entry Astra can manage.</param>
/// <param name="Enabled">An entry starts the server at login (written here or by <c>astra autostart enable</c>).</param>
/// <param name="Mechanism">launchd | systemd | registry | none.</param>
/// <param name="Unsupported">Why it is not supported (container | no-systemd | platform); null when it is.</param>
/// <param name="Location">Where the entry lives: the plist, the unit file, the Run key value or the CLI's old scheduled task.</param>
/// <param name="Program">The program the entry starts, when it can be read back.</param>
public sealed record AutostartState(bool Supported, bool Enabled, string Mechanism, string? Unsupported, string? Location, string? Program);

/// <summary>
/// Starting the server when the user logs in — the same entries <c>astra autostart</c> writes (cli/src/lib/autostart.ts),
/// so the CLI and the settings page see and change one switch: a LaunchAgent on macOS, a systemd user unit on Linux and
/// a value under the per-user Run key on Windows, each running <c>&lt;this server&gt; serve --started-by autostart</c>.
/// Unlike the CLI this never starts or stops anything now (no <c>launchctl load</c>, no <c>--now</c>): the server is
/// already running, and when it is the one the entry started, unloading the entry would stop it mid-request.
/// At startup an entry left pointing at an older managed binary (~/.astra/server, which the updater prunes) is moved to
/// the running one, so an update does not leave the next login starting the previous version.
/// </summary>
public sealed partial class AutostartService(AutostartHost host, AstraPaths paths, ILogger<AutostartService> log) : IHostedService
{
    public const string LaunchdLabel = "io.astra.server";
    public const string SystemdUnitName = "astra.service";
    public const string RunValueName = "Astra Server";
    /// <summary>What <c>astra autostart</c> created on Windows before it moved to the Run key (a logon task needs an elevated prompt).</summary>
    public const string LegacyTaskName = "Astra Server";
    public const string RunKeyPath = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

    public const string MechanismLaunchd = "launchd";
    public const string MechanismSystemd = "systemd";
    public const string MechanismRegistry = "registry";
    public const string MechanismNone = "none";

    public const string UnsupportedContainer = "container";
    public const string UnsupportedNoSystemd = "no-systemd";
    public const string UnsupportedPlatform = "platform";

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LaunchdPlistPath => Path.Combine(host.UserHome, "Library", "LaunchAgents", $"{LaunchdLabel}.plist");
    public string SystemdUnitPath => Path.Combine(host.UserHome, ".config", "systemd", "user", SystemdUnitName);

    /// <summary>Why this machine has no login entry Astra can manage; null when it has one.</summary>
    public string? Unsupported =>
        host.InContainer ? UnsupportedContainer
        : host.Launch.Count == 0 ? UnsupportedPlatform
        : host.Platform switch
        {
            "darwin" => null,
            "linux" => host.HasSystemd ? null : UnsupportedNoSystemd,
            "windows" => host.RunKey is null ? UnsupportedPlatform : null,
            _ => UnsupportedPlatform,
        };

    public string Mechanism => Unsupported is not null ? MechanismNone : host.Platform switch
    {
        "darwin" => MechanismLaunchd,
        "linux" => MechanismSystemd,
        _ => MechanismRegistry,
    };

    /// <summary>The full command line of the entry: this server, then <c>serve --started-by autostart</c>.</summary>
    public IReadOnlyList<string> Argv => [.. host.Launch, "serve", "--started-by", "autostart"];

    public async Task<AutostartState> StatusAsync(CancellationToken ct = default)
    {
        if (Unsupported is { } why) return new AutostartState(false, false, MechanismNone, why, null, null);
        switch (Mechanism)
        {
            case MechanismLaunchd:
            {
                var plist = LaunchdPlistPath;
                if (!File.Exists(plist)) return Off(MechanismLaunchd, plist);
                return new AutostartState(true, true, MechanismLaunchd, null, plist, PlistProgram(await File.ReadAllTextAsync(plist, ct)));
            }
            case MechanismSystemd:
            {
                var unit = SystemdUnitPath;
                if (!File.Exists(unit)) return Off(MechanismSystemd, unit);
                var r = await host.Run("systemctl", ["--user", "is-enabled", SystemdUnitName], ct);
                if (r.ExitCode != 0 || r.Stdout.Trim() != "enabled") return Off(MechanismSystemd, unit);
                return new AutostartState(true, true, MechanismSystemd, null, unit, UnitProgram(await File.ReadAllTextAsync(unit, ct)));
            }
            default:
            {
                var location = $@"{RunKeyPath}\{RunValueName}";
                if (host.RunKey!.Get(RunValueName) is { } value)
                    return new AutostartState(true, true, MechanismRegistry, null, location, FirstWindowsArg(value));
                if (await LegacyTaskExistsAsync(ct))
                    return new AutostartState(true, true, MechanismRegistry, null, $@"Task Scheduler\{LegacyTaskName}", null);
                return Off(MechanismRegistry, location);
            }
        }
    }

    /// <summary>Adds (pointing at this server) or removes the login entry. Throws <see cref="AutostartException"/> when a command fails.</summary>
    public async Task<AutostartState> SetAsync(bool enabled, CancellationToken ct = default)
    {
        if (Unsupported is { } why) throw new AutostartException($"Autostart is not supported here ({why})");
        await _gate.WaitAsync(ct);
        try
        {
            if (enabled) await WriteAsync(ct);
            else await RemoveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
        return await StatusAsync(ct);
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not check the autostart entry");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Points an existing entry at this server when this server is a managed binary and the entry starts another one that
    /// is managed too or gone — what an update leaves behind. Entries for a dev build or a package install are left alone.
    /// </summary>
    internal async Task<bool> RefreshAsync(CancellationToken ct)
    {
        if (Unsupported is not null) return false;
        var state = await StatusAsync(ct);
        if (!state.Enabled || state.Program is not { } program) return false;
        var current = host.Launch[0];
        if (SamePath(program, current) || !IsManaged(current)) return false;
        if (File.Exists(program) && !IsManaged(program)) return false;
        await SetAsync(true, ct);
        log.LogInformation("Autostart now starts {Current} (was {Previous})", current, program);
        return true;
    }

    private async Task WriteAsync(CancellationToken ct)
    {
        switch (Mechanism)
        {
            case MechanismLaunchd:
                // launchd loads ~/Library/LaunchAgents at the next login; loading it now would start a second server.
                Directory.CreateDirectory(Path.GetDirectoryName(LaunchdPlistPath)!);
                await File.WriteAllTextAsync(LaunchdPlistPath, RenderLaunchdPlist(Argv, Path.Combine(paths.LogsDir, "server-stdout.log")), ct);
                break;
            case MechanismSystemd:
            {
                var unit = SystemdUnitPath;
                var existed = File.Exists(unit);
                Directory.CreateDirectory(Path.GetDirectoryName(unit)!);
                await File.WriteAllTextAsync(unit, RenderSystemdUnit(Argv), ct);
                var failure = await SystemctlAsync(["--user", "daemon-reload"], ct) ?? await SystemctlAsync(["--user", "enable", SystemdUnitName], ct);
                if (failure is not null)
                {
                    if (!existed) File.Delete(unit);
                    throw new AutostartException(failure);
                }
                break;
            }
            default:
                host.RunKey!.Set(RunValueName, WindowsCommandLine(Argv));
                // The CLI's old logon task would start a second server next to the Run key's.
                await DeleteLegacyTaskAsync(ct);
                break;
        }
    }

    private async Task RemoveAsync(CancellationToken ct)
    {
        switch (Mechanism)
        {
            case MechanismLaunchd:
                // Not unloaded: when this server is the one launchd started, that would stop it. Gone at the next login.
                File.Delete(LaunchdPlistPath);
                break;
            case MechanismSystemd:
                // `disable` without --now for the same reason; a missing unit is fine.
                await host.Run("systemctl", ["--user", "disable", SystemdUnitName], ct);
                File.Delete(SystemdUnitPath);
                await host.Run("systemctl", ["--user", "daemon-reload"], ct);
                break;
            default:
                host.RunKey!.Delete(RunValueName);
                if (!await DeleteLegacyTaskAsync(ct))
                    throw new AutostartException($"The scheduled task \"{LegacyTaskName}\" could not be removed; delete it from an administrator prompt: schtasks /Delete /F /TN \"{LegacyTaskName}\"");
                break;
        }
    }

    /// <summary>Runs systemctl; null when it succeeded, else what failed.</summary>
    private async Task<string?> SystemctlAsync(string[] args, CancellationToken ct)
    {
        var r = await host.Run("systemctl", args, ct);
        if (r.ExitCode == 0) return null;
        var output = r.Stderr.Trim() is { Length: > 0 } err ? err : r.Stdout.Trim();
        return $"systemctl {string.Join(' ', args)} failed{(output.Length > 0 ? $": {output}" : "")}";
    }

    private async Task<bool> LegacyTaskExistsAsync(CancellationToken ct) =>
        (await host.Run("schtasks", ["/Query", "/TN", LegacyTaskName], ct)).ExitCode == 0;

    /// <summary>Removes the CLI's old logon task when there is one; false when it is still there afterwards.</summary>
    private async Task<bool> DeleteLegacyTaskAsync(CancellationToken ct)
    {
        if (!await LegacyTaskExistsAsync(ct)) return true;
        await host.Run("schtasks", ["/Delete", "/F", "/TN", LegacyTaskName], ct);
        return !await LegacyTaskExistsAsync(ct);
    }

    private static AutostartState Off(string mechanism, string location) => new(true, false, mechanism, null, location, null);

    private bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        host.Platform == "linux" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>A binary the updater installed: ~/.astra/server/astra-server-&lt;version&gt; (packages/update-core/src/paths.ts).</summary>
    private bool IsManaged(string program) =>
        Path.GetFileName(program).StartsWith("astra-server-", StringComparison.OrdinalIgnoreCase)
        && Path.GetDirectoryName(Path.GetFullPath(program)) is { } dir && SamePath(dir, Path.Combine(paths.Root, "server"));

    // ---------- entry formats (kept identical to cli/src/lib/autostart.ts) ----------

    internal static string RenderLaunchdPlist(IReadOnlyList<string> argv, string logFile)
    {
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>

            """);
        sb.Append($"  <string>{XmlEscape(LaunchdLabel)}</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var a in argv) sb.Append($"    <string>{XmlEscape(a)}</string>\n");
        sb.Append("""
              </array>
              <key>RunAtLoad</key>
              <true/>
              <key>KeepAlive</key>
              <false/>
              <key>ProcessType</key>
              <string>Background</string>
              <key>StandardOutPath</key>

            """);
        sb.Append($"  <string>{XmlEscape(logFile)}</string>\n");
        sb.Append("  <key>StandardErrorPath</key>\n");
        sb.Append($"  <string>{XmlEscape(logFile)}</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    internal static string RenderSystemdUnit(IReadOnlyList<string> argv) => $"""
        [Unit]
        Description=Astra local AI gateway server
        After=network.target

        [Service]
        Type=simple
        ExecStart={string.Join(' ', argv.Select(SystemdQuote))}
        Restart=on-failure
        RestartSec=3

        [Install]
        WantedBy=default.target

        """;

    /// <summary>The program, always quoted, then the arguments, quoted when they contain spaces (CommandLineToArgvW rules).</summary>
    internal static string WindowsCommandLine(IReadOnlyList<string> argv) =>
        string.Join(' ', argv.Select((a, i) => i == 0 || a.Length == 0 || a.Any(c => c is ' ' or '\t' or '"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));

    private static string SystemdQuote(string part) =>
        part.Any(c => char.IsWhiteSpace(c) || c == '"') ? $"\"{part.Replace("\"", "\\\"")}\"" : part;

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string XmlUnescape(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

    internal static string? PlistProgram(string plist) =>
        PlistProgramRegex().Match(plist) is { Success: true } m ? XmlUnescape(m.Groups[1].Value) : null;

    internal static string? UnitProgram(string unit)
    {
        var line = unit.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal));
        return line is null ? null : FirstArg(line["ExecStart=".Length..], unescapeQuotes: true);
    }

    internal static string? FirstWindowsArg(string commandLine) => FirstArg(commandLine, unescapeQuotes: false);

    private static string? FirstArg(string commandLine, bool unescapeQuotes)
    {
        var s = commandLine.TrimStart();
        if (s.Length == 0) return null;
        if (s[0] != '"')
        {
            var end = s.IndexOfAny([' ', '\t']);
            return end < 0 ? s : s[..end];
        }
        var sb = new StringBuilder();
        for (var i = 1; i < s.Length; i++)
        {
            if (unescapeQuotes && s[i] == '\\' && i + 1 < s.Length && s[i + 1] == '"')
            {
                sb.Append('"');
                i++;
            }
            else if (s[i] == '"') return sb.ToString();
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"<key>ProgramArguments</key>\s*<array>\s*<string>([^<]*)</string>")]
    private static partial Regex PlistProgramRegex();
}
