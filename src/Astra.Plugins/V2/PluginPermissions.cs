namespace Astra.Plugins.V2;

/// <summary>
/// The permissions a v2 plugin can ask for (design/plugin-v2 01 §6): a string <c>&lt;domain&gt;[.&lt;object&gt;][:&lt;action or scope&gt;]</c>.
/// A permission only decides which host calls a plugin may make and which extension points it may bind; there is no
/// "everything". <c>net:</c> names one https origin, or a setting the user fills in (<c>net:{config.endpoint}</c>).
/// </summary>
public static class PluginPermissions
{
    public const string Storage = "storage";
    public const string Secrets = "secrets";
    public const string Ui = "ui";
    public const string Notifications = "notifications";
    public const string Jobs = "jobs";
    public const string Events = "events";
    public const string RequestRead = "gateway.request:read";
    public const string RequestRewrite = "gateway.request:rewrite";
    public const string RequestReject = "gateway.request:reject";
    public const string ResponseRead = "gateway.response:read";
    public const string ResponseRewrite = "gateway.response:rewrite";
    public const string UpstreamHeaders = "gateway.upstream:headers";
    public const string RequestsRead = "data.requests:read";
    public const string ProvidersRead = "data.providers:read";
    public const string ModelsRead = "data.models:read";
    public const string TokensRead = "data.tokens:read";
    public const string Providers = "providers";
    public const string Clients = "clients";
    public const string LegacyAuthImport = "legacy.auth:import";
    public const string NetPrefix = "net:";

    /// <summary>Every fixed permission (net: ones are open-ended).</summary>
    public static readonly IReadOnlyList<string> Fixed =
    [
        Storage, Secrets, Ui, Notifications, Jobs, Events, RequestRead, RequestRewrite, RequestReject, ResponseRead,
        ResponseRewrite, UpstreamHeaders, RequestsRead, ProvidersRead, ModelsRead, TokensRead, Providers, Clients, LegacyAuthImport,
    ];

    /// <summary>The ones the plugin page marks as high-risk: they see or change the conversation, or reach the network.</summary>
    public static bool IsSensitive(string permission) =>
        permission is RequestRewrite or RequestRead or ResponseRead or ResponseRewrite or RequestReject or Providers or LegacyAuthImport
        || permission.StartsWith(NetPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="granted"/> covers <paramref name="needed"/>: the permission itself, or one that includes it
    /// (rewriting or rejecting a request includes reading it; rewriting a response includes reading it).
    /// </summary>
    public static bool Holds(IEnumerable<string> granted, string needed) => granted.Any(g => g == needed || needed switch
    {
        RequestRead => g is RequestRewrite or RequestReject,
        ResponseRead => g is ResponseRewrite,
        _ => false,
    });

    /// <summary>Why <paramref name="permission"/> is not one a manifest may name; null when it is.</summary>
    public static string? Problem(string permission)
    {
        if (Fixed.Contains(permission)) return null;
        if (!permission.StartsWith(NetPrefix, StringComparison.Ordinal)) return $"unknown permission \"{permission}\"";
        var target = permission[NetPrefix.Length..];
        if (IsConfigPlaceholder(target, out _)) return null;
        return Origin(target) is null ? $"\"{permission}\": net: takes one https origin (https://host[:port]) or {{config.<setting>}}" : null;
    }

    /// <summary>"{config.endpoint}" → endpoint.</summary>
    public static bool IsConfigPlaceholder(string target, out string setting)
    {
        setting = "";
        if (target.Length < 10 || !target.StartsWith("{config.", StringComparison.Ordinal) || !target.EndsWith('}')) return false;
        setting = target[8..^1];
        return setting.Length > 0 && setting.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    }

    /// <summary>The origin a URL or origin text names (scheme://host[:port], lower case), or null when it is not https.</summary>
    public static string? Origin(string text, bool allowLoopbackHttp = false)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return null;
        if (!string.IsNullOrEmpty(u.UserInfo)) return null;
        return u.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    /// <summary>
    /// Whether <paramref name="granted"/> lets a plugin reach <paramref name="url"/>: an exact origin, or a
    /// <c>{config.x}</c> placeholder whose setting currently holds that origin.
    /// </summary>
    public static bool AllowsUrl(IEnumerable<string> granted, string url, Func<string, string?> setting, bool dev)
    {
        var origin = Origin(url, allowLoopbackHttp: dev);
        if (origin is null) return false;
        foreach (var p in granted)
        {
            if (!p.StartsWith(NetPrefix, StringComparison.Ordinal)) continue;
            var target = p[NetPrefix.Length..];
            var allowed = IsConfigPlaceholder(target, out var name) ? setting(name) is { } v ? Origin(v, dev) : null : Origin(target, dev);
            if (allowed == origin) return true;
        }
        return false;
    }
}
