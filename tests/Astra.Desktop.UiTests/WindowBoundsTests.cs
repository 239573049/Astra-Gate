using Astra.Desktop.Platform;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Regression for the "resized window after the first launch" bug (plan avalonia-client §24): the saved size was the client size times the
/// WINDOW's render scaling (2 on a Retina Mac), but the position and work areas are in the screen's units (points on macOS, scaling 1) —
/// the next launch read a 2560x1640 window on a 1470x847 display and opened it full screen.
/// </summary>
public class WindowBoundsTests
{
    private static readonly Box[] MacDisplay = [new Box(0, 33, 1470, 847)];

    [Fact]
    public void Size_is_saved_in_the_units_of_the_screen_not_of_the_window()
    {
        // A Retina Mac: Avalonia reports the screen with scaling 1 (points), the window renders at 2. The size must stay 1280x820.
        var (w, h) = WindowBounds.ToScreenUnits(1280, 820, screenScaling: 1);
        Assert.Equal((1280d, 820d), (w, h));
        // Windows at 150 %: screen coordinates are physical pixels.
        Assert.Equal((1920d, 1230d), WindowBounds.ToScreenUnits(1280, 820, 1.5));
    }

    [Fact]
    public void Save_then_restore_round_trips_on_any_scale()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var (sw, sh) = WindowBounds.ToScreenUnits(1280, 820, scale);
            var (w, h) = WindowBounds.FromScreenUnits(sw, sh, scale);
            Assert.Equal((1280d, 820d), (w, h));
        }
    }

    [Fact]
    public void A_prefs_file_written_by_the_buggy_build_is_dropped_instead_of_opening_full_screen()
    {
        var legacy = new Box(95, 46, 2560, 1640);
        Assert.NotNull(WindowBounds.Restorable(legacy, MacDisplay)); // it does overlap the display ...
        Assert.False(WindowBounds.FitsDisplay(legacy, MacDisplay));   // ... but is larger than it: default size instead
    }

    [Fact]
    public void A_sane_remembered_window_is_restored_and_one_that_lost_its_display_is_not()
    {
        var saved = new Box(95, 46, 1280, 820);
        Assert.Equal(saved, WindowBounds.Restorable(saved, MacDisplay));
        Assert.True(WindowBounds.FitsDisplay(saved, MacDisplay));
        Assert.Null(WindowBounds.Restorable(new Box(5000, 46, 1280, 820), MacDisplay));
        Assert.False(WindowBounds.FitsDisplay(new Box(5000, 46, 1280, 820), MacDisplay));
    }

    [Fact]
    public void Remembered_size_never_goes_below_the_minimum()
        => Assert.Equal(new Box(0, 0, 960, 640), WindowBounds.ClampToMinimum(new Box(0, 0, 100, 100)));
}
