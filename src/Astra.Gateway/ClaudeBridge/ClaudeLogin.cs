using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// A <c>claude auth login --claudeai</c> the admin started for an account (magpie's sign-in, MIT): the program opens the browser itself on a
/// desktop and prints the page's address, which is kept so it can be shown (a server has no browser to open). It ends by itself once
/// the sign-in is done; one nobody finishes is ended after <see cref="Lifetime"/>.
/// </summary>
public sealed partial class ClaudeLogin : IAsyncDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Process _process;
    private readonly StringBuilder _said = new();
    private readonly CancellationTokenSource _limit = new(Lifetime);

    /// <summary>The sign-in page the program printed; null until it did.</summary>
    public string? Url { get; private set; }

    /// <summary><c>waiting</c> | <c>done</c> | <c>failed</c>.</summary>
    public string State { get; private set; } = "waiting";

    public string? Message { get; private set; }

    private ClaudeLogin(Process process)
    {
        _process = process;
        _ = Task.Run(ReadAsync);
        _limit.Token.Register(() => _ = ClaudeProcess.StopAsync(_process, TimeSpan.FromSeconds(2)));
    }

    public static ClaudeLogin Start(string binary, string configDir, string? proxy)
    {
        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in (string[])["auth", "login", "--claudeai"]) psi.ArgumentList.Add(a);
        ClaudeProcess.ApplyEnvironment(psi, configDir, proxy, run: false);
        return new ClaudeLogin(Process.Start(psi) ?? throw new InvalidOperationException("claude did not start"));
    }

    private async Task ReadAsync()
    {
        try
        {
            var err = Task.Run(async () =>
            {
                string? l;
                while ((l = await _process.StandardError.ReadLineAsync()) is not null) Note(l);
            });
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync()) is not null) Note(line);
            await err;
            await _process.WaitForExitAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        var code = _process.HasExited ? _process.ExitCode : -1;
        if (code == 0)
        {
            State = "done";
        }
        else
        {
            State = "failed";
            Message = _said.Length > 0 ? _said.ToString().Trim() : "claude auth login 没有完成。";
        }
    }

    private void Note(string raw)
    {
        var line = Ansi().Replace(raw, "").Trim();
        if (line.Length == 0) return;
        if (Url is null && Link().Match(line) is { Success: true } m) Url = m.Value;
        if (_said.Length < 4000) _said.AppendLine(line);
    }

    public async ValueTask DisposeAsync()
    {
        await _limit.CancelAsync();
        await ClaudeProcess.StopAsync(_process, TimeSpan.FromSeconds(2));
        _limit.Dispose();
    }

    [GeneratedRegex(@"https://\S+")]
    private static partial Regex Link();

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();
}
