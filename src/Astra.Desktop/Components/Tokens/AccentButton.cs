using Astra.Desktop.Components.Kit;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// The web's primary button: <c>variant="primary"</c> fills with the system accent, not Arc's neutral foreground fill
/// (<c>controls.tsx</c> <c>Button</c>, plan §9.1). Arc's Primary button with its fill, edge and label bound to the accent tokens.
/// </summary>
public static class AccentButton
{
    /// <summary>A primary button with <paramref name="content"/> (text or a control).</summary>
    public static ArcButton Create(object content)
    {
        // the accent fill and its label colour come from the kit's app-wide Primary style (Theme/KitStyles.axaml)
        return new ArcButton { Variant = ButtonVariant.Primary, Content = content };
    }
}
