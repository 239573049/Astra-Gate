using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Core.Privacy;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Privacy;

/// <summary>
/// Editing the privacy guard's policy (web/src/components/PrivacyGuardSettings.tsx): every change builds a complete new
/// <see cref="PrivacySettings"/> from the current one (the server takes the whole policy with PUT /api/privacy, so the page never
/// sends a half policy), plus the rule-dialog helpers.
/// </summary>
public static class PrivacyPolicyLogic
{
    /// <summary>A deep copy (<see cref="PrivacySettings.Clone"/> goes through the storage JSON options, which are not the API's).</summary>
    public static PrivacySettings Copy(PrivacySettings s) => new()
    {
        Enabled = s.Enabled,
        DryRun = s.DryRun,
        RestoreResponses = s.RestoreResponses,
        RecordSamples = s.RecordSamples,
        RecordFullSamples = s.RecordFullSamples,
        DefaultAction = s.DefaultAction,
        CategoryActions = new Dictionary<string, string>(s.CategoryActions),
        ClientDefaults = new Dictionary<string, string>(s.ClientDefaults),
        CustomRules = s.CustomRules.Select(r => new PrivacyCustomRule { Id = r.Id, Name = r.Name, Pattern = r.Pattern, Enabled = r.Enabled }).ToList(),
    };

    /// <summary>The policy with one change applied to a copy.</summary>
    public static PrivacySettings With(PrivacySettings s, Action<PrivacySettings> change)
    {
        var next = Copy(s);
        change(next);
        return next;
    }

    /// <summary>The built-in categories the rules cover, in the server's order.</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<PrivacyRuleDto> rules) => rules.Select(r => r.Category).Distinct().ToList();

    /// <summary>The first rule's description of a category (what the row's detail says).</summary>
    public static string? Description(IEnumerable<PrivacyRuleDto> rules, string category) => rules.FirstOrDefault(r => r.Category == category)?.Description;

    /// <summary>The action a category effectively has: its override, else the default action.</summary>
    public static string ActionOf(PrivacySettings s, string category) => s.CategoryActions.TryGetValue(category, out var a) ? a : s.DefaultAction;

    /// <summary>The category row is off: the guard is off, or this category's action is "off".</summary>
    public static bool IsOff(PrivacySettings s, string category) => !s.Enabled || ActionOf(s, category) == PrivacyActions.Off;

    /// <summary>
    /// The action "enable" gives a category that is switched off: the default action, or "warn" when that is off too.
    /// (The web's button writes <c>categoryActions[category] ?? defaultAction</c>, which for an explicit "off" is "off" again and does nothing.)
    /// </summary>
    public static string EnabledAction(PrivacySettings s) => s.DefaultAction == PrivacyActions.Off ? PrivacyActions.Warn : s.DefaultAction;

    /// <summary>Sets one category's action.</summary>
    public static PrivacySettings SetAction(PrivacySettings s, string category, string action) => With(s, n => n.CategoryActions[category] = action);

    /// <summary>Switches a custom rule on or off.</summary>
    public static PrivacySettings SetRuleEnabled(PrivacySettings s, string ruleId, bool enabled)
        => With(s, n => { foreach (var r in n.CustomRules.Where(r => r.Id == ruleId)) r.Enabled = enabled; });

    /// <summary>Removes a custom rule.</summary>
    public static PrivacySettings RemoveRule(PrivacySettings s, string ruleId) => With(s, n => n.CustomRules.RemoveAll(r => r.Id == ruleId));

    /// <summary>Adds a custom rule at the end.</summary>
    public static PrivacySettings AddRule(PrivacySettings s, PrivacyCustomRule rule) => With(s, n => n.CustomRules.Add(rule));

    /// <summary>A new custom rule's id: "custom-" and the clock in base 36 (web <c>Date.now().toString(36)</c>).</summary>
    public static string NewRuleId(DateTimeOffset now) => "custom-" + Base36(now.ToUnixTimeMilliseconds());

    private static string Base36(long value)
    {
        if (value <= 0) return "0";
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var chars = new Stack<char>();
        for (; value > 0; value /= 36) chars.Push(digits[(int)(value % 36)]);
        return new string(chars.ToArray());
    }

    /// <summary>Whether a pattern compiles; <paramref name="error"/> is the engine's message when it does not (the server validates again on save).</summary>
    public static bool TryValidatePattern(string pattern, out string error)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            error = "";
            return true;
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>The rule to add from the dialog's fields, or null when name or pattern is empty.</summary>
    public static PrivacyCustomRule? NewRule(string name, string pattern, DateTimeOffset now)
        => name.Trim() == "" || pattern.Trim() == ""
            ? null
            : new PrivacyCustomRule { Id = NewRuleId(now), Name = name.Trim(), Pattern = pattern.Trim(), Enabled = true };

    /// <summary>The category / action options of a select: "off" first for the policy, without it for a category that is on.</summary>
    public static IReadOnlyList<string> SelectableActions(bool includeOff) => includeOff ? PrivacyMeta.Actions : PrivacyMeta.Actions.Where(a => a != "off").ToArray();
}
