using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.ClaudeBridge;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>Where the Claude bridge's helper is run from (this very program).</summary>
public static class ClaudeBridgeHost
{
    /// <summary>The command that runs <c>claude-mcp-helper</c>: this executable; under the JIT (<c>dotnet run</c>) <c>dotnet</c> and the assembly.</summary>
    public static (string Command, IReadOnlyList<string> Args) HelperCommand()
    {
        var exe = Environment.ProcessPath ?? "astra-server";
        return Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (exe, [Path.Combine(AppContext.BaseDirectory, typeof(ClaudeBridgeHost).Assembly.GetName().Name + ".dll")])
            : (exe, []);
    }
}

/// <summary>
/// Administration of the Claude bridge (linked update 36): what <c>claude</c> program was found, its settings and live runs, who is signed in to it,
/// and the accounts of kind <see cref="AccountKinds.ClaudeBridge"/>. Everything that names a program or a directory for Astra to use is
/// limited to callers on this machine by the endpoints (like the tunnel's <c>cloudflaredPath</c>).
/// </summary>
public sealed class ClaudeBridgeAdmin(AstraDatabase db, SettingsService settings, ClaudeBridgeService bridge)
{
    private readonly ConcurrentDictionary<string, ClaudeLogin> _logins = new(StringComparer.Ordinal);

    public async Task<ClaudeBridgeStatusDto> StatusAsync(CancellationToken ct)
    {
        var info = bridge.Locator.Find();
        var provider = await ClaudeProviderAsync(null, ct);
        return new ClaudeBridgeStatusDto(info is not null, info?.Path, info?.Version, info?.Source, SettingsDto(),
            bridge.Runs().Select(r => new ClaudeRunDto(r.Id, r.Pid, r.Model, r.State, r.Since, r.Account)).ToList(),
            provider?.Id, provider is null ? null : SubscriptionSupport.ClientPolicyOf(provider));
    }

    private ClaudeBridgeSettingsDto SettingsDto()
    {
        var s = settings.Current.ClaudeBridge;
        return new ClaudeBridgeSettingsDto(s.BinaryPath, s.MaxRuns, s.IdleMinutes);
    }

    public async Task<ClaudeBridgeSettingsDto> UpdateSettingsAsync(ClaudeBridgeSettingsPatch patch, CancellationToken ct)
    {
        if (patch.MaxRuns is { } runs and (< 1 or > Astra.Core.ClaudeBridgeSettings.MaxRunsLimit))
            throw new AdminApiException(400, $"maxRuns must be 1..{Astra.Core.ClaudeBridgeSettings.MaxRunsLimit}");
        if (patch.IdleMinutes is { } idle and (< 1 or > 120)) throw new AdminApiException(400, "idleMinutes must be 1..120");
        if (patch.BinaryPath is { Length: > 0 } path && (!Path.IsPathRooted(path) || !File.Exists(path)))
            throw new AdminApiException(400, "binaryPath must be the full path of an existing program");
        await settings.UpdateAsync(s =>
        {
            if (patch.BinaryPath is { } p) s.ClaudeBridge.BinaryPath = p.Length == 0 ? null : p;
            if (patch.MaxRuns is { } m) s.ClaudeBridge.MaxRuns = m;
            if (patch.IdleMinutes is { } i) s.ClaudeBridge.IdleMinutes = i;
        }, ct);
        return SettingsDto();
    }

    /// <summary>Who <c>claude auth status</c> says is signed in, in an account's directory (or the default one).</summary>
    public async Task<ClaudeSignInDto> SignInAsync(string? accountId, string? configDir, CancellationToken ct)
    {
        string? proxy = null;
        if (accountId is not null)
        {
            var account = await db.Accounts.GetAsync(accountId, ct) ?? throw new AdminApiException(404, "no such account");
            if (!account.IsClaudeBridge) throw new AdminApiException(409, "not a Claude bridge account");
            configDir = account.ClaudeBridgeConfigDir;
            proxy = account.HttpProxy;
        }
        var info = bridge.Locator.Find();
        if (info is null) return new ClaudeSignInDto(false, false, null, null, null, null, "claude was not found");
        var s = await bridge.Locator.SignInAsync(info.Path, configDir ?? "", proxy, ct);
        return new ClaudeSignInDto(s.Known, s.LoggedIn, s.Email, s.Plan, s.Method, s.Organization, s.Error);
    }

    // ---------------------------------------------------------------- accounts

    /// <summary>The Claude subscription provider bridge accounts are added to.</summary>
    private async Task<Provider?> ClaudeProviderAsync(string? providerId, CancellationToken ct)
    {
        var all = await db.Providers.ListAsync(ct);
        return providerId is null ? all.FirstOrDefault(SubscriptionSupport.IsClaudeSubscription) : all.FirstOrDefault(p => p.Id == providerId && SubscriptionSupport.IsClaudeSubscription(p));
    }

