using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// The environment and the ending of a <c>claude</c> process (ported from magpie's <c>cleanClaudeEnv</c> / <c>claudeRunEnv</c> /
/// <c>inClaudeDir</c> / <c>proxied</c>, MIT). Every run is the real Claude Code with none of the machine's wiring that would send it
/// elsewhere than to the account's own sign-in.
/// </summary>
public static class ClaudeProcess
{
    /// <summary>What points <c>claude</c> at another endpoint, token or entrypoint than its own sign-in; never passed on.</summary>
    private static readonly string[] Blocked =
    [
        "ANTHROPIC_BASE_URL", "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT",
        "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_PROMPT_CACHE_TTL", "FORCE_PROMPT_CACHING_5M", "CLAUDE_CODE_EXTRA_BODY", "ANTHROPIC_BETAS",
        "CLAUDE_CONFIG_DIR", "CLAUDE_SECURESTORAGE_CONFIG_DIR",
    ];

    private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "https_proxy", "http_proxy", "all_proxy"];

    /// <summary>
    /// Sets the environment of a run (<paramref name="run"/>) or of a short question. <paramref name="configDir"/> empty = the account
    /// <c>claude</c> is signed in to by default. <paramref name="proxy"/>: null = what the process has, "direct" = none, else the URL —
    /// <c>claude</c> is a Bun program, which takes only http:// and https:// proxies (a socks5:// one failed every request after minutes of retries).
    /// </summary>
    public static void ApplyEnvironment(ProcessStartInfo psi, string? configDir, string? proxy, bool run)
    {
        var env = psi.Environment;
        foreach (var name in Blocked) env.Remove(name);
        // the machine's own default stays when the account names no directory
        if (string.IsNullOrEmpty(configDir))
        {
            var own = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(own)) env["CLAUDE_CONFIG_DIR"] = own;
        }
        else
        {
            env["CLAUDE_CONFIG_DIR"] = configDir;
        }
        // no auto memory (every run works in one folder: a memory one conversation wrote there would be in the others' prompts and undo their
        // cache), no auto compact (the caller compacts), no repository status in the prompt (it changes with each commit)
        env["ENABLE_CLAUDEAI_MCP_SERVERS"] = "0";
        env["DISABLE_AUTO_COMPACT"] = "1";
        env["CLAUDE_CODE_DISABLE_AUTO_MEMORY"] = "1";
        env["CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS"] = "1";
        env["DISABLE_AUTOUPDATER"] = "1";
        env["DISABLE_ERROR_REPORTING"] = "1";
        if (run)
        {
            // the run's cache lives as long as claude's own, and the mark is stated so that it is the same everywhere
            env["CLAUDE_CODE_PROMPT_CACHE_TTL"] = "1h";
            if (!OperatingSystem.IsWindows() && Environment.UserName == "root") env["IS_SANDBOX"] = "1"; // --dangerously-skip-permissions refuses root otherwise
        }
        else
        {
            // a short question has no history to send and asks for nothing of its own
            env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        }
        if (proxy is null) return;
        foreach (var v in ProxyVariables) env.Remove(v);
        if (proxy.Equals("direct", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var v in (string[])["HTTPS_PROXY", "HTTP_PROXY", "https_proxy", "http_proxy"]) env[v] = proxy;
    }

    /// <summary>A reason <c>claude</c> cannot be sent through <paramref name="proxy"/>, or null.</summary>
    public static string? ProxyProblem(string? proxy) =>
        proxy is not null && proxy.StartsWith("socks", StringComparison.OrdinalIgnoreCase)
            ? "claude (a Bun program) takes only http:// and https:// proxies; give this account an http:// proxy (a SOCKS5 one makes every request fail after minutes of retries)"
            : null;

    [DllImport("libc", EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    /// <summary>
    /// Asks <c>claude</c> to end (SIGTERM, which lets it remove <c>~/.claude/.oauth_refresh.lock</c>: a SIGKILL left it for the next requests to
    /// fail on) and kills its tree once it has exited or <paramref name="grace"/> has passed. Windows kills the tree at once.
    /// </summary>
    public static async Task StopAsync(Process process, TimeSpan grace)
    {
        try
        {
            if (process.HasExited) return;
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Kill(process.Id, 15);
                }
                catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
                {
                    process.Kill(true);
                    return;
                }
                using var cts = new CancellationTokenSource(grace);
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
            if (!process.HasExited) process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }
}
