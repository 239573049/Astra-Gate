using System.Text.RegularExpressions;
using System.Runtime.Versioning;

namespace Astra.Desktop.Core.Shell;

/// <summary>The OS a login item is written for.</summary>
public enum LoginOs { MacOS, Windows, Linux }

/// <summary>The Windows Run key (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>), as a seam for tests.</summary>
public interface IRunKey
{
    /// <summary>The command stored under <paramref name="name"/>, or null.</summary>
    string? Get(string name);

    /// <summary>Stores <paramref name="command"/> under <paramref name="name"/>.</summary>
    void Set(string name, string command);

    /// <summary>Removes the value (no-op when absent).</summary>
    void Remove(string name);
}

/// <summary>The real Run key of the current user.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <inheritdoc />
    public string? Get(string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    /// <inheritdoc />
    public void Set(string name, string command)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue(name, command, Microsoft.Win32.RegistryValueKind.String);
    }

    /// <inheritdoc />
    public void Remove(string name)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>What the login item needs to know about this run.</summary>
/// <param name="Os">The OS.</param>
/// <param name="Packaged">False for a dev run from a checkout: a login item needs the stable executable of an installed app (Electron: <c>app.isPackaged</c>).</param>
/// <param name="ExecPath">The running executable.</param>
/// <param name="Home">The user's home directory.</param>
/// <param name="Env">Environment lookup (<c>APPIMAGE</c>, <c>XDG_CONFIG_HOME</c>).</param>
/// <param name="RunKey">Windows only.</param>
public sealed record LoginItemEnvironment(LoginOs Os, bool Packaged, string ExecPath, string Home, Func<string, string?> Env, IRunKey? RunKey = null);

/// <summary>
/// Launch at login with the same entries as the Electron app (desktop/src/shared/loginItem.ts, desktop/src/main.ts), so switching from
/// the Electron client to this one finds the setting as the user left it: Windows the Run key value <c>Astra</c> =
/// <c>"&lt;exe&gt;" --hidden</c>, Linux the XDG autostart entry <c>io.astra.desktop.desktop</c>. macOS: Electron registers the app with
/// SMAppService (not a plist), which needs the Objective-C runtime and a signed bundle; it is not implemented yet and reports
/// <c>Supported = false</c>, so Settings shows the switch disabled (plan avalonia-client §24).
/// </summary>
public sealed class LoginItemService(LoginItemEnvironment env)
{
    /// <summary>Passed by the login item: start in the tray without opening the window (loginItem.ts <c>HIDDEN_ARG</c>).</summary>
    public const string HiddenArg = "--hidden";

    /// <summary>The Run-key value name: Electron uses the product name.</summary>
    public const string RunValueName = "Astra";

    private bool Supported => env.Packaged && env.Os is LoginOs.Windows or LoginOs.Linux && (env.Os != LoginOs.Windows || env.RunKey is not null);

    /// <summary>Whether this run was started by the login item (<see cref="HiddenArg"/>).</summary>
    public static bool LaunchedAtLogin(IEnumerable<string> args) => args.Contains(HiddenArg, StringComparer.Ordinal);

    /// <summary>The current state; unsupported when the OS or run cannot have one.</summary>
    public LoginItemState Get()
    {
        if (!Supported) return new LoginItemState(false, false);
        try
        {
            return new LoginItemState(true, env.Os == LoginOs.Linux ? LinuxEnabled() : WindowsEnabled());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new LoginItemState(false, false);
        }
    }

    /// <summary>Switches the entry; returns the state afterwards (unchanged when the system refused).</summary>
    public LoginItemState Set(bool enabled)
    {
        if (!Supported) return Get();
        try
        {
            if (env.Os == LoginOs.Linux) SetLinux(enabled);
            else SetWindows(enabled);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // A read-only config dir or a locked registry: the state below reports it unchanged.
        }
        return Get();
    }

    /// <summary>Linux: an AppImage moved or replaced since the entry was written would no longer start; point the entry at this one (<c>refreshLinuxLoginItem</c>).</summary>
    public void RefreshLinuxEntry()
    {
        if (env.Os != LoginOs.Linux || !Supported) return;
        try
        {
            var file = LinuxAutostartPath(env.Env, env.Home);
            if (!File.Exists(file)) return;
            var next = RetargetLinuxAutostart(File.ReadAllText(file), LinuxArgv());
            if (next is not null) File.WriteAllText(file, next);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort, like the Electron app: it only logs.
        }
    }