    public async Task<ProviderAccountDto> AddAccountAsync(ClaudeBridgeAccountRequest request, CancellationToken ct)
    {
        var provider = await ClaudeProviderAsync(request.ProviderId, ct)
                       ?? throw new AdminApiException(404, "Add a Claude subscription provider first (Providers › Add › Claude subscription), then add the claude program to it.");
        var dir = request.ConfigDir?.Trim() ?? "";
        ValidateDirectory(dir);
        var proxy = request.HttpProxy?.Trim();
        if (proxy is { Length: > 0 } && ClaudeProcess.ProxyProblem(proxy) is { } problem) throw new AdminApiException(400, problem);
        var name = request.DisplayName?.Trim() is { Length: > 0 } n ? n : dir.Length == 0 ? "claude" : Path.GetFileName(dir.TrimEnd('/', '\\'));
        var account = new ProviderAccount
        {
            Id = Guid.NewGuid().ToString("N"), ProviderId = provider.Id, DisplayName = name, Kind = AccountKinds.ClaudeBridge, Source = AccountSources.ClaudeBridge,
            Status = AccountStatus.Active, HttpProxy = proxy is { Length: > 0 } ? proxy : null,
            Extra = new JsonObject { ["claude_bridge"] = new JsonObject { ["config_dir"] = dir } },
        };
        // who it is, when the program can say (the account's name and plan); never a reason to refuse the account
        var who = await SignInAsync(null, dir, ct);
        if (who.LoggedIn)
        {
            account.AccountEmail = who.Email;
            account.Plan = who.Plan;
        }
        await db.Accounts.InsertAsync(account, ct);
        return SubscriptionEndpoints.ToDto((await db.Accounts.GetAsync(account.Id, ct))!, false);
    }

    public async Task<ProviderAccountDto> PatchAccountAsync(string id, ClaudeBridgeAccountPatch patch, CancellationToken ct)
    {
        var account = await db.Accounts.GetAsync(id, ct) ?? throw new AdminApiException(404, "no such account");
        if (!account.IsClaudeBridge) throw new AdminApiException(409, "not a Claude bridge account");
        if (patch.ConfigDir is { } dir)
        {
            ValidateDirectory(dir.Trim());
            account.Extra["claude_bridge"] = new JsonObject { ["config_dir"] = dir.Trim() };
            await db.Accounts.UpdateAsync(account, ct);
        }
        return SubscriptionEndpoints.ToDto((await db.Accounts.GetAsync(id, ct))!, false);
    }

    private static void ValidateDirectory(string dir)
    {
        if (dir.Length == 0) return;
        if (!Path.IsPathRooted(dir)) throw new AdminApiException(400, "configDir must be a full path (or empty for the program's default one)");
        if (dir.Contains('\0')) throw new AdminApiException(400, "configDir is not a path");
    }

    // ---------------------------------------------------------------- sign-in

    /// <summary>Starts <c>claude auth login --claudeai</c> for an account (one at a time per account); the page's address comes back once printed.</summary>
    public async Task<ClaudeBridgeLoginDto> StartLoginAsync(string accountId, CancellationToken ct)
    {
        var account = await db.Accounts.GetAsync(accountId, ct) ?? throw new AdminApiException(404, "no such account");
        if (!account.IsClaudeBridge) throw new AdminApiException(409, "not a Claude bridge account");
        var info = bridge.Locator.Find() ?? throw new AdminApiException(409, "claude was not found: install Claude Code first");
        if (_logins.TryRemove(accountId, out var old)) await old.DisposeAsync();
        ClaudeLogin login;
        try
        {
            login = ClaudeLogin.Start(info.Path, account.ClaudeBridgeConfigDir, account.HttpProxy);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new AdminApiException(502, "claude did not start: " + e.Message);
        }
        _logins[accountId] = login;
        // the page's address is printed within a moment
        for (var i = 0; i < 50 && login.Url is null && login.State == "waiting"; i++) await Task.Delay(100, ct);
        return new ClaudeBridgeLoginDto(login.Url, login.State, login.Message);
    }

    public ClaudeBridgeLoginDto LoginState(string accountId) =>
        _logins.TryGetValue(accountId, out var login) ? new ClaudeBridgeLoginDto(login.Url, login.State, login.Message) : new ClaudeBridgeLoginDto(null, "none", null);

    // ---------------------------------------------------------------- the one-off ask (test, warm-up)

    /// <summary>One short message through <c>claude</c> for the account: proves that it answers.</summary>
    public Task AskAsync(ProviderAccount account, string model, string prompt, CancellationToken ct) =>
        bridge.AskAsync(new ClaudeTarget(account.ClaudeBridgeConfigDir, account.HttpProxy, account.Id), model, prompt, ct);
}
