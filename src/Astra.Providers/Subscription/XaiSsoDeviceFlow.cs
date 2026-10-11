using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Astra.Providers.Subscription;

/// <summary>
/// Converts a Grok Web SSO cookie into Grok Build OAuth tokens: the cookie signs in to accounts.x.ai and approves a
/// device-code authorization on the user's behalf (plan §5.4 account import). Ported from routin-fax
/// <c>XaiSsoDeviceFlow</c> / sub2api <c>xai/sso_device.go</c>. Failures surface as <see cref="OAuthProtocolException"/>
/// carrying a user-facing message, so the import endpoint reports them per entry.
/// </summary>
public static partial class XaiSsoDeviceFlow
{
    private const string AccountsUrl = "https://accounts.x.ai/";
    private const string AccountsOrigin = "https://accounts.x.ai";
    private const int MaxBodyBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxPoll = TimeSpan.FromSeconds(75);

    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Runs the conversion over the config's proxy (provider / account), else direct.</summary>
    public static async Task<OAuthClient.TokenResult> ConvertAsync(
        SubscriptionOAuthConfig config, string ssoToken, CancellationToken ct)
    {
        using var client = config.HttpProxy.Length > 0
            ? ProxiedHttpClients.Create(config.HttpProxy, Timeout)
            : new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
            }, disposeHandler: true) { Timeout = Timeout };
        return await ConvertAsync(client, config, ssoToken, null, ct);
    }

    /// <param name="client">Must not follow redirects or keep cookies itself (the flow does both by hand).</param>
    /// <param name="delay">Poll delay; tests pass a no-op.</param>
    public static async Task<OAuthClient.TokenResult> ConvertAsync(
        HttpClient client, SubscriptionOAuthConfig config, string ssoToken, Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken ct)
    {
        ssoToken = XaiSsoNormalizer.Normalize(ssoToken);
        if (ssoToken.Length == 0) throw Invalid();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var session = new Session(client);
        session.Cookies["sso"] = ssoToken;
        session.Cookies["sso-rw"] = ssoToken;
        try
        {
            return await RunAsync(session, config, delay ?? Task.Delay, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Fail("xAI SSO 换取超时，请稍后重试");
        }
    }

    private static async Task<OAuthClient.TokenResult> RunAsync(
        Session s, SubscriptionOAuthConfig config, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        // 1. 探测：cookie 失效时 accounts.x.ai 会 401 或重定向到登录 / 注册页。
        var probe = await s.SendAsync(HttpMethod.Get, AccountsUrl, null, null, ct);
        if (probe.Status == 401 || probe.FinalUrl.Contains("sign-in", StringComparison.OrdinalIgnoreCase)
                                || probe.FinalUrl.Contains("sign-up", StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        Require(probe, 200, 400, "校验 Grok SSO");

        // 2. 申请设备码（与普通设备码登录同一组 client_id / scope / 额外参数）。
        var start = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["scope"] = string.Join(' ', config.Scopes),
        };
        foreach (var (key, value) in config.ExtraDeviceParams) start[key] = value;
        var device = await s.SendAsync(HttpMethod.Post, config.DeviceCodeUrl, start, null, ct);
        Require(device, 200, 300, "发起 xAI 设备码授权");

        string deviceCode, userCode, verificationUri;
        int interval, expiresIn;
        try
        {
            using var doc = JsonDocument.Parse(device.Body);
            var root = doc.RootElement;
            deviceCode = Str(root, "device_code");
            userCode = Str(root, "user_code");
            verificationUri = Str(root, "verification_uri_complete");
            interval = Int(root, "interval");
            expiresIn = Int(root, "expires_in");
        }
        catch (JsonException)
        {
            throw Fail("xAI 设备码响应无法解析");
        }
        if (deviceCode.Length == 0 || userCode.Length == 0 || !IsTrusted(verificationUri))
            throw Fail("xAI 设备码响应不完整");
        if (interval <= 0) interval = 5;
        if (expiresIn <= 0) expiresIn = 1800;

        // 3. 打开授权页并提交 user_code，应落在 consent 页。
        Require(await s.SendAsync(HttpMethod.Get, verificationUri, null, null, ct), 200, 400, "打开 xAI 设备授权页");
        var authBase = new Uri(config.DeviceCodeUrl);
        var verifyUrl = new Uri(authBase, "/oauth2/device/verify").ToString();
        var approveUrl = new Uri(authBase, "/oauth2/device/approve").ToString();
        var verify = await s.SendAsync(HttpMethod.Post, verifyUrl, new() { ["user_code"] = userCode }, null, ct);
        Require(verify, 200, 400, "校验 xAI 设备码");
        if (!verify.FinalUrl.Contains("consent", StringComparison.OrdinalIgnoreCase))
            throw Fail($"xAI 设备码校验没有进入授权确认页{Snippet(verify.Body)}");

        // 4. 授权确认页是 HTML 表单：重放它的隐藏字段（CSRF 等）和浏览器表单提交的导航头，
        // 只发裸字段会被 xAI / Cloudflare 回 403。
        var action = FormAction(verify.Body);
        if (action is null || !IsTrusted(action)) action = approveUrl;
        var fields = HiddenFields(verify.Body);
        fields["user_code"] = userCode;
        fields["action"] = "allow";
        fields["principal_type"] = "User";
        fields.TryAdd("principal_id", "");
        var approve = await s.SendAsync(HttpMethod.Post, action, fields, new Dictionary<string, string>
        {
            ["Origin"] = AccountsOrigin,
            ["Referer"] = verify.FinalUrl,
            ["Upgrade-Insecure-Requests"] = "1",
            ["Sec-Fetch-Site"] = "cross-site",
            ["Sec-Fetch-Mode"] = "navigate",
            ["Sec-Fetch-Dest"] = "document",
            ["Sec-Fetch-User"] = "?1",
        }, ct);
        Require(approve, 200, 400, "批准 xAI 设备授权");
        if (!approve.FinalUrl.Contains("done", StringComparison.OrdinalIgnoreCase))
            throw Fail($"xAI 设备授权没有完成{Snippet(approve.Body)}");

        // 5. 轮询换令牌。
        return await PollAsync(s, config, deviceCode, TimeSpan.FromSeconds(interval),
            TimeSpan.FromSeconds(Math.Min(expiresIn, MaxPoll.TotalSeconds)), delay, ct);
    }

    private static async Task<OAuthClient.TokenResult> PollAsync(
        Session s, SubscriptionOAuthConfig config, string deviceCode, TimeSpan interval, TimeSpan budget,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct)
    {
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            await delay(interval, ct);
            var poll = await s.SendAsync(HttpMethod.Post, config.TokenUrl, new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = config.ClientId,
                ["device_code"] = deviceCode,
            }, null, ct);

            string access, refresh, idToken, scope, error, description;
            int expires;
            try
            {
                using var doc = JsonDocument.Parse(poll.Body);
                var root = doc.RootElement;
                access = Str(root, "access_token");
                refresh = Str(root, "refresh_token");
                idToken = Str(root, "id_token");
                scope = Str(root, "scope");
                error = Str(root, "error");
                description = Str(root, "error_description");
                expires = Int(root, "expires_in");
            }
            catch (JsonException)
            {
                throw Fail($"xAI 令牌响应无法解析{Snippet(poll.Body)}");
            }

            if (poll.Status is >= 200 and < 300 && access.Length > 0)
                return new OAuthClient.TokenResult(access, refresh.Length > 0 ? refresh : null, expires > 0 ? expires : 6 * 3600,
                    idToken.Length > 0 ? idToken : null, scope.Length > 0 ? scope : null);

            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "access_denied":
                case "expired_token":
                    throw Fail("xAI 设备授权被拒绝或已过期");
                default:
                    var detail = description.Length > 0 ? description : error.Length > 0 ? error : poll.Status.ToString();
                    throw Fail($"xAI 令牌轮询失败（{detail}）");
            }
        }
        throw Fail("xAI 设备授权轮询超时");
    }

    private sealed record Response(int Status, string FinalUrl, string Body);

    /// <summary>One conversion's HTTP session: manual redirects (host-checked) and a manual cookie jar.</summary>
    private sealed class Session(HttpClient client)
    {
        public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

        public async Task<Response> SendAsync(
            HttpMethod method, string url, Dictionary<string, string>? form, Dictionary<string, string>? headers, CancellationToken ct)
        {
            if (!IsTrusted(url)) throw Fail("xAI 授权地址不可信");
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(method, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/html;q=0.9, */*;q=0.8");
                request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (headers is not null)
                    foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
                if (Cookies.Count > 0)
                    request.Headers.TryAddWithoutValidation("Cookie",
                        string.Join("; ", Cookies.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}={c.Value}")));
                if (form is not null) request.Content = new FormUrlEncodedContent(form);

                using var response = await client.SendAsync(request, ct);
                Capture(response);
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > MaxBodyBytes) throw Fail("xAI 响应超过 2 MiB");
                var status = (int)response.StatusCode;
                var body = Encoding.UTF8.GetString(bytes);
                if (status is < 300 or > 399) return new Response(status, url, body);

                var location = response.Headers.Location?.ToString().Trim();
                if (string.IsNullOrEmpty(location)) throw Fail("xAI 重定向缺少 Location");
                if (!Uri.TryCreate(new Uri(url), location, out var next)) throw Fail("xAI 重定向地址无效");
                url = next.ToString();
                if (!IsTrusted(url)) throw Fail("xAI 重定向到了不可信的主机");
                if (response.StatusCode == HttpStatusCode.SeeOther
                    || (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found && method != HttpMethod.Get))
                {
                    method = HttpMethod.Get;
                    form = null;
                }
            }
            throw Fail("xAI 重定向次数过多");
        }

        private void Capture(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
            foreach (var raw in values)
            {
                var parts = raw.Split(';');
                var first = parts[0];
                var eq = first.IndexOf('=');
                if (eq <= 0) continue;
                var name = first[..eq].Trim();
                var value = first[(eq + 1)..].Trim();
                if (name.Length is 0 or > 128 || value.Length > 16384 || name.IndexOfAny(['\r', '\n', '\0']) >= 0
                    || value.IndexOfAny(['\r', '\n', '\0']) >= 0)
                    continue;
                var deleted = parts.Skip(1).Select(p => p.Trim())
                    .Any(p => p.StartsWith("Max-Age=", StringComparison.OrdinalIgnoreCase)
                              && int.TryParse(p["Max-Age=".Length..], out var age) && age <= 0);
                if (deleted) Cookies.Remove(name);
                else Cookies[name] = value;
            }
        }
    }

    /// <summary>https and an x.ai host: every hop of the flow must stay on xAI (SSRF / cookie-leak guard).</summary>
    public static bool IsTrusted(string? raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Host.Length == 0) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return host == "x.ai" || host.EndsWith(".x.ai", StringComparison.Ordinal);
    }

    [GeneratedRegex("<form[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FormTag();

    [GeneratedRegex("<input[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();

    [GeneratedRegex("""action\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ActionAttr();

    [GeneratedRegex("""type\s*=\s*["']hidden["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenAttr();

    [GeneratedRegex("""name\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex NameAttr();

    [GeneratedRegex("""value\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttr();

    /// <summary>The consent form's action as an absolute URL; null when the page has no usable form.</summary>
    internal static string? FormAction(string html)
    {
        var form = FormTag().Match(html);
        if (!form.Success) return null;
        var action = ActionAttr().Match(form.Value);
        if (!action.Success) return null;
        var value = WebUtility.HtmlDecode(action.Groups[1].Value.Trim());
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        return value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) ? AccountsOrigin + value : null;
    }

    /// <summary>Hidden inputs of the page (CSRF / session fields), HTML-decoded.</summary>
    internal static Dictionary<string, string> HiddenFields(string html)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in InputTag().Matches(html))
        {
            if (!HiddenAttr().IsMatch(input.Value)) continue;
            var name = NameAttr().Match(input.Value);
            if (!name.Success) continue;
            var value = ValueAttr().Match(input.Value);
            fields[WebUtility.HtmlDecode(name.Groups[1].Value.Trim())] = value.Success ? WebUtility.HtmlDecode(value.Groups[1].Value) : "";
        }
        return fields;
    }

    private static void Require(Response r, int min, int below, string what)
    {
        if (r.Status >= min && r.Status < below) return;
        throw Fail($"{what}失败：HTTP {r.Status}{Snippet(r.Body)}");
    }

    /// <summary>A short, single-line excerpt so a failed import shows whether Cloudflare or xAI refused.</summary>
    private static string Snippet(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var text = (body.Length > 200 ? body[..200] : body).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $" | {text}";
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()?.Trim() ?? "" : "";

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var v) ? v : 0;

    private static OAuthProtocolException Invalid() => Fail("Grok SSO cookie 已失效或不正确，请重新获取");

    private static OAuthProtocolException Fail(string message) => new(message, null);
}
