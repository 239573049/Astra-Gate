using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Providers.Subscription;

/// <summary>
/// 本机找到的一份 GitHub 授权。<paramref name="Source"/> 是给用户看的来源说明
/// （如「VS Code（系统凭据存储）」），只用于展示与排错。
/// </summary>
public sealed record LocalGitHubCredential(string Token, string Source);

/// <summary>
/// 读本机凭据需要的最小环境：home 目录、OS 标签（"osx"/"linux"/"windows"）与环境变量查询。
/// 与 <c>Astra.Clients.Config.ClientEnvironment</c> 同形，但故意不依赖它——Astra.Providers 不引用
/// Astra.Clients（两边是同一层的兄弟程序集），而且这个类型比它需要的更窄。
/// </summary>
public sealed record LocalCredentialEnvironment(string HomeDirectory, string Os, Func<string, string?>? Environment = null)
{
    public string? GetEnvironmentVariable(string name) => Environment?.Invoke(name);

    public string Combine(params string[] parts) => Path.Combine([HomeDirectory, .. parts]);

    /// <summary>Reads a file as UTF-8 text, or null when it does not exist.</summary>
    public string? ReadTextOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
}

/// <summary>
/// 找本机已经登录过的 GitHub 授权，好直接接成 Copilot 订阅账号，不必再走一遍设备码授权。
///
/// 为什么这件事成立：Copilot 没有自己的长期凭据——真正发请求的短时令牌是拿 GitHub token 换来的
/// （<see cref="OAuthClient.CopilotTokenAsync"/>）。所以「本机已经登过 Copilot」等价于
/// 「本机有一份能换出 Copilot 令牌的 GitHub token」，而 VS Code 的 GitHub 会话正好就是这样一份 token：
/// 它把会话写在系统凭据存储里（macOS 的 service 名见 <see cref="VsCodeService"/>），实测就是
/// <c>gho_…</c> 形态的 GitHub OAuth token。
///
/// 注意「有 GitHub token」不等于「有 Copilot 订阅」：只有真的换出 Copilot 令牌才算数，
/// 所以导入是一份一份试、试成功为止（见 SubscriptionEndpoints 的 import-copilot）。
///
/// 读系统凭据存储在 macOS 上会弹系统授权框，所以它只出现在 <see cref="All"/>（用户点了「导入」才跑）；
/// <see cref="Cheap"/> 是不产生任何副作用的子集，给页面加载时的探测用。
/// </summary>
public static class LocalCopilotLogin
{
    /// <summary>
    /// VS Code 存取 GitHub 会话用的凭据名（vscode 的 keychain.ts 把 service 原样透传，
    /// 所以 macOS keychain 里就是这个 service；本机实测条目里存的是 <c>gho_…</c>）。
    /// </summary>
    private const string VsCodeService = "GitHub - https://api.github.com";

    /// <summary>GitHub token 的形态：OAuth / 用户 / Actions / 旧版 PAT / 细粒度 PAT。</summary>
    public static bool LooksLikeGitHubToken(string? token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Length is > 20 and < 300
        && !token.Any(char.IsWhiteSpace)
        && (token.StartsWith("gho_", StringComparison.Ordinal)
            || token.StartsWith("ghu_", StringComparison.Ordinal)
            || token.StartsWith("ghs_", StringComparison.Ordinal)
            || token.StartsWith("ghp_", StringComparison.Ordinal)
            || token.StartsWith("github_pat_", StringComparison.Ordinal));

    /// <summary>不产生副作用的来源：环境变量与 Copilot 插件配置。</summary>
    public static List<LocalGitHubCredential> Cheap(LocalCredentialEnvironment env) =>
        Collect([
            EnvironmentToken(env, "GH_TOKEN"),
            EnvironmentToken(env, "GITHUB_TOKEN"),
            CopilotHostsFile(env),
        ]);

    /// <summary>
    /// 全部来源，按可信度排序（环境变量 → Copilot 插件配置 → 系统凭据存储）。
    /// 是惰性枚举：调用方找到一份能用的就停下，便宜来源已经成功时不会再去读系统凭据存储
    /// （macOS 上那一步会弹系统授权框）。
    /// </summary>
    public static IEnumerable<LocalGitHubCredential> All(LocalCredentialEnvironment env)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in Cheap(env))
            if (Admit(candidate)) yield return candidate;

