using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Theme;
using Avalonia.Controls;
using Avalonia.Layout;
using CoreThemeSource = Astra.Desktop.Core.Shell.ThemeSource;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › General: appearance (theme, accent, language) and start at login. The rest of the web's <c>GeneralSettings</c> lives in its
/// own categories (<see cref="GatewaySettingsView"/>, <see cref="ModelSettingsView"/>, <see cref="NetworkSettingsView"/>,
/// <see cref="UsageSettingsView"/>, <see cref="LoggingSettingsView"/>, <see cref="AboutSettingsView"/>).
/// </summary>
public sealed class GeneralSettingsView : LiveView
{
    private readonly SettingsPageViewModel _page;
    private readonly SettingsApi _api;
    private ControlledSegmented _theme = null!;
    private ControlledSelect _accent = null!;
    private SettingsRow _accentRow = null!;
    private ControlledSegmented _language = null!;

    /// <summary>Creates the view.</summary>
    public GeneralSettingsView(SettingsPageViewModel page) : base(page.Context!)
    {
        _page = page;
        _api = page.Api;
    }

    /// <inheritdoc />
    protected override void Build()
    {
        var root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(BuildAppearance());
        root.Children.Add(new StartupGroupView(Context, _api));
        Content = root;
    }

    private Control BuildAppearance()
    {
        var a = _page.Appearance;
        _theme = new ControlledSegmented(
            [("system", Loc.T("settings.theme.system")), ("light", Loc.T("settings.theme.light")), ("dark", Loc.T("settings.theme.dark"))],
            ThemeName(a.Theme));
        Avalonia.Automation.AutomationProperties.SetName(_theme, Loc.T("settings.theme"));
        _theme.Chose += v => _ = ChangeThemeAsync(v);

        var options = new List<(string, string)> { ("system", Loc.T("settings.accent.system")) };
        options.AddRange(ThemeService.Presets.Select(p => (p.Hex, Loc.T("settings.accent." + p.Id))));
        _accent = new ControlledSelect(options, a.AccentChoice, 160);
        Avalonia.Automation.AutomationProperties.SetName(_accent, Loc.T("settings.accent"));
        _accent.Chose += v => _ = ChangeAccentAsync(v);
        _accentRow = RowBuilder.Row(Loc.T("settings.accent"), null, _accent);

        _language = new ControlledSegmented([("zh", "中文"), ("en", "English")], a.Language);
        Avalonia.Automation.AutomationProperties.SetName(_language, Loc.T("settings.language"));
        _language.Chose += v => _ = ChangeLanguageAsync(v);

        return RowBuilder.Group(Loc.T("settings.appearance"), null,
            RowBuilder.Row(Loc.T("settings.theme"), null, _theme),
            _accentRow,
            RowBuilder.Hinted(Loc.T("settings.language"), Loc.T("settings.languageHint"), _language));
    }

    private static string ThemeName(CoreThemeSource s) => s switch { CoreThemeSource.Light => "light", CoreThemeSource.Dark => "dark", _ => "system" };

    /// <inheritdoc />
    protected override void Refresh()
    {
        var a = _page.Appearance;
        _theme.SetValue(ThemeName(a.Theme));
        _accent.SetValue(a.AccentChoice);
        _language.SetValue(a.Language);
        // Which colour "system" resolved to is a hint, not a line: the row stays one line high.
        _accentRow.Hint = a.AccentChoice == MemoryAppearance.SystemAccentChoice
            ? Loc.T(a.SystemAccent is not null ? "settings.accent.followingSystem" : "settings.accent.systemUnavailable")
            : null;
    }

    private async Task ChangeThemeAsync(string name)
    {
        var source = name switch { "light" => CoreThemeSource.Light, "dark" => CoreThemeSource.Dark, _ => CoreThemeSource.System };
        await Host.TryAsync(() => _page.Appearance.SetThemeAsync(source));
        Refresh();
    }

    private async Task ChangeAccentAsync(string choice)
    {
        await Host.TryAsync(() => _page.Appearance.SetAccentAsync(choice));
        Refresh();
    }

    private async Task ChangeLanguageAsync(string language)
    {
        _page.Appearance.SetLanguage(language);
        // The language of stored billing descriptions follows too.
        await Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("locale", language))));
    }
}
