using Astra.Desktop.Components.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The provider card's name row stays inside the card (it clips), and the tooltips over it have a bubble.</summary>
public class ProviderCardLayoutTests
{
    [AvaloniaTheory]
    [InlineData(1440)]
    [InlineData(1100)] // cards near their 300 px minimum
    public void Actions_stay_inside_the_card_and_the_name_keeps_some_room(int width)
    {
        var built = Scenes.All["providers__states"](); // long names, a template-update badge, every health state
        var window = new Window { Content = built.View, Width = width, Height = SceneCapture.Height };
        window.Show();
        try
        {
            if (built.Settled is not null) SceneCapture.Await(built.Settled(), TimeSpan.FromSeconds(20));
            SceneCapture.Pump(20);

            var cards = window.GetVisualDescendants().OfType<ProviderCardView>().ToList();
            Assert.NotEmpty(cards);
            foreach (var card in cards)
            {
                var label = Avalonia.Automation.AutomationProperties.GetName(card);
                var buttons = card.GetVisualDescendants().OfType<Button>().ToList();
                Assert.NotEmpty(buttons);
                foreach (var button in buttons)
                {
                    var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), card)!.Value.X;
                    Assert.True(right <= card.Bounds.Width, $"{label}: {button.GetType().Name} ends at {right} of {card.Bounds.Width}");
                }
                var name = card.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label);
                // A short name may be narrower than the room it was given; only a trimmed one has to have kept enough to read.
                var full = new TextBlock { Text = name.Text, FontSize = name.FontSize, FontWeight = name.FontWeight, FontFamily = name.FontFamily };
                full.Measure(Size.Infinity);
                Assert.True(name.Bounds.Width >= Math.Min(full.DesiredSize.Width - 1, 60), $"{label}: the name has only {name.Bounds.Width} of {full.DesiredSize.Width} px");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_tooltip_draws_a_bubble()
    {
        var tip = new ToolTip { Content = "WorkBuddy" };
        var window = new Window { Content = tip, Width = 200, Height = 100 };
        window.Show();
        try
        {
            SceneCapture.Pump(2);
            var bubble = tip.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
            Assert.IsAssignableFrom<ISolidColorBrush>(bubble.Background);
            Assert.True(bubble.BorderThickness.Left > 0);
        }
        finally
        {
            window.Close();
        }
    }
}
