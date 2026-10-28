using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Privacy;

/// <summary>
/// Names of the privacy guard's categories and actions (web/src/components/PrivacyMeta.tsx): the built-in categories and the three
/// actions have catalog keys; an unknown category (a removed custom rule) shows its raw id.
/// </summary>
public static class PrivacyMeta
{
    /// <summary>The built-in categories, in the web's key table order.</summary>
    public static readonly string[] Categories =
        ["api_key", "aws_key", "jwt", "private_key", "email", "phone", "intranet", "credit_card", "national_id", "credential", "custom"];

    /// <summary>Every action a category may have, "off" first (the policy selects).</summary>
    public static readonly string[] Actions = ["off", "warn", "block", "redact"];

    /// <summary>The actions that detect something (log filters, hit badges).</summary>
    public static readonly string[] ActiveActions = ["block", "redact", "warn"];

    /// <summary>Localized category name; unknown categories fall back to the raw id.</summary>
    public static string CategoryLabel(string category) => Categories.Contains(category) ? Loc.T("settings.privacy.category." + category) : category;

    /// <summary>Localized action name ("off" included).</summary>
    public static string ActionLabel(string action) => Loc.T("settings.privacy.action." + (Actions.Contains(action) ? action : "warn"));

    /// <summary>The badge tone of an action: block red, redact orange, anything else neutral (web <c>PrivacyActionBadge</c>).</summary>
    public static string ActionTone(string action) => action switch { "block" => "red", "redact" => "orange", _ => "neutral" };
}
