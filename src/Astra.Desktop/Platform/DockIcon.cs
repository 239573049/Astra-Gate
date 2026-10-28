using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Astra.Desktop.Platform;

/// <summary>
/// The macOS Dock shows an icon image as is: the rounded corners and the margin of the system's icon grid have to be in the picture
/// (1024 canvas, 824 square, ~22% radius), unlike a bundle's .icns which the artwork already shapes the same way.
/// </summary>
internal static class DockIcon
{
    private const double Canvas = 1024, Body = 824, Radius = 185;

    /// <summary>PNG bytes of <paramref name="logo"/> clipped to a rounded square on a transparent canvas.</summary>
    public static byte[] Render(Bitmap logo)
    {
        using var target = new RenderTargetBitmap(new PixelSize((int)Canvas, (int)Canvas), new Vector(96, 96));
        using (var dc = target.CreateDrawingContext())
        {
            var rect = new Rect((Canvas - Body) / 2, (Canvas - Body) / 2, Body, Body);
            using (dc.PushClip(new RoundedRect(rect, Radius)))
                dc.DrawImage(logo, new Rect(logo.Size), rect);
        }
        using var ms = new MemoryStream();
        target.Save(ms, new PngBitmapEncoderOptions());
        return ms.ToArray();
    }
}
