using System.Collections;
using Astra.Core;

namespace Astra.Plugins.Runtime;

/// <summary>
/// The environment Bun runs with — the plugin host and <c>bun add</c> alike: Astra's own, minus every
/// <c>ASTRA_*</c> variable (a container's <c>ASTRA_ADMIN_PASSWORD</c> must never reach plugin code),
/// with Astra's proxy as variables (Bun can't read the macOS / Windows proxy settings).
/// </summary>
public static class ChildEnvironment
{
    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy"];

    /// <summary>
    /// Builds the variables for a Bun child. For the plugin host (<paramref name="forHost"/>) the
    /// proxy goes in as <c>MAGPIE_*_PROXY</c>, which host.js applies to each fetch itself: Bun reads
    /// <c>*_PROXY</c> once and puts every fetch through them, so a provider set to "direct" could not
    /// go around them (magpie's hostEnv). <paramref name="path"/> is the PATH to give it — the login
    /// shell's and the folders users' tools live in, so a plugin that runs a vendor's CLI finds it.
    /// </summary>
    public static Dictionary<string, string> Build(bool forHost, IEnumerable<string>? path = null, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var env = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = (string)e.Key;
            if (key.StartsWith("ASTRA_", StringComparison.OrdinalIgnoreCase)) continue;
            if (Array.IndexOf(ProxyVariables, key) >= 0 || key.Equals("NO_PROXY", StringComparison.OrdinalIgnoreCase)) continue;
            env[key] = (string?)e.Value ?? "";
        }
        if (path is not null) env["PATH"] = string.Join(Path.PathSeparator, path);
        env["NO_COLOR"] = "1";

        var (proxy, noProxy) = Proxy(getEnv);
        if (proxy is not null)
        {
            var prefix = forHost ? "MAGPIE_" : "";
            env[prefix + "HTTPS_PROXY"] = proxy;
            env[prefix + "HTTP_PROXY"] = proxy;
            if (!forHost)
            {
                env["https_proxy"] = proxy;
                env["http_proxy"] = proxy;
            }
            env[prefix + "NO_PROXY"] = noProxy;
        }
        return env;
    }

    /// <summary>
    /// The proxy a Bun child goes through and what bypasses it, by Astra's proxy setting: "direct" is
    /// none, "custom" the configured one, "system" the environment's or else the operating system's.
    /// Mirrors ClientInstallService.ProxyEnvironment. A SOCKS proxy is left out: Bun takes only
    /// http:// and https:// ones, and refuses to start a fetch with any other.
    /// </summary>
    public static (string? Proxy, string NoProxy) Proxy(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        const string local = "localhost,127.0.0.1,::1";
        if (SystemProxy.Mode == SystemProxy.ModeDirect) return (null, local);
        if (SystemProxy.CustomForChildren is var (url, bypass))
            return (HttpOnly(url), Join(local, bypass));

        foreach (var name in ProxyVariables)
            if (getEnv(name) is { Length: > 0 } value)
                return (HttpOnly(value), Join(local, getEnv("NO_PROXY") ?? getEnv("no_proxy")));
        return (HttpOnly(SystemProxy.ProxyUrlFor(new Uri("https://registry.npmjs.org/"))), local);
    }

    private static string? HttpOnly(string? url) =>
        url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ? url
            : null;

    private static string Join(string local, string? bypass) =>
        string.Join(',', local.Split(',').Concat((bypass ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
}
