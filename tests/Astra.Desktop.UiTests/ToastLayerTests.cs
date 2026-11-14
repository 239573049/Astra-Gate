using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Toasts and error alerts share the window's overlay layer with Arc's sheets, which take a rising ZIndex per open: a toast shown
/// while a sheet is open must still draw above it (issue #41: 「备份成功」 appeared under the backup sheet's scrim).
/// </summary>
public class ToastLayerTests
{
    [AvaloniaFact]
    public void Toasts_and_error_alerts_draw_above_an_open_sheet()
    {
        var sheet = new Sheet { SheetWidth = 640, Title = "sheet" };
        var root = new Panel { Children = { sheet } };
        var window = new Window { Content = root, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            // The stack exists before the sheet opens, as in the app (ArcFeedback attaches it when the window opens).
            ToastPresenter.Attach(root);
            sheet.IsOpen = true;
            SceneCapture.Pump(5);

            ToastPresenter.Attach(root).Show(new ToastOptions { Kind = ToastKind.Success, Title = "done" });
            var alert = ErrorToasts.Show(root, "failed");
            SceneCapture.Pump(5);

            var overlay = OverlayLayer.GetOverlayLayer(root) ?? throw new InvalidOperationException("no overlay layer");
            var stack = overlay.Children.OfType<ToastStack>().Single();
            var alertHost = overlay.Children.Single(c => alert is not null && c.GetVisualDescendants().Contains(alert));
            var modals = overlay.Children.Where(c => c != stack && c != alertHost).ToList();
            Assert.NotEmpty(modals);
            Assert.All(modals, m =>
            {
                Assert.True(stack.ZIndex > m.ZIndex, $"toast stack ZIndex {stack.ZIndex} is not above {m.GetType().Name} ({m.ZIndex})");
                Assert.True(alertHost.ZIndex > m.ZIndex, $"error alert ZIndex {alertHost.ZIndex} is not above {m.GetType().Name} ({m.ZIndex})");
            });
        }
        finally
        {
            sheet.IsOpen = false;
            window.Close();
        }
    }
}
