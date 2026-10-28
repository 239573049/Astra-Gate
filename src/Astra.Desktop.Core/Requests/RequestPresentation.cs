using Astra.Api.Contracts;
using Astra.Core.Routing;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Core.Requests;

/// <summary>How a status reads in a badge (web <c>StatusBadge</c>): pending = accent + spinner, success = green, cancelled = orange, anything else = red.</summary>
public enum RequestStatusKind
{
    /// <summary>In flight.</summary>
    Pending,

    /// <summary>Finished fine.</summary>
    Success,

    /// <summary>The client went away.</summary>
    Cancelled,

    /// <summary>Failed, blocked, upstream or gateway error.</summary>
    Failed,
}

/// <summary>A status badge's content.</summary>
/// <param name="Kind">Tone and icon.</param>
/// <param name="Label">The status word (<c>status.&lt;id&gt;</c>).</param>
/// <param name="Title">Tooltip: "HTTP 502 · hint", or the hint alone; null when there is none.</param>
public sealed record StatusBadgeInfo(RequestStatusKind Kind, string Label, string? Title);

/// <summary>The returned-model line under a model name (web <c>ResponseModelTag</c>).</summary>
/// <param name="Comparable">There is both a returned model and one that was sent.</param>
/// <param name="Matches">They are the same.</param>
/// <param name="Text">The line (labeled "返回：x" in the list, the bare value in the detail).</param>
/// <param name="Title">Tooltip.</param>
/// <param name="MatchWord">"一致" / "不一致" when comparable, else null.</param>
public sealed record ResponseModelInfo(bool Comparable, bool Matches, string Text, string Title, string? MatchWord);

/// <summary>The protocol line (web <c>ProtocolTag</c>): one protocol, or a conversion inbound → upstream.</summary>
/// <param name="Inbound">The client's protocol.</param>
/// <param name="Upstream">The upstream's protocol when it differs and the call was not passed through; else null.</param>
public sealed record ProtocolInfo(string Inbound, string? Upstream);

/// <summary>The smart-routing badge (web <c>RoutingBadge</c>).</summary>
/// <param name="Ok">Green (the judge's way) or orange (fallback / moved).</param>
/// <param name="Text">"路由: …" text.</param>
/// <param name="Title">Tooltip.</param>
public sealed record RoutingBadgeInfo(bool Ok, string Text, string Title);

/// <summary>The two cache cells of a list row (web <c>CacheUsageCell</c> / <c>TokenTotalsCell</c>).</summary>
/// <param name="Empty">Nothing to show: a dash, dimmed.</param>
/// <param name="EmptyTitle">Why it is a dash.</param>
/// <param name="First">First line.</param>
/// <param name="FirstTitle">Its tooltip.</param>
/// <param name="Second">Second line (cache write, or reasoning), or null.</param>
/// <param name="SecondTitle">Its tooltip.</param>
public sealed record TwoLineCell(bool Empty, string? EmptyTitle, string First, string? FirstTitle, string? Second, string? SecondTitle);

/// <summary>
/// The text decisions of <c>web/src/pages/RequestsPage.tsx</c> and <c>web/src/components/TokenUsage.tsx</c>, free of any view: what a badge
/// says, which tone, which tooltip. Texts come through <see cref="TraceText"/> (web <c>t</c>; pages pass <see cref="TraceTexts.FromLoc"/>).
/// </summary>
public static class RequestPresentation
{
    /// <summary>Client names (web <c>CLIENT_META</c>, which the web keeps next to the icons).</summary>
    private static readonly Dictionary<string, string> ClientNames = new(StringComparer.Ordinal)
    {
        ["codex"] = "Codex", ["claude-code"] = "Claude Code", ["gemini-cli"] = "Gemini CLI", ["opencode"] = "OpenCode",
        ["claude-desktop"] = "Claude Desktop", ["grok-build"] = "Grok Build", ["pi"] = "Pi", ["hermes-agent"] = "Hermes Agent",
        ["minimax-code"] = "MiniMax Code", ["copilot-cli"] = "Copilot CLI", ["vscode-copilot"] = "VS Code Copilot", ["crush"] = "Crush",
        ["qwen-code"] = "Qwen Code", ["droid"] = "Droid", ["kimi-code"] = "Kimi Code", ["zed"] = "Zed", ["vscode-insiders"] = "VS Code Insiders",
        ["vscodium"] = "VSCodium", ["omp"] = "omp (oh-my-pi)", ["mimo-code"] = "MiMo Code", ["deepseek-harness"] = "DeepSeek Harness",
        ["workbuddy"] = "WorkBuddy", ["workbuddy-ai"] = "WorkBuddy AI", ["muse-code"] = "Muse Code", ["nextcowork"] = "NextCoWork",
        ["paseo"] = "Paseo", ["zcode"] = "ZCode", ["tkcode"] = "TKCode",
    };

