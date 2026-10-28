using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Theme;
using CoreThemeSource = Astra.Desktop.Core.Shell.ThemeSource;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The app's <see cref="IAppearance"/>: the theme source goes through the desktop host (the web's <c>setThemeSource</c> bridge call, which
/// is the theme service), the accent through the theme service, the language through <see cref="Loc"/> (every page redraws on its
/// <c>LanguageChanged</c>) and the prefs file.
/// </summary>
public sealed class AppAppearance(IDesktopHost host, ThemeService theme, DesktopPrefs prefs) : IAppearance
{
    /// <inheritdoc />
    public CoreThemeSource Theme => theme.Source switch
    {
        Astra.Desktop.Theme.ThemeSource.Light => CoreThemeSource.Light,
        Astra.Desktop.Theme.ThemeSource.Dark => CoreThemeSource.Dark,
        _ => CoreThemeSource.System,
    };

    /// <inheritdoc />
    public string AccentChoice => theme.AccentChoice;

    /// <inheritdoc />
    public string? SystemAccent => theme.SystemAccentHex;

    /// <inheritdoc />
    public string Language => Loc.Language;

    /// <inheritdoc />
    public async Task SetThemeAsync(CoreThemeSource source) => await host.SetThemeSourceAsync(source);

    /// <inheritdoc />
    public Task SetAccentAsync(string choice) => theme.SetAccentAsync(choice);

    /// <inheritdoc />
    public void SetLanguage(string language)
    {
        Loc.Language = language;
        prefs.Language = Loc.Language;
        prefs.Save();
    }
}
