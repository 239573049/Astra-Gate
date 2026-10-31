using Astra.Api.Contracts;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

/// <summary>The request list's local table (<see cref="RequestGrid"/>, ARC-GAP): sort, row reuse across updates, row height, activation.</summary>
public class RequestGridTests
{
    private static RequestSummaryDto Row(string id, string startedAt, long tokens, string? model = "gpt-4.1-mini", string? effort = null)
        => new(id, DateTimeOffset.Parse(startedAt, System.Globalization.CultureInfo.InvariantCulture), null, "prov_a", "OpenAI", "openai-chat", "openai-chat", true, model, model,
            model, model, true, "success", 200, 120, 900, 50.0, tokens, 20, 10, 0, 0, 12_000_000, "reported", effort, null, null, "tok_a", "ci-bot", null, null, false, null);

    private static (RequestGrid Grid, Window Window) Open(IReadOnlyList<RequestRowViewModel> rows, double width = 1200)
    {
        var ctx = new RequestTableContext { Clock = new FixedClock(DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), Provider = _ => null, TimeZone = TimeZoneInfo.Utc };
        var grid = RequestTable.Create(rows, ctx);
        var window = new Window { Content = grid, Width = width, Height = 600 };
        window.Show();
        SceneCapture.Pump(2);
        return (grid, window);
    }

    private static List<Control> BodyRows(RequestGrid grid)
        => grid.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "BodyRow").ToList();

    [AvaloniaFact]
    public void Header_clicks_sort_ascending_then_descending_with_empty_values_last()
    {
        var rows = new List<RequestRowViewModel>
        {
            new(Row("a", "2026-10-26T11:00:00Z", 30)), new(Row("b", "2026-10-26T11:01:00Z", 10, model: null)), new(Row("c", "2026-10-26T11:02:00Z", 20)),
        };
        var (grid, window) = Open(rows);
        Assert.Equal(["a", "b", "c"], grid.DisplayedRows.Select(r => r.Id));
        grid.SortBy("tokens");
        Assert.Equal(["b", "c", "a"], grid.DisplayedRows.Select(r => r.Id));
        grid.SortBy("tokens", descending: true);
        Assert.Equal(["a", "c", "b"], grid.DisplayedRows.Select(r => r.Id));
        grid.SortBy("model", descending: true);
        Assert.Equal("b", grid.DisplayedRows[^1].Id); // no model: empty, last in both directions
        window.Close();
    }

    [AvaloniaFact]
    public void A_refetch_keeps_the_row_controls_of_unchanged_requests()
    {
        var a = new RequestRowViewModel(Row("a", "2026-10-26T11:00:00Z", 30));
        var b = new RequestRowViewModel(Row("b", "2026-10-26T11:01:00Z", 10));
        var (grid, window) = Open([a, b]);
        var before = BodyRows(grid);
        grid.ItemsSource = [new RequestRowViewModel(Row("n", "2026-10-26T11:05:00Z", 5)), a, b];
        SceneCapture.Pump(2);
        var after = BodyRows(grid);
        Assert.Equal(3, after.Count);
        Assert.All(before, r => Assert.Contains(r, after));
        Assert.Equal(["n", "a", "b"], grid.DisplayedRows.Select(r => r.Id));
        window.Close();
    }

    [AvaloniaFact]
    public void Rows_are_at_least_52_px_and_grow_with_the_model_cell()
    {
        var (grid, window) = Open([new RequestRowViewModel(Row("a", "2026-10-26T11:00:00Z", 30)), new RequestRowViewModel(Row("b", "2026-10-26T11:01:00Z", 30, effort: "medium"))]);
        var rows = BodyRows(grid);
        Assert.All(rows, r => Assert.True(r.Bounds.Height >= RequestGrid.RowMinHeight));
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_on_a_row_activates_it_and_arrows_move_between_rows()
    {
        var rows = new List<RequestRowViewModel> { new(Row("a", "2026-10-26T11:00:00Z", 30)), new(Row("b", "2026-10-26T11:01:00Z", 10)) };
        var (grid, window) = Open(rows);
        RequestRowViewModel? opened = null;
        grid.RowActivated += r => opened = r;
        var body = BodyRows(grid);
        body[0].Focus(NavigationMethod.Tab);
        body[0].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down });
        Assert.True(body[1].IsFocused);
        body[1].RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Assert.Equal("b", opened?.Id);
        window.Close();
    }

    [AvaloniaFact]
    public void The_columns_keep_the_web_widths_and_scroll_sideways_when_they_do_not_fit()
    {
        var (grid, window) = Open([new RequestRowViewModel(Row("a", "2026-10-26T11:00:00Z", 30))], width: 900);
        var row = BodyRows(grid)[0];
        Assert.Equal(1352, row.Bounds.Width, 1); // 92+320+72+110+130+104+88+92+116+136+92
        window.Close();
    }

    [AvaloniaFact]
    public void In_a_narrow_window_the_sideways_scroll_bar_shows_at_the_bottom_of_the_visible_page_and_scrolls_the_columns()
    {
        // Issue #19: the bar sat under the last row (a page or more below the window) and showed only on hover, so the columns past the
        // window's edge looked cut off. Hosted like the shell does: the page's vertical scroll area with its edge fade.
        var ctx = new RequestTableContext { Clock = new FixedClock(DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), Provider = _ => null, TimeZone = TimeZoneInfo.Utc };
        var rows = Enumerable.Range(0, 30).Select(i => new RequestRowViewModel(Row($"r{i}", "2026-10-26T11:00:00Z", i))).ToList();
        var grid = RequestTable.Create(rows, ctx);
        var page = new TKCode.Arc.ScrollArea { FadeLength = 18, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid };
        var window = new Window { Content = page, Width = 900, Height = 600 };
        window.Show();
        SceneCapture.Pump(3);

        var scroller = grid.GetVisualDescendants().OfType<ScrollViewer>().First();
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Horizontal);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        Assert.True(bar.IsEffectivelyVisible);
        Assert.Equal(1, bar.Opacity); // shown without a pointer over the table
        // drawn inside the visible page, above its bottom fade — not under the 30th row
        var bottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), page)!.Value.Y;
        Assert.InRange(bottom, page.Bounds.Height - 18 - 1, page.Bounds.Height - 18 + 1);
        Assert.True(grid.BarShift < 0);

        // dragging its thumb moves the header and the rows together
        var header = grid.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "HeaderRow");
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
        var at = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Point(100, 0));
        window.MouseUp(at + new Point(100, 0), MouseButton.Left);
        SceneCapture.Pump(3);
        Assert.True(scroller.Offset.X > 50);
        Assert.Equal(-scroller.Offset.X, header.TranslatePoint(default, scroller)!.Value.X, tolerance: 1.0); // whole pixels (layout rounding)
        Assert.Equal(-scroller.Offset.X, BodyRows(grid)[0].TranslatePoint(default, scroller)!.Value.X, tolerance: 1.0);

        // scrolled to the table's end, the bar is back in its own place under the last row
        page.Offset = new Vector(0, page.Extent.Height);
        SceneCapture.Pump(3);
        Assert.Equal(0, grid.BarShift);
        window.Close();
    }
}
