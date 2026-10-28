using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// The look-and-language preferences Settings › Appearance edits (web <c>useAppearance</c> and <c>useI18n</c>): the theme source, the accent
/// choice and the UI language. The app implements it over the theme service, the desktop host and its prefs file; tests use
/// <see cref="MemoryAppearance"/>.
/// </summary>
public interface IAppearance
{
    /// <summary>Light, dark or follow the system.</summary>
    ThemeSource Theme { get; }

    /// <summary>"system" (follow the OS accent) or a "#rrggbb" preset color.</summary>
    string AccentChoice { get; }

    /// <summary>The OS accent color, or null when the platform does not report one.</summary>
    string? SystemAccent { get; }

    /// <summary>"zh" or "en".</summary>
    string Language { get; }

    /// <summary>Applies a theme source.</summary>
    Task SetThemeAsync(ThemeSource source);

    /// <summary>Applies an accent choice.</summary>
    Task SetAccentAsync(string choice);

    /// <summary>Switches the UI language (every open page redraws) and remembers it.</summary>
    void SetLanguage(string language);
}

/// <summary>An <see cref="IAppearance"/> that only remembers what it was told.</summary>
public sealed class MemoryAppearance : IAppearance
{
    /// <summary>The accent choice meaning "follow the system".</summary>
    public const string SystemAccentChoice = "system";

    /// <inheritdoc />
    public ThemeSource Theme { get; set; } = ThemeSource.System;

    /// <inheritdoc />
    public string AccentChoice { get; set; } = SystemAccentChoice;

    /// <inheritdoc />
    public string? SystemAccent { get; set; }

    /// <inheritdoc />
    public string Language { get; set; } = "zh";

    /// <inheritdoc />
    public Task SetThemeAsync(ThemeSource source)
    {
        Theme = source;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAccentAsync(string choice)
    {
        AccentChoice = choice;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SetLanguage(string language) => Language = language;
}