    /// <summary>The settings categories the privacy hits of a detail are named by (web <c>PRIVACY_CATEGORY_KEY</c>).</summary>
    private static readonly string[] PrivacyCategories = ["api_key", "aws_key", "jwt", "private_key", "email", "phone", "intranet", "credit_card", "national_id", "credential", "custom"];

    private static readonly string[] PrivacyActions = ["warn", "block", "redact"];

    private static readonly string[] RouteSources = ["judge", "pin", "turn", "fallback", "breaker"];

    private static readonly string[] RouteModes = ["session", "turn"];

    private static readonly Dictionary<string, string> TokenTypeKeys = new(StringComparer.Ordinal)
    {
        ["input"] = "usage.input", ["cache_read"] = "usage.cacheRead", ["cache_write_5m"] = "usage.cacheWrite5m", ["cache_write_30m"] = "usage.cacheWrite30m", ["cache_write_1h"] = "usage.cacheWrite1h", ["cache_write_12h"] = "usage.cacheWrite12h",
        ["output"] = "usage.output", ["reasoning"] = "usage.reasoning", ["input_audio"] = "usage.inputAudio", ["output_audio"] = "usage.outputAudio",
        ["input_image"] = "usage.inputImage", ["output_image"] = "usage.outputImage",
    };

    /// <summary>The client's display name; a plugin client shows the name the server reports, else its plugin id (web <c>clientName</c>).</summary>
    public static string ClientName(string kind, string? fallback = null)
        => ClientNames.TryGetValue(kind, out var name) ? name
            : !string.IsNullOrEmpty(fallback) ? fallback
            : kind.StartsWith("plugin-", StringComparison.Ordinal) ? kind["plugin-".Length..] : kind;

    /// <summary>Client name of a row; a request authenticated by a bare token (no client suffix) is a direct call (web <c>requestClientName</c>).</summary>
    public static string RequestClientName(string? clientKind, string? tokenId, TraceText t)
        => !string.IsNullOrEmpty(clientKind) ? ClientName(clientKind) : !string.IsNullOrEmpty(tokenId) ? t("requests.direct") : TraceFormat.Dash;

    /// <summary>True for a status that deserves the "view error" affordance: anything but success, cancelled and pending.</summary>
    public static bool IsFailed(string status) => status is not ("success" or "client_cancelled" or "pending");

    /// <summary>A status badge's tone, label and tooltip (web <c>StatusBadge</c>).</summary>
    public static StatusBadgeInfo Status(string status, int? http, string? hint, TraceText t)
    {
        if (status == "pending") return new(RequestStatusKind.Pending, t("status.pending"), hint);
        var kind = status == "success" ? RequestStatusKind.Success : status == "client_cancelled" ? RequestStatusKind.Cancelled : RequestStatusKind.Failed;
        var parts = new List<string>();
        if (http is > 0) parts.Add($"HTTP {http}");
        if (!string.IsNullOrEmpty(hint)) parts.Add(hint);
        return new(kind, t("status." + status), parts.Count == 0 ? null : string.Join(" · ", parts));
    }

