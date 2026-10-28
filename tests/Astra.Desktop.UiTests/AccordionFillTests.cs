using Astra.Desktop.Components.Kit;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using TKCode.Arc;
using AtomWindow = AtomUI.Desktop.Controls.Window;

namespace Astra.Desktop.UiTests;

/// <summary>Arc's accordion body frame is content-wide (ARC-GAP): <see cref="AccordionFill.FillWidth"/> makes a stretching child as wide as the item.</summary>
public class AccordionFillTests
{
    private static double WidthOf(bool fill)
    {
        var block = new CodeBlock { Code = "{\n  \"a\": 1\n}", Language = "json", HorizontalAlignment = HorizontalAlignment.Stretch };
        var item = new AccordionItem { Header = "x", Content = block };
        if (fill) item.FillWidth();
        var accordion = new Accordion { ExpandedIndex = 0 };
        accordion.Items.Add(item);
        var window = new AtomWindow { Content = new StackPanel { Children = { accordion } }, Width = 700, Height = 500 };
        window.Show();
        SceneCapture.Pump(3);
        var width = block.Bounds.Width;
        window.Close();
        return width;
    }

    [AvaloniaFact]
    public void Content_fills_the_item_only_with_FillWidth()
    {
        Assert.True(WidthOf(false) < 400);
        Assert.True(WidthOf(true) > 600);
    }
}
