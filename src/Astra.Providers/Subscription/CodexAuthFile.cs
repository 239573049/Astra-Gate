using System.Text.Json.Nodes;

namespace Astra.Providers.Subscription;

/// <summary>
/// codex-cli 的本地登录态（<c>~/.codex/auth.json</c>）：用户已经用 <c>codex login</c> 登过的话，
/// 直接把那份凭据接进来，不必再走一遍浏览器授权。文件形状来自 codex 自己的 auth 存储：
/// <code>{ "auth_mode": "chatgpt", "tokens": { "access_token", "refresh_token", "id_token", "account_id" } }</code>
/// </summary>
public static class CodexAuthFile
{
    /// <summary>One imported credential set; tokens are the raw (still unencrypted) values.</summary>
    public sealed record Credentials(string AccessToken, string RefreshToken, string? IdToken, string? AccountId);

    /// <summary>Parses the JSON text; null with a user-facing reason when it is not a usable ChatGPT login.</summary>
    public static Credentials? Parse(string json, out string? error)
    {
        error = null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            error = "本机 Codex 登录文件不是有效的 JSON";
            return null;
        }
        if (root is not JsonObject o)
        {
            error = "本机 Codex 登录文件不是有效的 JSON";
            return null;
        }
        var mode = o["auth_mode"]?.GetValue<string>();
        if (o["tokens"] is not JsonObject tokens)
        {
            error = mode == "apikey"
                ? "本机 Codex 用的是 API Key 登录，没有可导入的订阅（ChatGPT）凭据"
                : "本机 Codex 登录文件里没有 tokens";
            return null;
        }
        var access = Text(tokens, "access_token");
        var refresh = Text(tokens, "refresh_token");
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
        {
            error = "本机 Codex 凭据不完整（缺少 access_token / refresh_token）";
            return null;
        }
        var accountId = Text(tokens, "account_id")
                        ?? SubscriptionTokenService.ChatGptAccountId(access);
        return new Credentials(access, refresh, Text(tokens, "id_token"), accountId);
    }

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