    /// <summary>The reasoning config the client explicitly set; empty = nothing recorded (web <c>reasoningParts</c>).</summary>
    public static List<string> ReasoningParts(string? effort, string? mode, long? budget, TraceText t)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(effort)) parts.Add(effort);
        if (!string.IsNullOrEmpty(mode)) parts.Add(mode);
        if (budget is { } b) parts.Add(t("requests.reasoning.budget", ("tokens", b.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        return parts;
    }

    /// <summary>The model the gateway sent in place of the requested one, or null when there was none (web <c>MappedModel</c>).</summary>
    public static string? MappedModel(string? requested, string? upstream)
        => string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(upstream) || upstream == requested ? null : upstream;

    /// <summary>Tooltip of <see cref="MappedModel"/>.</summary>
    public static string MappedModelTitle(string upstream, TraceText t) => t("requests.mappedModel.hint", ("model", upstream));

    /// <summary>The returned-model line (web <c>ResponseModelTag</c>); <paramref name="labeled"/> is the list's "返回：x".</summary>
    public static ResponseModelInfo ResponseModel(string? requested, string? upstream, string? response, string status, bool labeled, TraceText t)
    {
        // Compared with what the gateway sent: a mapping is the user's own setting (shown beside the requested model), a
        // response naming another model is the upstream's doing.
        var sent = !string.IsNullOrEmpty(upstream) ? upstream : requested;
        var comparable = !string.IsNullOrEmpty(response) && !string.IsNullOrEmpty(sent);
        var matches = comparable && response == sent;
        // The returned model is recorded when the request finishes, so an in-flight one has none yet.
        var pending = string.IsNullOrEmpty(response) && status == "pending";
        var value = !string.IsNullOrEmpty(response) ? response : t(pending ? "requests.modelPending" : "requests.modelNotRecorded");
        var title = !string.IsNullOrEmpty(response)
            ? t("requests.modelComparison", ("requested", requested ?? TraceFormat.Dash), ("upstream", upstream ?? TraceFormat.Dash), ("returned", response))
            : t(pending ? "requests.modelPending.hint" : "requests.modelNotRecorded.hint");
        return new(comparable, matches, labeled ? t("requests.returnedModel", ("model", value)) : value, title,
            comparable ? t(matches ? "requests.modelMatch" : "requests.modelMismatch") : null);
    }

    /// <summary>The protocol line (web <c>ProtocolTag</c>).</summary>
    public static ProtocolInfo Protocol(string inbound, string? upstream, bool passthrough)
        => passthrough || string.IsNullOrEmpty(upstream) || upstream == inbound ? new(inbound, null) : new(inbound, upstream);

    private static string RouteSourceText(string source, TraceText t) => RouteSources.Contains(source) ? t("traces.route.source." + source) : source;

    /// <summary>Where a routing decision came from, in the words the trace view uses (web <c>routeSourceText</c>).</summary>
    public static string RouteSource(string source, TraceText t) => RouteSourceText(source, t);

    /// <summary>How a routing decision was made: per session or per turn (web <c>routeModeText</c>).</summary>
    public static string RouteMode(string mode, TraceText t) => RouteModes.Contains(mode) ? t("traces.route.mode." + mode) : mode;

    /// <summary>Why the caller could not use the grade decided first, in the smart-routing page's words (web <c>unusableText</c>).</summary>
    public static string Unusable(string? reason, TraceText t, Func<string, bool>? has = null)
    {
        has ??= Loc.Has;
        var key = "smartRouting.unusable." + reason;
        return !string.IsNullOrEmpty(reason) && has(key) ? t(key) : t("smartRouting.unusable.unknown");
    }

    /// <summary>Why the fallback grade was used (web <c>routeReasonText</c>).</summary>
    public static string RouteReason(string reason, TraceText t, Func<string, bool>? has = null)
    {
        has ??= Loc.Has;
        var key = "smartRouting.failure." + reason;
        return has(key) ? t(key) : reason;
    }

    /// <summary>
    /// How smart routing sent a request to the virtual model: green when it went the judge's way (judged now, or a session's / turn's
    /// judged grade reused), orange when it did not (web <c>RoutingBadge</c>). Null for every other request.
    /// </summary>
    public static RoutingBadgeInfo? Routing(RoutingReport? routing, TraceText t, Func<string, bool>? has = null)
    {
        if (routing is null) return null;
        var vars = new (string, object?)[] { ("grade", routing.Grade), ("model", routing.Model) };
        bool ok;
        string text;
        string title;
        if (!string.IsNullOrEmpty(routing.FallbackReason))
        {
            var reason = RouteReason(routing.FallbackReason, t, has);
            ok = false;
            text = t("requests.routing.fallback", [.. vars, ("reason", reason)]);
            title = t("requests.routing.hint.fallback", [.. vars, ("reason", reason)]);
        }
        else if (!string.IsNullOrEmpty(routing.AdjustedFrom))
        {
            ok = false;
            text = t("requests.routing.moved", [.. vars, ("from", routing.AdjustedFrom)]);
            title = t("requests.routing.hint.moved", [.. vars, ("from", routing.AdjustedFrom), ("reason", Unusable(routing.AdjustedReason, t, has))]);
        }
        else if (routing.Source == "judge")
        {
            ok = true;
            text = routing.Grade;
            title = t("requests.routing.hint.judge", [.. vars, ("percent", (long)TraceFormat.JsRound((routing.Confidence ?? 0) * 100))]);
        }
        else
        {
            ok = true;
            text = $"{routing.Grade} · {RouteSourceText(routing.Source, t)}";
            title = t("requests.routing.hint.reused", [.. vars, ("source", RouteSourceText(routing.Source, t))]);
        }
        return new(ok, $"{t("traces.node.routing")}: {text}", $"{t("traces.node.routing")}: {title}");
    }

    /// <summary>The cache cell: hits and writes, or a dash when the upstream reported no usage / no cache (web <c>CacheUsageCell</c>).</summary>
    public static TwoLineCell CacheCell(UsageSplit u, TraceText t)
    {
        if (!u.Reported || (u.Read == 0 && u.Write == 0))
            return new(true, t(u.Reported ? "usage.noCache" : "usage.missing"), TraceFormat.Dash, null, null, null);
        var hit = $"{t("usage.hitShort")} {TraceFormat.Tokens(u.Read, true)}" + (u.HitRatio is not null ? " " + TraceFormat.Percent(u.HitRatio) : "");
        return new(false, null, hit, t("usage.hitTooltip", ("read", TraceFormat.Tokens(u.Read)), ("total", TraceFormat.Tokens(u.Input))),
            $"{t("usage.writeShort")} {TraceFormat.Tokens(u.Write, true)}", t("usage.writeTooltip", ("tokens", TraceFormat.Tokens(u.Write))));
    }

    /// <summary>The input / output cell, with the reasoning count underneath when there is one (web <c>TokenTotalsCell</c>).</summary>
    public static TwoLineCell TokensCell(UsageSplit u, TraceText t)
    {
        if (!u.Reported) return new(true, t("usage.missing"), TraceFormat.Dash, null, null, null);
        return new(false, null, $"{TraceFormat.Tokens(u.Input, true)} / {TraceFormat.Tokens(u.Output, true)}", t("usage.totalsHint"),
            u.Reasoning > 0 ? $"{t("usage.reasoningShort")} {TraceFormat.Tokens(u.Reasoning, true)}" : null, null);
    }

    /// <summary>The label of a billed token type, or null when it is not a known one (web <c>TOKEN_LABELS</c>).</summary>
    public static string? TokenTypeLabel(string type, TraceText t) => TokenTypeKeys.TryGetValue(type, out var key) ? t(key) : null;

    /// <summary>The i18n key of a privacy category, or null (web <c>PRIVACY_CATEGORY_KEY</c>).</summary>
    public static string? PrivacyCategoryKey(string category) => PrivacyCategories.Contains(category) ? "settings.privacy.category." + category : null;

    /// <summary>The i18n key of a privacy action, or null (web <c>PRIVACY_ACTION_KEY</c>).</summary>
    public static string? PrivacyActionKey(string action) => PrivacyActions.Contains(action) ? "settings.privacy.action." + action : null;

    /// <summary>The detail's origin line (web <c>requests.col.origin</c> KV).</summary>
    public static string Origin(bool remote, string? clientIp, TraceText t)
        => remote
            ? !string.IsNullOrEmpty(clientIp) ? t("requests.origin.remoteFrom", ("ip", clientIp)) : t("requests.origin.remote")
            : clientIp ?? t("requests.origin.local");

    /// <summary>The live "waited so far" of a request still waiting on its first token (web <c>Elapsed</c>).</summary>
    public static string Elapsed(DateTimeOffset startedAt, DateTimeOffset now) => TraceFormat.Ms(Math.Max(0, (now - startedAt).TotalMilliseconds));

    /// <summary>The detail sheet's title (web: <c>r?.requestedModel ?? t('requests.detail.title')</c>).</summary>
    public static string DetailTitle(string? requestedModel, TraceText t) => requestedModel ?? t("requests.detail.title");

    /// <summary>The pricing badge of the billing section (web <c>PricingSourceBadge</c>): tone name and text.</summary>
    public static (string Tone, string Text) PricingSource(string? source, string? priceKey, TraceText t) => source switch
    {
        "provider_override" => ("orange", t("pricingSource.override")),
        "provider_price" => ("accent", t("pricingSource.provider", ("key", priceKey ?? ""))),
        "system_default" => ("neutral", t("pricingSource.default")),
        _ => ("red", t("pricingSource.none")),
    };

    /// <summary>
    /// Whether a captured body is cut: the detail carries only its first <c>limit</c> bytes, a whole part (up to 8 MB) is fetched on
    /// request and shown as plain text (web <c>RequestBodyPanel</c>).
    /// </summary>
    public static bool IsTruncated(long? size, long? limit) => size is { } s && limit is { } l && s > l;
}
