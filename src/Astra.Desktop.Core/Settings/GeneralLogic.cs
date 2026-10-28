using Astra.Api.Contracts;
using Astra.Core;

namespace Astra.Desktop.Core.Settings;

/// <summary>The small decisions of Settings › General that are not a form of their own (web SettingsPage <c>GeneralSettings</c>).</summary>
public static class GeneralLogic
{
    /// <summary>Zones offered for token limits besides the server's own (any IANA id the server knows is accepted).</summary>
    public static readonly string[] UsageTimeZones =
    [
        "UTC", "Asia/Shanghai", "Asia/Hong_Kong", "Asia/Taipei", "Asia/Tokyo", "Asia/Singapore",
        "Europe/London", "Europe/Berlin", "America/New_York", "America/Los_Angeles",
    ];

    /// <summary>The zone list: the offered ones plus the saved one when it is not among them.</summary>
    public static IReadOnlyList<string> ZoneOptions(SettingsDto s)
        => s.UsageTimeZone is { Length: > 0 } saved ? UsageTimeZones.Concat([saved]).Distinct().ToList() : UsageTimeZones;

    /// <summary>The upstream request compressions, in the web's order.</summary>
    public static readonly string[] Compressions = ["default", "zstd", "gzip", "br", "deflate", "none"];

    /// <summary>The compression option's label (the codecs are names; default / none are translated).</summary>
    public static string CompressionLabel(string value) => value switch
    {
        "default" => I18n.Loc.T("settings.requestCompression.default"),
        "none" => I18n.Loc.T("settings.requestCompression.none"),
        "br" => "Brotli (br)",
        _ => value,
    };

    /// <summary>The three reasoning-effort levels.</summary>
    public static readonly string[] Efforts = ["low", "medium", "high"];

    /// <summary>The budget of one level.</summary>
    public static int Budget(EffortBudgets b, string effort) => effort switch { "low" => b.Low, "medium" => b.Medium, _ => b.High };

    /// <summary>The body that changes one level (the whole object goes, like the web): an empty field keeps the old value.</summary>
    public static System.Text.Json.Nodes.JsonObject EffortPatch(EffortBudgets current, string effort, int? value)
    {
        int Pick(string k) => k == effort ? value ?? Budget(current, k) : Budget(current, k);
        var budgets = JsonPatch.Of(("low", Pick("low")), ("medium", Pick("medium")), ("high", Pick("high")));
        return JsonPatch.Of(("effortBudgets", budgets));
    }

    /// <summary>The retention switch: on keeps 90 days, off keeps forever (null).</summary>
    public static int? RetentionFor(bool on) => on ? 90 : null;

    /// <summary>The text of a numeric field's value with the unit the web shows after it.</summary>
    public static string WithUnit(decimal value, string suffix) => $"{value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}{suffix}";

    /// <summary>The public gateway URL of a connected tunnel (<c>…/v1</c>), or null.</summary>
    public static string? RemoteUrl(TunnelStatusDto? tunnel)
        => tunnel is { State: "connected", PublicUrl: { Length: > 0 } url } ? url + "/v1" : null;

    /// <summary>How long a number field waits after the last change before saving (web <c>DeferredNumber</c>).</summary>
    public static readonly TimeSpan NumberCommitDelay = TimeSpan.FromMilliseconds(600);
}
