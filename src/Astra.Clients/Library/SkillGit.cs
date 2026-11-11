using System.Diagnostics;
using Astra.Clients.Config;

namespace Astra.Clients.Library;

/// <summary>
/// Fetches a Git repository's files for the Skill installer (<c>.plan/resources-library.md</c> §8). It runs the user's own <c>git</c>,
/// so their credentials and SSH keys apply, and it never goes through a shell. The address is checked first: nothing that could be
/// read as an option or a transport helper, and no password or token in it (the source is shown in the UI).
/// </summary>
public static class SkillGit
{
    /// <summary>Why an address is not acceptable, or null when it is. Public for tests.</summary>
    public static string? CheckUrl(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return "empty";
        if (url.StartsWith('-')) return "option";
        if (url.Contains("::", StringComparison.Ordinal)) return "transport";
        if (url.Contains(' ') || url.Contains('\n') || url.Contains('\0')) return "invalid";
        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "invalid";
            if (uri.Scheme is not ("https" or "http" or "ssh" or "git" or "file")) return "scheme";
            // ssh://git@host/… is a user name, not a secret; a password or a token in https://… is.
            if (uri.UserInfo.Contains(':') || (uri.Scheme is "https" or "http" && uri.UserInfo.Length > 0)) return "credentials";
            return null;
        }
        // scp-like: user@host:path
        return System.Text.RegularExpressions.Regex.IsMatch(url, @"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$") ? null : "invalid";
    }

    public static string? CheckRef(string? gitRef) =>
        gitRef is null or "" ? null : gitRef.StartsWith('-') || gitRef.Contains(' ') || gitRef.Contains("..", StringComparison.Ordinal) ? "ref" : null;

    /// <summary>Fetches <paramref name="gitRef"/> (default HEAD) of <paramref name="url"/> into <paramref name="destination"/> (without .git); returns the commit.</summary>
    public static async Task<string> FetchAsync(ClientEnvironment env, string url, string? gitRef, string destination, CancellationToken ct)
    {
        if (CheckUrl(url) is { } why) throw new SkillLibraryException($"The repository address is not accepted ({why})", SkillFailure.Invalid);
        if (CheckRef(gitRef) is not null) throw new SkillLibraryException("The branch or tag is not accepted", SkillFailure.Invalid);
        var git = env.FindTool("git") ?? throw new SkillLibraryException("git was not found on this machine", SkillFailure.Invalid);
        Directory.CreateDirectory(destination);
        await RunAsync(env, git, destination, ["init", "-q"], ct);
        string[] safe = ["-c", "protocol.ext.allow=never", "-c", "core.symlinks=false"];
        await RunAsync(env, git, destination, [.. safe, "fetch", "-q", "--depth", "1", "--no-tags", "--", url.Trim(), string.IsNullOrEmpty(gitRef) ? "HEAD" : gitRef], ct);
        await RunAsync(env, git, destination, [.. safe, "checkout", "-q", "FETCH_HEAD"], ct);
        var commit = (await RunAsync(env, git, destination, ["rev-parse", "FETCH_HEAD"], ct)).Trim();
        var gitDir = Path.Combine(destination, ".git");
        if (Directory.Exists(gitDir))
        {
            foreach (var f in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(gitDir, recursive: true);
        }
        return commit;
    }

    private static async Task<string> RunAsync(ClientEnvironment env, string git, string workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(git)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (env.GetEnvironmentVariable("GIT_SSH_COMMAND") is not { Length: > 0 }) psi.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        using var process = Process.Start(psi) ?? throw new SkillLibraryException("git could not be started", SkillFailure.Invalid);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            ct.ThrowIfCancellationRequested();
            throw new SkillLibraryException("git took too long", SkillFailure.Invalid);
        }
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new SkillLibraryException($"git {args.FirstOrDefault(a => !a.StartsWith('-') && a != "protocol.ext.allow=never" && a != "core.symlinks=false")} failed: {error.Trim().Split('\n').LastOrDefault()?.Trim()}", SkillFailure.Invalid);
        return output;
    }
}
