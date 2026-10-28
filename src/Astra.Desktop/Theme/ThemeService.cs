using AtomUI;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using TKCode.Arc;

namespace Astra.Desktop.Theme;

/// <summary>One of the web's manual accent colors (<c>ACCENT_PRESETS</c> in web/src/shell/appearance.tsx: the macOS set).</summary>
public sealed record AccentPreset(string Id, string Hex);

/// <summary>
/// Light / dark / follow-system plus the accent, applied through AtomUI's theme manager (plan avalonia-client §3 D5).
/// The choice is read from <see cref="DesktopPrefs"/> and handed to AtomUI while the app is built
/// (<see cref="Configure"/>), so the first window already has its final colors; later changes go through
/// <see cref="ApplyAsync"/>. The accent follows the OS ("system") unless a preset color is chosen, like the web.
/// </summary>
public sealed class ThemeService
{
    /// <summary>The web's fallback when the OS reports no accent.</summary>
    public const string DefaultAccent = "#007aff";

    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("blue", "#007aff"), new("purple", "#a550a7"), new("pink", "#f74f9e"), new("red", "#ff5257"),
        new("orange", "#f7821b"), new("yellow", "#ffc600"), new("green", "#62ba46"), new("graphite", "#8c8c8c"),
    ];

    private static readonly (string Name, string Value)[] WebTokens =
    [
        ("RadiusControl", "10"), ("RadiusPanel", "14"), ("RadiusSurface", "22"),
        ("ControlSizeSM", "28"), ("ControlSizeMD", "32"), ("ControlSizeLG", "36"),
        ("TypeXS", "11"), ("TypeSM", "13"), ("TypeBase", "14"), ("TypeLG", "15"), ("TypeXL", "18"), ("Type2XL", "22"), ("Type3XL", "26"),
    ];

    // The web remaps Arc's color tokens onto its macOS palette (web/src/styles/index.css, `:root` / `[data-theme='dark']` and the
    // ":root[data-theme]" block): --foreground = --label, --text-secondary = --label-2, --text-muted = label 46%, --border = --separator,
    // --border-subtle = separator 60%, --border-strong = label 22%, --surface-muted = label 6%, --background = --canvas, the status colors
    // = the system green / orange / red, and --surface = the translucent content material (#fff 78% / #3a3a3e 42%) that cards, tables,
    // alerts and controls lay over the wallpaper. Floating layers (dialogs, menus, popovers) use SurfaceRaised = --surface-solid (#fff / #2a2a2d), opaque.
    // color-mix(in oklab, X p%, transparent) is X with its alpha scaled by p, so the values below are the resolved ARGB.
    private static readonly (string Name, string Value)[] PaletteLight =
    [
        ("Success", "#34C759"), ("Warning", "#FF9500"), ("Danger", "#FF3B30"), ("Surface", "#C7FFFFFF"),
        ("Foreground", "#E0000000"), ("TextSecondary", "#8C000000"), ("TextMuted", "#67000000"),
        ("Border", "#17000000"), ("BorderSubtle", "#0E000000"), ("BorderStrong", "#31000000"), ("SurfaceMuted", "#0D000000"),
        ("Background", "#EEF0F5"), ("SurfaceRaised", "#FFFFFF"),
    ];
    private static readonly (string Name, string Value)[] PaletteDark =
    [
        ("Success", "#30D158"), ("Warning", "#FF9F0A"), ("Danger", "#FF453A"), ("Surface", "#6B3A3A3E"),
        ("Foreground", "#E6FFFFFF"), ("TextSecondary", "#8FFFFFFF"), ("TextMuted", "#6AFFFFFF"),
        ("Border", "#1AFFFFFF"), ("BorderSubtle", "#0FFFFFFF"), ("BorderStrong", "#32FFFFFF"), ("SurfaceMuted", "#0EFFFFFF"),
        ("Background", "#161618"), ("SurfaceRaised", "#2A2A2D"),
    ];

    private readonly DesktopPrefs _prefs;
    private PlatformColorValues? _system;
    private bool _subscribed;

    public ThemeService(DesktopPrefs prefs)
    {
        _prefs = prefs;
        AccentHex = ResolveAccentHex(prefs.LastSystemAccent);
    }

    /// <summary>The accent in effect (<c>#rrggbb</c>): what the wallpaper gradients and the window chrome are derived from.</summary>
    public string AccentHex { get; private set; }

    /// <summary>The OS accent (<c>#rrggbb</c>; the last one seen before the OS can be read), or null when the platform has none (the bridge's <c>getAccentColor</c>).</summary>
    public string? SystemAccentHex => _system is { } s ? SystemHex(s) : _prefs.LastSystemAccent;

    /// <summary>Raised after the accent or the light / dark choice was applied (the window chrome repaints its wallpaper from it).</summary>
    public event Action? AppearanceChanged;

    public ThemeSource Source => _prefs.Theme;

    /// <summary>"system" or a <c>#rrggbb</c> preset.</summary>
    public string AccentChoice => _prefs.Accent;

    /// <summary>The theme algorithms and Arc accent for one appearance. Arc recomputes its whole palette from the accent seed.</summary>
    public static ThemeConfig BuildConfig(bool dark, Color accent)
    {
        var builder = new ThemeConfigBuilder();
        builder = dark
            ? builder.WithAlgorithms(ThemeAlgorithm.Default, ThemeAlgorithm.Dark)
            : builder.WithAlgorithms(ThemeAlgorithm.Default);
        return builder.WithArc(arc =>
        {
            arc.Accent(accent);
            // The web remaps Arc's tokens to macOS density in web/src/styles/index.css (":root[data-theme]" block): 10 / 14 / 22
            // corner radii, 28 / 32 / 36 controls and a 11-13-14-15-18-22-26 type scale. Arc's own defaults are larger (18 / 26 / 34,
            // 36 / 44 / 50, 12-14-16-18-22-28-36), so they are overridden by the same numbers. The Corners package would also cover
            // the per-control radii; it is not referenced yet (open question in plan avalonia-client §17).
            foreach (var (name, value) in WebTokens) arc.Token(name, value);
            // The web's palette over Arc's defaults (PaletteLight / PaletteDark above).
            foreach (var (name, value) in dark ? PaletteDark : PaletteLight) arc.Token(name, value);
        }).Build();
    }

    /// <summary>
    /// Startup hook (call inside <c>UseAtomUI</c>): follow-system registers a light and a dark request so AtomUI resolves the
    /// OS appearance before the first window; an explicit choice is a single initial theme.
    /// </summary>
    public void Configure(IAtomUIBuilder builder)
    {
        var accent = ParseHex(AccentHex);
        // The kit's app-wide corrections of Arc (accent primary buttons, CJK empty states): an initializer runs once the Application exists.
        builder.Theme.AddInitializer(_ => KitStyles.Install(Application.Current, AccentHex));
        if (_prefs.Theme == ThemeSource.System)
        {
            builder.WithFollowSystemThemes(Request(false, accent), Request(true, accent));
        }
        else
        {
            builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID, BuildConfig(_prefs.Theme == ThemeSource.Dark, accent));
        }
    }

    /// <summary>
    /// Reads the OS accent (only possible once Avalonia is up), remembers it for the next start's first frame and listens for
    /// OS appearance / accent changes. A changed accent re-applies the theme.
    /// </summary>
    public Task AttachAsync()
    {
        if (Application.Current?.PlatformSettings is not { } settings) return Task.CompletedTask;
        _system = settings.GetColorValues();
        if (!_subscribed)
        {
            _subscribed = true;
            settings.ColorValuesChanged += async (_, values) =>
            {
                _system = values;
                await ApplyAsync();
            };
        }
        var seen = SystemHex(_system);
        if (seen is not null && seen != _prefs.LastSystemAccent)
        {
            _prefs.LastSystemAccent = seen;
            _prefs.Save();
            if (_prefs.Accent == DesktopPrefs.SystemAccent) return ApplyAsync();
        }
        return Task.CompletedTask;
    }

    public Task SetSourceAsync(ThemeSource source)
    {
        _prefs.Theme = source;
        _prefs.Save();
        return ApplyAsync();
    }

    public Task SetAccentAsync(string choice)
    {
        _prefs.Accent = choice;
        _prefs.Save();
        return ApplyAsync();
    }

    /// <summary>Applies the current choice. Always asserts the full desired state, so it also wins over AtomUI's own follow-system listener.</summary>
    public async Task ApplyAsync()
    {
        if (Application.Current?.GetThemeManager() is not { } manager) return;
        var dark = _prefs.Theme switch
        {
            ThemeSource.Dark => true,
            ThemeSource.Light => false,
            _ => (_system ?? Application.Current.PlatformSettings?.GetColorValues())?.ThemeVariant == PlatformThemeVariant.Dark,
        };
        var systemHex = _system is { } s ? SystemHex(s) : _prefs.LastSystemAccent;
        AccentHex = ResolveAccentHex(systemHex);
        var accent = ParseHex(AccentHex);
        KitStyles.SetAccent(Application.Current, AccentHex);
        await manager.ApplyThemeAsync(Request(dark, accent));
        AppearanceChanged?.Invoke();
    }

    private static ThemeRequest Request(bool dark, Color accent)
        => new(IThemeManager.DEFAULT_THEME_ID, BuildConfig(dark, accent), ThemeTransitionReason.UserRequest);

    private string ResolveAccentHex(string? systemHex)
        => _prefs.Accent == DesktopPrefs.SystemAccent ? systemHex ?? DefaultAccent : _prefs.Accent;

    /// <summary>The OS accent as <c>#rrggbb</c>; null where the platform reports none (Avalonia then leaves it transparent).</summary>
    private static string? SystemHex(PlatformColorValues v)
        => v.AccentColor1 is { A: > 0 } c ? $"#{c.R:x2}{c.G:x2}{c.B:x2}" : null;

    private static Color ParseHex(string hex) => Color.Parse(hex);
}