    // ---------------------------------------------------------------- Windows

    /// <summary>The Run value's data: the quoted executable and the hidden argument, as Electron writes it.</summary>
    public static string WindowsCommand(string execPath) => $"\"{execPath}\" {HiddenArg}";

    private bool WindowsEnabled()
        => string.Equals(env.RunKey!.Get(RunValueName), WindowsCommand(env.ExecPath), StringComparison.OrdinalIgnoreCase);

    private void SetWindows(bool enabled)
    {
        if (enabled) env.RunKey!.Set(RunValueName, WindowsCommand(env.ExecPath));
        else env.RunKey!.Remove(RunValueName);
    }

    // ---------------------------------------------------------------- Linux (loginItem.ts)

    /// <summary>XDG autostart file; its name is not the launcher's astra.desktop, so neither shadows the other (<c>linuxAutostartPath</c>).</summary>
    public static string LinuxAutostartPath(Func<string, string?> env, string home)
    {
        var configHome = env("XDG_CONFIG_HOME")?.Trim();
        return Path.Combine(string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome, "autostart", "io.astra.desktop.desktop");
    }

    /// <summary>What the entry starts: the AppImage file itself (its mount point changes every launch), else the executable.</summary>
    public static string LinuxExecutable(Func<string, string?> env, string execPath)
        => env("APPIMAGE")?.Trim() is { Length: > 0 } appImage ? appImage : execPath;

    private string[] LinuxArgv() => [LinuxExecutable(env.Env, env.ExecPath), HiddenArg];

    private static readonly Regex ExecReserved = new(@"[\s""'\\><~|&;$*?#`()]");

    /// <summary>One Exec argument per the Desktop Entry spec: <c>%</c> doubled; quoted with <c>"</c> <c>`</c> <c>$</c> <c>\</c> escaped, then every <c>\</c> escaped again (<c>desktopExecArg</c>).</summary>
    public static string DesktopExecArg(string arg)
    {
        var value = arg.Replace("%", "%%", StringComparison.Ordinal);
        if (!ExecReserved.IsMatch(value)) return value;
        var quoted = Regex.Replace(value, @"([""`$\\])", @"\$1");
        return "\"" + quoted.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
    }

    private static string ExecLine(IEnumerable<string> argv) => "Exec=" + string.Join(' ', argv.Select(DesktopExecArg));

    /// <summary>The entry's text (<c>renderLinuxAutostart</c>).</summary>
    public static string RenderLinuxAutostart(IEnumerable<string> argv)
        => $"[Desktop Entry]\nType=Application\nName=Astra\nComment=Local AI gateway\n{ExecLine(argv)}\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";

    /// <summary>An entry the desktop's own startup settings switched off (they keep the file and mark it) does not count as on.</summary>
    public static bool LinuxAutostartEnabled(string text)
        => !Regex.IsMatch(text, @"^\s*Hidden\s*=\s*true\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)
           && !Regex.IsMatch(text, @"^\s*X-GNOME-Autostart-enabled\s*=\s*false\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>The entry pointed at <paramref name="argv"/> with everything else kept; null when it already is or has no Exec line.</summary>
    public static string? RetargetLinuxAutostart(string text, IEnumerable<string> argv)
    {
        var line = ExecLine(argv);
        var exec = new Regex("^Exec=.*$", RegexOptions.Multiline);
        if (!exec.IsMatch(text)) return null;
        var next = exec.Replace(text, _ => line, 1);
        return next == text ? null : next;
    }

    private bool LinuxEnabled()
    {
        var file = LinuxAutostartPath(env.Env, env.Home);
        return File.Exists(file) && LinuxAutostartEnabled(File.ReadAllText(file));
    }

    private void SetLinux(bool enabled)
    {
        var file = LinuxAutostartPath(env.Env, env.Home);
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, RenderLinuxAutostart(LinuxArgv()));
        }
        else if (File.Exists(file))
        {
            File.Delete(file);
        }
    }
}
