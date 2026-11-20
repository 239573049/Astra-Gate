using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Update;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Core.Update;
using Astra.Desktop.Platform.Tray;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The bottom-right update card: in the main window's corner while it is on screen (above an open sheet, like the toasts — issue #41), in a
/// corner window of its own otherwise, and moved between the two when the window is shown or hidden mid-update.
/// </summary>
public class UpdateNoticeTests
{
    private static readonly UpdateDialogLabels T = UpdateDialogLabels.For("en");

    [AvaloniaFact]
    public void The_card_sits_in_the_windows_bottom_right_corner_above_an_open_sheet()
    {
        var sheet = new Sheet { SheetWidth = 640, Title = "sheet" };
        var window = new Window { Content = new Panel { Children = { sheet } }, Width = 1200, Height = 800 };
        window.Show();
        var notice = new UpdateNotice(() => window.IsVisible ? window : null);
        try
        {
            sheet.IsOpen = true;
            SceneCapture.Pump(5);
            notice.Show(UpdateCards.Available(T, new UpdateSummary("", UpdateAction.DesktopDmg, "9.9.9", "notes"), reminder: true));
            SceneCapture.Pump(5);

            Assert.True(notice.InWindow);
            var overlay = OverlayLayer.GetOverlayLayer(window) ?? throw new InvalidOperationException("no overlay layer");
            var host = overlay.Children.Single(c => c.GetVisualDescendants().Contains(notice.Card));
            Assert.All(overlay.Children.Where(c => c != host), m => Assert.True(host.ZIndex > m.ZIndex, $"card is not above {m.GetType().Name}"));
            Assert.Equal((HorizontalAlignment.Right, VerticalAlignment.Bottom), (notice.Card.HorizontalAlignment, notice.Card.VerticalAlignment));
            var cardRight = notice.Card.TranslatePoint(new Avalonia.Point(notice.Card.Bounds.Width, notice.Card.Bounds.Height), window)!.Value;
            Assert.InRange(window.ClientSize.Width - cardRight.X, UpdateNotice.Gap - 1, UpdateNotice.Gap + 1);
            Assert.InRange(window.ClientSize.Height - cardRight.Y, UpdateNotice.Gap - 1, UpdateNotice.Gap + 1);
        }
        finally
        {
            notice.Hide();
            sheet.IsOpen = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_running_update_follows_the_window_into_the_corner_window_and_back()
    {
        var window = new Window { Content = new Panel(), Width = 1000, Height = 700 };
        window.Show();
        var notice = new UpdateNotice(() => window.IsVisible ? window : null);
        try
        {
            notice.Show(UpdateCards.Progress(T, new UpdateProgress(UpdateStage.Downloading, "9.9.9", 10, 100)));
            SceneCapture.Pump(3);
            Assert.True(notice.InWindow);

            window.Hide();
            notice.Rehost();
            SceneCapture.Pump(3);
            Assert.False(notice.InWindow);
            Assert.IsType<UpdateCardWindow>(TopLevel.GetTopLevel(notice.Card));

            // Download ticks keep rendering into the same card wherever it is.
            notice.Show(UpdateCards.Progress(T, new UpdateProgress(UpdateStage.Downloading, "9.9.9", 60, 100)));
            Assert.Equal("60% · 0.0 / 0.0 MB", notice.Model!.ProgressText);

            window.Show();
            notice.Rehost();
            SceneCapture.Pump(3);
            Assert.True(notice.InWindow);
            Assert.Same(window, TopLevel.GetTopLevel(notice.Card));
        }
        finally
        {
            notice.Hide();
            window.Close();
        }
        Assert.Null(notice.InWindow);
        Assert.Null(notice.Card.Parent);
    }

    [AvaloniaFact]
    public void The_card_draws_the_state_and_reports_its_buttons()
    {
        var card = new UpdateCardView();
        UpdateCardAction? pressed = null;
        card.Acted += a => pressed = a;

        var window = new Window { Content = card, Width = 500, Height = 300 };
        window.Show();
        try
        {
            card.Render(UpdateCards.Progress(T, new UpdateProgress(UpdateStage.Downloading, "9.9.9", 50, 100)));
            SceneCapture.Pump(3);
            Assert.True(card.ProgressShown);
            Assert.False(card.Indeterminate);
            Assert.Equal(0.5, card.FillShare, 3);

            card.Render(UpdateCards.Progress(T, new UpdateProgress(UpdateStage.Restarting, "9.9.9")));
            Assert.True(card.Indeterminate);
        }
        finally
        {
            window.Close();
        }

        card.Render(UpdateCards.Progress(T, new UpdateProgress(UpdateStage.Failed, "9.9.9", Error: "boom")));
        Assert.False(card.ProgressShown);
        Assert.True(card.Press(UpdateCardAction.Retry));
        Assert.Equal(UpdateCardAction.Retry, pressed);
        Assert.False(card.Press(UpdateCardAction.Skip));
        Assert.Single(card.GetLogicalDescendants().OfType<TKCode.Arc.Button>());
    }
}
