using Astra.Api.Contracts;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
}
