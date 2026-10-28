using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// The web's own pictures, linked into the assembly from <c>web/src/assets</c> (Astra.Desktop.csproj <c>AvaloniaResource</c>), so the two clients
/// cannot drift. Each bitmap is decoded once and shared.
/// </summary>
public static class AppAssets
{
    private static Bitmap? _logo;
    private static Bitmap? _logoMark;

    /// <summary>The app logo with its dark rim (web <c>AstraLogo</c>, <c>assets/logo.png</c>): the sidebar header.</summary>
    public static Bitmap Logo => _logo ??= Load("logo.png");

    /// <summary>The bare mark (web <c>assets/logo-mark.png</c>): the traces hub node (web traces/nodes.tsx).</summary>
    public static Bitmap LogoMark => _logoMark ??= Load("logo-mark.png");

    private static Bitmap Load(string name) => new(AssetLoader.Open(new Uri("avares://astra-desktop/Assets/" + name)));
}
