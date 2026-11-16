using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>The shell profile and quoting shared by the clients Astra routes through a shell function (Dim, Antigravity CLI; Muse Code has its own copy).</summary>
internal static class ShellRc
{
    /// <summary>The rc file: <c>~/.zshrc</c> (macOS default shell), or <c>~/.bashrc</c> when only that one exists.</summary>
    public static string File(ClientEnvironment env)
    {
        var zshrc = env.Combine(".zshrc");
        var bashrc = env.Combine(".bashrc");
        return !env.FileExists(zshrc) && env.FileExists(bashrc) ? bashrc : zshrc;
    }

    /// <summary>POSIX single-quoting: wraps the value in single quotes and escapes embedded quotes.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
