using Astra.Desktop.Components.Kit;
using Avalonia;
using Astra.Desktop.Core.Shell;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace Astra.Desktop.Theme;

/// <summary>
/// App-wide corrections of Arc toward the web, applied once at startup (<see cref="ThemeService.Install"/> calls <see cref="Install"/>
/// from <c>AppBuilder.AfterSetup</c>, which runs once the <see cref="Application"/> exists, in the app and in the headless test host alike):
/// the accent primary button (KitStyles.axaml), CJK-aware wrapping of Arc's empty states (<see cref="CjkWrap"/>), select menus as wide as
/// their options (<see cref="SelectMenuFit"/>) and device-pixel hairlines.
/// </summary>
public sealed partial class KitStyles : Styles
{
    /// <summary>Loads the compiled styles.</summary>
    public KitStyles() => AvaloniaXamlLoader.Load(this);

    /// <summary>Resource key of the accent fill (web <c>--accent</c>, the same in light and dark).</summary>
    public const string AccentKey = "KitAccent";

    /// <summary>Resource key of the text color on accent fills (web <c>--on-accent</c>).</summary>
    public const string OnAccentKey = "KitOnAccent";

    /// <summary>Adds the styles to <paramref name="app"/> (once) and installs the kit's class handlers.</summary>
    public static void Install(Application? app, string accentHex)
    {
        if (app is null) return;
        SetAccent(app, accentHex);
        if (!app.Styles.OfType<KitStyles>().Any()) app.Styles.Add(new KitStyles());
        CjkWrap.Install();
        SelectMenuFit.Install();
        InstallHairlines();
        InstallSegmented();
    }

    private static bool _segmented;

    /// <summary>
    /// Arc's segmented control draws its selected pill with the Surface token (the translucent content material here) and a pill radius;
    /// the web's is <c>--control-selected</c> (#fff / white 16 %) with <c>radius-control - 4</c> (6 px). Both are set inside the template,
    /// where an app style cannot reach, so they are set on the part once the template is applied.
    /// </summary>
    private static void InstallSegmented()
    {
        if (_segmented) return;
        _segmented = true;
        Avalonia.Controls.Primitives.TemplatedControl.TemplateAppliedEvent.AddClassHandler<TKCode.Arc.SegmentedControl>((_, e) =>
        {
            if (e.NameScope.Find("PART_Selection") is not TKCode.Arc.Primitives.GlidingIndicator selection) return;
            selection.Bind(TKCode.Arc.Primitives.GlidingIndicator.FillProperty, Avalonia.Controls.ResourceNodeExtensions.GetResourceObservable(selection, "KitControlSelected"));
            selection.CornerRadius = new CornerRadius(6);
        });
    }

    private static bool _hairlines;

    /// <summary>
    /// The web's <c>0.5px</c> borders (cards, separators, glass edges) are drawn by Chromium as one device pixel, never thinner, so on a 1x
    /// display they are full 1px lines; Avalonia draws a half-pixel line at half strength. Every border side of exactly 0.5 is widened to
    /// one device pixel when it is loaded on a screen below 2x (on 2x and up 0.5 already is one device pixel).
    /// </summary>
    private static void InstallHairlines()
    {
        if (_hairlines) return;
        _hairlines = true;
        Avalonia.Controls.Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Border>((border, _) => WidenHairline(border));
    }

    private static void WidenHairline(Avalonia.Controls.Border border)
    {
        var t = border.BorderThickness;
        if (t.Left != 0.5 && t.Top != 0.5 && t.Right != 0.5 && t.Bottom != 0.5) return;
        var scale = Avalonia.Controls.TopLevel.GetTopLevel(border)?.RenderScaling ?? 1;
        if (scale >= 2) return;
        var px = 1 / scale;
        double Side(double v) => v == 0.5 ? px : v;
        border.SetCurrentValue(Avalonia.Controls.Border.BorderThicknessProperty, new Thickness(Side(t.Left), Side(t.Top), Side(t.Right), Side(t.Bottom)));
    }

    /// <summary>Sets the accent fill and re-derives the on-accent text color (<see cref="AccentMath.OnAccent"/>) for <paramref name="accentHex"/>.</summary>
    public static void SetAccent(Application? app, string accentHex)
    {
        if (app is null) return;
        app.Resources[AccentKey] = new SolidColorBrush(Color.Parse(accentHex));
        app.Resources[OnAccentKey] = new SolidColorBrush(Color.Parse(AccentMath.OnAccent(accentHex)));
    }
}
