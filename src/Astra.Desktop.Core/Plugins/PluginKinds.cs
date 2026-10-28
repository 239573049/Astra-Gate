using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Plugins;

/// <summary>Badge colors of the web (<c>KIND_TONE</c>); the page maps them to its own tone enum.</summary>
public enum PluginTone
{
    /// <summary>Muted.</summary>
    Neutral,

    /// <summary>The accent color.</summary>
    Accent,

    /// <summary>Success.</summary>
    Green,

    /// <summary>Warning.</summary>
    Orange,
}

/// <summary>What a plugin can be (<c>PluginKind</c>): the strings the API uses, their labels and badge tones.</summary>
public static class PluginKinds
{
    /// <summary>An OpenCode provider plugin (runs in the Bun plugin host).</summary>
    public const string Provider = "provider";

    /// <summary>Gateway middleware (runs in Astra's own process).</summary>
    public const string Middleware = "middleware";

    /// <summary>An agent: a client added to the Clients page.</summary>
    public const string Agent = "agent";

    /// <summary>A pi extension.</summary>
    public const string Pi = "pi";

    /// <summary>The message key of a kind's name (<c>KIND_LABEL</c>); an unknown kind is shown as it is by <see cref="Label"/>.</summary>
    public static string LabelKey(string kind) => "plugins.kind." + kind;

    /// <summary>The kind's name in the UI language; the kind itself when this build has no word for it.</summary>
    public static string Label(string kind) => I18n.Loc.Has(LabelKey(kind)) ? I18n.Loc.T(LabelKey(kind)) : kind;

    /// <summary>The badge tone of a kind (<c>KIND_TONE</c>).</summary>
    public static PluginTone Tone(string kind) => kind switch
    {
        Middleware => PluginTone.Accent,
        Agent => PluginTone.Green,
        Pi => PluginTone.Orange,
        _ => PluginTone.Neutral,
    };

    /// <summary>True when the plugin runs in the Bun plugin host (provider or pi): the page then asks for the host's status.</summary>
    public static bool RunsInHost(string kind) => kind is Provider or Pi;
}

/// <summary>How a plugin's picture is drawn (<c>PluginIcon</c> in PluginsPage.tsx).</summary>
public enum PluginIconKind
{
    /// <summary>A picture the plugin gives (a <c>data:image/</c> URL).</summary>
    Picture,

    /// <summary>A shield on blue: middleware without a usable picture.</summary>
    MiddlewareGlyph,

    /// <summary>An app window on teal: an agent without a usable picture.</summary>
    AgentGlyph,

    /// <summary>The brand Astra knows by the plugin's name, else its initials.</summary>
    Brand,
}

/// <summary>Decides how a plugin is pictured.</summary>
public static partial class PluginIcons
{
    /// <summary>Fill of the middleware glyph tile (web <c>#0a84ff</c>).</summary>
    public const string MiddlewareColor = "#0a84ff";

    /// <summary>Fill of the agent glyph tile (web <c>#30b0a0</c>).</summary>
    public const string AgentColor = "#30b0a0";

    [GeneratedRegex(@"^(https:|data:image/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Drawable();

    /// <summary>True when the web would put the icon in an <c>img</c>: an https URL or an image data URL.</summary>
    public static bool IsPictureUrl(string? icon) => !string.IsNullOrEmpty(icon) && Drawable().IsMatch(icon);

    /// <summary>
    /// How to draw. The web shows an https icon as an image; the desktop client does not download icons (BrandIcon shows initials for
    /// them), so only <c>data:image/</c> counts as a picture and the https ones fall to the kind's glyph or brand.
    /// </summary>
    public static PluginIconKind Resolve(string? icon, string kind)
    {
        if (icon is not null && icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return PluginIconKind.Picture;
        return kind switch
        {
            PluginKinds.Middleware => PluginIconKind.MiddlewareGlyph,
            PluginKinds.Agent => PluginIconKind.AgentGlyph,
            _ => PluginIconKind.Brand,
        };
    }

    /// <summary>The corner radius of a plugin picture of <paramref name="size"/> px (web: 28%).</summary>
    public static double Radius(double size) => Math.Round(size * 0.28);
}