        var stored = OsCredentialStore(env); // 只有便宜来源全都失败时才会走到这里
        if (Admit(stored)) yield return stored!;

        bool Admit(LocalGitHubCredential? candidate) =>
            candidate is not null && LooksLikeGitHubToken(candidate.Token) && seen.Add(candidate.Token);
    }

    /// <summary>丢掉空的、形态不对的、以及与前面重复的（同一份 token 只试一次）。</summary>
    private static List<LocalGitHubCredential> Collect(IEnumerable<LocalGitHubCredential?> candidates)
    {
        var result = new List<LocalGitHubCredential>();
        foreach (var candidate in candidates)
        {
            if (candidate is null || !LooksLikeGitHubToken(candidate.Token)) continue;
            if (result.Any(existing => existing.Token == candidate.Token)) continue;
            result.Add(candidate);
        }
        return result;
    }

    private static LocalGitHubCredential? EnvironmentToken(LocalCredentialEnvironment env, string name)
    {
        var value = env.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : new LocalGitHubCredential(value, $"环境变量 {name}");
    }

    /// <summary>GitHub Copilot 插件（vim / neovim）的 <c>hosts.json</c>：<c>{"github.com":{"oauth_token":"gho_…"}}</c>。</summary>
    private static LocalGitHubCredential? CopilotHostsFile(LocalCredentialEnvironment env)
    {
        var text = env.ReadTextOrNull(env.Combine(".config", "github-copilot", "hosts.json"));
        if (text is null) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject hosts) return null;
            foreach (var (host, value) in hosts)
            {
                if (value is JsonObject entry && entry["oauth_token"] is JsonValue token
                    && token.TryGetValue<string>(out var secret) && !string.IsNullOrWhiteSpace(secret))
                    return new LocalGitHubCredential(secret, $"Copilot 插件配置（{host}）");
            }
        }
        catch (JsonException)
        {
            // 配置坏了不值得报错：这只是「少登一次」的便捷路径，不是登录的必经之路。
        }
        return null;
    }

    /// <summary>
    /// 系统凭据存储里的 GitHub 授权。macOS 用系统的 <c>security</c> 读 VS Code 写进去的条目
    /// （service = <see cref="VsCodeService"/>）；Linux 走 libsecret 的 <c>secret-tool</c>
    /// （VS Code 在 gnome-keyring 里的属性布局是 service <c>vscode.github-authentication</c>）。
    /// Windows 凭据管理器没有稳定的命令行读取方式，这里不猜；那边可以用 GH_TOKEN 提供。
    /// 读不到一律当「没有」。
    /// </summary>
    private static LocalGitHubCredential? OsCredentialStore(LocalCredentialEnvironment env)
    {
        try
        {
            var (ok, output) = env.Os switch
            {
                "osx" => Run("/usr/bin/security", ["find-generic-password", "-s", VsCodeService, "-w"]),
                "linux" => Run("secret-tool", ["lookup", "service", "vscode.github-authentication", "account", VsCodeService]),
                _ => (false, ""),
            };
            var token = output.Trim();
            return ok && token.Length > 0 ? new LocalGitHubCredential(token, "VS Code（系统凭据存储）") : null;
        }
        catch (Exception)
        {
            // 没有这个命令、没有这条凭据、用户拒绝了授权框……都不是错误。
            return null;
        }
    }

    /// <summary>
    /// 读系统凭据存储的等待上限。这个值不能太短：macOS 上读别的应用写的条目会弹系统授权框，
    /// 用户点它之前 <c>security</c> 会一直阻塞——超时太短会变成"明明有凭据却报没找到"，
    /// 而那是最难排查的一类错误。用户点了「导入」就是在等这一下，宁可多等。
    /// </summary>
    private static readonly TimeSpan CredentialStoreTimeout = TimeSpan.FromSeconds(60);

    /// <summary>跑一个只读的系统命令取输出；超时或失败一律当作没有。</summary>
    private static (bool Ok, string Output) Run(string fileName, string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        // stderr 只用来把系统提示挡在服务端日志外面（`security` 找不到条目时会往 stderr 抱怨）。
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CredentialStoreTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 已经退出了就没什么可杀的。
            }
            return (false, "");
        }
        return (process.ExitCode == 0, stdout.GetAwaiter().GetResult());
    }
}
