using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Alerts;
using Astra.Gateway.Protocol;

namespace Astra.Server.Alerts;

/// <summary>
/// Renders a delivery's stored payload (always Astra's generic JSON) into what a webhook of a given
/// <see cref="WebhookFormats"/> expects, signs it the platform's way, and reads the platform's answer. Rendering happens
/// at send time so signatures carry a fresh timestamp (Feishu and DingTalk reject old ones); the delivery log keeps the
/// generic payload.
/// <list type="bullet">
/// <item>Feishu: <c>{ msg_type: "text", content: { text } }</c>; signed: <c>timestamp</c> (seconds) and
/// <c>sign = base64(HMAC_SHA256(key: "&lt;timestamp&gt;\n&lt;secret&gt;", data: ""))</c> in the body; success = <c>code</c> 0.</item>
/// <item>DingTalk: <c>{ msgtype: "text", text: { content } }</c>; signed: <c>&amp;timestamp=&lt;ms&gt;&amp;sign=</c>
/// <c>urlencode(base64(HMAC_SHA256(key: secret, data: "&lt;ms&gt;\n&lt;secret&gt;")))</c> on the URL; success = <c>errcode</c> 0.</item>
/// <item>WeCom: <c>{ msgtype: "text", text: { content } }</c>; success = <c>errcode</c> 0.</item>
/// <item>Slack: <c>{ text }</c>; Discord: <c>{ content }</c> (at most 2000 characters); success = 2xx.</item>
/// </list>
/// </summary>
public static class WebhookFormatter
{
    private const int DiscordMaxChars = 2000;

    /// <summary>The URL and body to send; <paramref name="secret"/> is the webhook's decrypted secret (if any).</summary>
    public static (string Url, string Body) Render(string format, string url, string payloadJson, string? secret, DateTimeOffset now, string locale)
    {
        if (format == WebhookFormats.Generic) return (url, payloadJson);
        var payload = JsonNode.Parse(payloadJson) as JsonObject ?? [];
        var text = Text(payload, locale);
        JsonObject body;
        switch (format)
        {
            case WebhookFormats.Feishu:
                body = new JsonObject { ["msg_type"] = "text", ["content"] = new JsonObject { ["text"] = text } };
                if (secret is not null)
                {
                    var ts = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                    body["timestamp"] = ts;
                    body["sign"] = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes($"{ts}\n{secret}"), Array.Empty<byte>()));
                }
                break;
            case WebhookFormats.DingTalk:
                body = new JsonObject { ["msgtype"] = "text", ["text"] = new JsonObject { ["content"] = text } };
                if (secret is not null)
                {
                    var ms = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    var sign = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{ms}\n{secret}")));
                    url += (url.Contains('?') ? "&" : "?") + $"timestamp={ms}&sign={Uri.EscapeDataString(sign)}";
                }
                break;
            case WebhookFormats.WeCom:
                body = new JsonObject { ["msgtype"] = "text", ["text"] = new JsonObject { ["content"] = text } };
                break;
            case WebhookFormats.Slack:
                body = new JsonObject { ["text"] = text };
                break;
            default:
                body = new JsonObject { ["content"] = text.Length > DiscordMaxChars ? text[..(DiscordMaxChars - 1)] + "…" : text };
                break;
        }
        return (url, body.ToJsonString(GatewayJson.Options));
    }

    /// <summary>
    /// The failure a 2xx answer still reports (the bot platforms answer 200 with an error code); null = delivered.
    /// <paramref name="snippet"/> is the start of the response body.
    /// </summary>
    public static string? Rejection(string format, string? snippet)
    {
        if (format is not (WebhookFormats.Feishu or WebhookFormats.DingTalk or WebhookFormats.WeCom) || string.IsNullOrWhiteSpace(snippet))
            return null;
        JsonObject? answer;
        try
        {
            answer = JsonNode.Parse(snippet) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (answer is null) return null;
        var (codeKey, messageKey) = format == WebhookFormats.Feishu ? ("code", "msg") : ("errcode", "errmsg");
        // Older Feishu bots answer { StatusCode, StatusMessage }.
        var code = AlertConditions.Num(answer[codeKey]) ?? (format == WebhookFormats.Feishu ? AlertConditions.Num(answer["StatusCode"]) : null);
        if (code is null or 0) return null;
        var message = AlertConditions.Str(answer[messageKey]) ?? AlertConditions.Str(answer["StatusMessage"]) ?? "";
        return $"{codeKey} {code.Value.ToString(CultureInfo.InvariantCulture)}: {message}".TrimEnd(' ', ':');
    }

    /// <summary>The plain-text message a bot posts: a title line, the alert's own message, then rule and time.</summary>
    public static string Text(JsonObject payload, string locale)
    {
        var en = locale == "en";
        var status = AlertConditions.Str(payload["status"]);
        var title = status switch
        {
            "resolved" => en ? "[Astra] Resolved" : "【Astra】告警恢复",
            "test" => en ? "[Astra] Test notification" : "【Astra】测试通知",
            _ when payload["repeat"] is JsonValue r && r.TryGetValue<bool>(out var repeat) && repeat => en ? "[Astra] Alert (still firing)" : "【Astra】告警（持续中）",
            _ => en ? "[Astra] Alert" : "【Astra】告警",
        };
        var lines = new List<string> { title, AlertConditions.Str(payload["message"]) ?? "" };
        if (AlertConditions.Str(payload["rule"]?["name"]) is { Length: > 0 } rule) lines.Add((en ? "Rule: " : "规则：") + rule);
        if (AlertConditions.Str(payload["occurredAt"]) is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            lines.Add((en ? "Time: " : "时间：") + TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        if (AlertConditions.Str(payload["astra"]?["url"]) is { Length: > 0 } url) lines.Add(url);
        return string.Join('\n', lines);
    }
}
