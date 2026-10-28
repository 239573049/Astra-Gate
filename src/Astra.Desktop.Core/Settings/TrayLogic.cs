using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// Settings › Tray panel (web/src/components/tray/TraySettings.tsx): which text the menu bar shows and which blocks the panel shows.
/// The enums are the desktop host's; the web names (catalog keys, prefs JSON) come from here.
/// </summary>
public static class TrayLogic
{
    /// <summary>The blocks the usage card lists (everything but the clients block, which has its own card).</summary>
    public static readonly TraySection[] UsageSections =
    [
        TraySection.Overview, TraySection.Cost, TraySection.Chart, TraySection.TopModel,
        TraySection.CompareYesterday, TraySection.CacheHitRate, TraySection.Quota,
    ];

    /// <summary>The menu-bar title modes, in the web's order.</summary>
    public static readonly TrayTitleMode[] TitleModes =
        [TrayTitleMode.None, TrayTitleMode.Requests, TrayTitleMode.Tokens, TrayTitleMode.Cost, TrayTitleMode.Quota, TrayTitleMode.Alerts];

    /// <summary>The web name of a block (desktop/src/shared/prefs.ts <c>TRAY_SECTIONS</c>).</summary>
    public static string Name(TraySection section) => section switch
    {
        TraySection.Overview => "overview",
        TraySection.Cost => "cost",
        TraySection.Chart => "chart",
        TraySection.TopModel => "topModel",
        TraySection.CompareYesterday => "compareYesterday",
        TraySection.CacheHitRate => "cacheHitRate",
        TraySection.Quota => "quota",
        _ => "clients",
    };

    /// <summary>The web name of a title mode.</summary>
    public static string Name(TrayTitleMode mode) => mode switch
    {
        TrayTitleMode.None => "none",
        TrayTitleMode.Requests => "requests",
        TrayTitleMode.Tokens => "tokens",
        TrayTitleMode.Cost => "cost",
        TrayTitleMode.Quota => "quota",
        _ => "alerts",
    };

    /// <summary>The block's switch label.</summary>
    public static Msg SectionLabel(TraySection section) => new("settings.tray.section." + Name(section));

    /// <summary>The block's switch detail.</summary>
    public static Msg SectionHint(TraySection section) => new("settings.tray.section." + Name(section) + ".hint");

    /// <summary>The title mode's card label.</summary>
    public static Msg TitleLabel(TrayTitleMode mode) => new("settings.tray.title." + Name(mode));

    /// <summary>The title mode's card description.</summary>
    public static Msg TitleHint(TrayTitleMode mode) => new("settings.tray.title." + Name(mode) + ".hint");

    /// <summary>Prefs with one block switched (web <c>withSection</c>).</summary>
    public static TrayPrefs WithSection(TrayPrefs prefs, TraySection section, bool on)
    {
        var sections = new Dictionary<TraySection, bool>(prefs.Sections) { [section] = on };
        return new TrayPrefs(prefs.Title, sections);
    }

    /// <summary>Prefs with another title mode.</summary>
    public static TrayPrefs WithTitle(TrayPrefs prefs, TrayTitleMode mode) => new(mode, prefs.Sections);

    /// <summary>Whether a block is on (an absent entry counts as off).</summary>
    public static bool IsOn(TrayPrefs prefs, TraySection section) => prefs.Sections.TryGetValue(section, out var on) && on;
}
