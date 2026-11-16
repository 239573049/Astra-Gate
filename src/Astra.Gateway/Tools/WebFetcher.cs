using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Astra.Core.Tools;

namespace Astra.Gateway.Tools;

/// <summary>
/// The address check of <c>web_fetch</c>: the URL is a model's (or a token holder's), so Astra reads only public addresses — never a
/// service only this machine or the user's network reaches (magpie's <c>publicClient</c>, MIT, here with an explicit allowance).
/// </summary>
public static class SsrfGuard
{
    /// <summary>Whether <paramref name="ip"/> is loopback, private, link-local, shared, multicast, unspecified or otherwise not a public address.</summary>
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.None)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0 || b[0] == 10 || b[0] >= 224
                   || (b[0] == 100 && b[1] is >= 64 and <= 127)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                   || (b[0] == 198 && b[1] is 18 or 19);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal) return true;
            // NAT64 (64:ff9b::/96) carries an IPv4 address in its last four bytes.
            if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
                return IsBlocked(new IPAddress(b[12..16]));
            return false;
        }
        return true;
    }
}

/// <summary>The page <c>web_fetch</c> read, in the part asked for.</summary>
public sealed record FetchedPage(string Url, string? Title, string Text, int Offset, int? NextOffset, int Total, string ContentType);

/// <summary>A fetch that cannot be done: said to the model as the tool's error, never with an internal detail.</summary>
public sealed class FetchRefusedException(string message) : Exception(message);

/// <summary>
/// <c>web_fetch</c>: a page by its URL as readable text (ported from magpie's search_fetch.go, MIT). http and https only; every hop of
/// every redirect and every connect is checked against <see cref="SsrfGuard"/> unless <see cref="WebFetchSettings.AllowPrivate"/>; a page is read for at most
/// <see cref="WebFetchSettings.TimeoutSeconds"/> and <see cref="WebFetchSettings.MaxBytes"/>; HTML comes back as Markdown-like text, other text as
/// it is, anything else is refused. A long page comes in parts of <c>max_chars</c> characters, <c>offset</c> naming where the next starts.
/// </summary>
public sealed partial class WebFetcher(ToolsSettingsService settings)
{
    public const int MaxRedirects = 5;

