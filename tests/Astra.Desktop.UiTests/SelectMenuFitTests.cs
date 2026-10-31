using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Astra.Desktop.UiTests;

/// <summary>
/// GitHub issue #15: a select sized to its value ("不设置") opened a menu exactly as narrow, and every model name in it read "dee…".
/// <see cref="SelectMenuFit"/> widens the menu to its widest option (within the web's 12rem..24rem) and keeps it in the window.
/// </summary>
public class SelectMenuFitTests
{
    private static readonly string[] Models =
    [
        "deepseek-ai/DeepSeek-V3.1-Terminus", "kimi-k2-0905-preview", "deepseek-v3.2-exp", "hy4-turbo", "step-3", "spark-x1", "minimax-m2",
    ];

    private sealed record Opened(Window Window, Select Select, Border Trigger, Border Surface);

    /// <summary>A clients-page model picker (<see cref="ClientUi.Select"/>, max 300 px) at the end of a row, or at its start.</summary>
    private static Opened Open(IEnumerable<string> labels, HorizontalAlignment align, double windowWidth = 900)
    {
        var options = new List<(string, string, bool)> { ("", "不设置", false) };
        options.AddRange(labels.Select(m => (m, m, false)));
        var select = ClientUi.Select(options, "", _ => { }, "model", maxWidth: 300);
        select.HorizontalAlignment = align;
        var window = new Window { Content = new Border { Padding = new Thickness(24), Child = select }, Width = windowWidth, Height = 600 };
        // Settled geometry: the menu's enter motion scales it from 0.97, which TranslatePoint would see (as SceneCapture does).
        ArcMotion.SetLevel(window, ArcMotionLevel.Off);
        window.Show();
        SceneCapture.Pump(2);
        select.IsOpen = true;
        SceneCapture.Pump(4);
        var parts = select.GetVisualDescendants().OfType<Border>().ToList();
        var trigger = parts.Single(b => b.Name == Select.TriggerPart);
        // The open menu lives in the window's overlay layer, not under the select.
        var surface = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == Select.SurfacePart && b.IsEffectivelyVisible);
        return new Opened(window, select, trigger, surface);
    }

    private static Rect InWindow(Visual v, Window window) => new(v.TranslatePoint(default, window) ?? default, v.Bounds.Size);

    [AvaloniaFact]
    public void A_narrow_trigger_opens_a_menu_wide_enough_for_long_model_names()
    {
        var o = Open(Models, HorizontalAlignment.Right);
        try
        {
            var trigger = InWindow(o.Trigger, o.Window);
            var surface = InWindow(o.Surface, o.Window);
            Assert.True(trigger.Width < 150, $"trigger {trigger.Width}");
            // The longest name needs ~250 px at 14 px plus padding and the check's room: the menu grows past the trigger, up to the cap.
            Assert.True(surface.Width > trigger.Width + 120, $"menu {surface.Width} vs trigger {trigger.Width}");
            Assert.True(surface.Width <= SelectMenuFit.MaxMenuWidth + 0.5, $"menu {surface.Width}");
            // At a row's end it flips to end-aligned and stays inside the window.
            Assert.True(surface.X >= -0.5 && surface.Right <= o.Window.ClientSize.Width + 0.5, $"menu {surface} in {o.Window.ClientSize}");
            Assert.Equal(trigger.Right, surface.Right, tolerance: 0.5);
        }
        finally
        {
            o.Select.IsOpen = false;
            o.Window.Close();
        }
    }

    [AvaloniaFact]
    public void A_menu_wider_than_its_trigger_opens_start_aligned_when_it_fits()
    {
        var o = Open(Models, HorizontalAlignment.Left);
        try
        {
            var trigger = InWindow(o.Trigger, o.Window);
            var surface = InWindow(o.Surface, o.Window);
            Assert.True(surface.Width > trigger.Width + 120, $"menu {surface.Width} vs trigger {trigger.Width}");
            Assert.Equal(trigger.X, surface.X, tolerance: 0.5);
        }
        finally
        {
            o.Select.IsOpen = false;
            o.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Short_options_keep_the_web_minimum_and_a_small_window_caps_the_menu()
    {
        var shortOnes = Open(["a", "b"], HorizontalAlignment.Left);
        try
        {
            Assert.Equal(SelectMenuFit.MinMenuWidth, shortOnes.Surface.Bounds.Width, tolerance: 0.5);
        }
        finally
        {
            shortOnes.Select.IsOpen = false;
            shortOnes.Window.Close();
        }

        var cramped = Open(Models.Select(m => m + "-with-a-very-long-suffix-that-cannot-fit"), HorizontalAlignment.Right, windowWidth: 320);
        try
        {
            var surface = InWindow(cramped.Surface, cramped.Window);
            Assert.True(surface.Width <= cramped.Window.ClientSize.Width - SelectMenuFit.WindowGutter + 0.5, $"menu {surface.Width}");
            Assert.True(surface.X >= -0.5 && surface.Right <= cramped.Window.ClientSize.Width + 0.5, $"menu {surface} in {cramped.Window.ClientSize}");
        }
        finally
        {
            cramped.Select.IsOpen = false;
            cramped.Window.Close();
        }
    }
}