    /// <summary>Tests swap the transport; the connect-time check lives in the default one.</summary>
    public Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>Tests swap DNS.</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; set; } = (host, ct) => Dns.GetHostAddressesAsync(host, ct);

    private HttpClient? _client;

    public async Task<FetchedPage> FetchAsync(string url, int? maxChars, int offset, CancellationToken ct)
    {
        var cfg = settings.Current.Fetch;
        var chars = Math.Clamp(maxChars ?? cfg.MaxChars, 500, 100_000);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) throw new FetchRefusedException("the url is not an absolute http(s) address");
        for (var hop = 0; ; hop++)
        {
            await CheckAsync(uri, cfg, limit.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; astra-fetch)");
            request.Headers.Accept.ParseAdd("text/html,text/plain,application/json,application/xml;q=0.9,*/*;q=0.1");
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            }
            catch (HttpRequestException ex) when (ex.InnerException is FetchRefusedException refused) { throw refused; }
            catch (HttpRequestException ex) { throw new FetchRefusedException("could not reach the page: " + ex.Message); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new FetchRefusedException("the page took too long"); }
            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } next)
                {
                    if (hop >= MaxRedirects) throw new FetchRefusedException("too many redirects");
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    continue;
                }
                if (status >= 400) throw new FetchRefusedException($"the page answered {status}");
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                var html = type is "text/html" or "application/xhtml+xml";
                if (!html && !type.StartsWith("text/", StringComparison.Ordinal) && type is not ("application/json" or "application/xml" or "application/rss+xml" or "application/atom+xml" or "application/javascript" or ""))
                    throw new FetchRefusedException($"a {type} page cannot be read as text");
                var raw = await ReadAsync(response, cfg.MaxBytes, limit.Token);
                var encoding = Encoding.UTF8;
                try { if (response.Content.Headers.ContentType?.CharSet is { Length: > 0 } cs) encoding = Encoding.GetEncoding(cs.Trim('"')); }
                catch (ArgumentException) { /* an unknown charset: UTF-8 */ }
                var text = encoding.GetString(raw);
                string? title = null;
                if (html || (type.Length == 0 && LooksLikeHtml(text))) (text, title) = HtmlToText(text);
                var total = text.Length;
                var from = Math.Clamp(offset, 0, total);
                var take = Math.Min(chars, total - from);
                if (take > 0 && from + take < total && char.IsHighSurrogate(text[from + take - 1])) take--;
                return new FetchedPage(uri.AbsoluteUri, title, text.Substring(from, take), from, from + take < total ? from + take : null, total, type);
            }
        }
    }

    private async Task CheckAsync(Uri uri, WebFetchSettings cfg, CancellationToken ct)
    {
        if (uri.Scheme is not ("http" or "https")) throw new FetchRefusedException("only http and https addresses can be fetched");
        if (uri.UserInfo.Length > 0) throw new FetchRefusedException("an address with a user name or password is refused");
        if (cfg.AllowPrivate) return;
        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal)) addresses = [literal];
        else
        {
            try { addresses = await Resolve(uri.DnsSafeHost, ct); }
            catch (SocketException) { throw new FetchRefusedException("the host does not resolve"); }
        }
        if (addresses.Length == 0 || addresses.Any(SsrfGuard.IsBlocked))
            throw new FetchRefusedException("that address is private or local; Astra only fetches public pages (allow it in Tools settings)");
    }

    private HttpClient Client() => _client ??= new HttpClient(HandlerFactory?.Invoke() ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        // The address actually connected to is checked again (DNS may answer differently the second time).
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (!settings.Current.Fetch.AllowPrivate && addresses.Any(SsrfGuard.IsBlocked))
                throw new FetchRefusedException("that address is private or local");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < max && (read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, max - buffer.Length)), ct)) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }

    private static bool LooksLikeHtml(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, 512));
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.Contains("<!doctype html", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- HTML as text

    [GeneratedRegex(@"<(script|style|noscript|template|svg|head)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Dropped();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)')[^>]*>(.*?)</a\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();

    [GeneratedRegex(@"<h([1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<(br|hr)\b[^>]*>|</?(p|div|section|article|header|footer|main|nav|ul|ol|table|tr|blockquote|pre|h[1-6]|li|form|figure)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\r]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex Blank();

    /// <summary>An HTML page as Markdown-like text (headings, list items, links) and its title.</summary>
    public static (string Text, string? Title) HtmlToText(string html)
    {
        var title = TitleTag().Match(html) is { Success: true } t ? Collapse(WebUtility.HtmlDecode(AnyTag().Replace(t.Groups[1].Value, ""))) : null;
        var s = Comments().Replace(html, "");
        s = Dropped().Replace(s, "");
        s = Anchor().Replace(s, m =>
        {
            var href = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var label = Collapse(WebUtility.HtmlDecode(AnyTag().Replace(m.Groups[3].Value, "")));
            if (label.Length == 0 || href.StartsWith('#') || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return label;
            return $"[{label}]({WebUtility.HtmlDecode(href)})";
        });
        s = Heading().Replace(s, m => "\n\n" + new string('#', m.Groups[1].Value[0] - '0') + " ");
        s = ListItem().Replace(s, "\n- ");
        s = Breaks().Replace(s, "\n");
        s = AnyTag().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        var lines = s.Split('\n').Select(l => Spaces().Replace(l, " ").Trim());
        s = Blank().Replace(string.Join('\n', lines), "\n\n").Trim();
        return (s, string.IsNullOrEmpty(title) ? null : title);
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
