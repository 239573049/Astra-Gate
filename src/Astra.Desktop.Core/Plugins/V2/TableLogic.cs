using System.Globalization;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>A table's sort: column key and direction ("asc" | "desc").</summary>
public sealed record TableSort(string Key, string Dir)
{
    public JsonObject ToJson() => new() { ["key"] = Key, ["dir"] = Dir };

    public static TableSort? From(JsonNode? node) =>
        node is JsonObject o && o["key"]?.ToString() is { Length: > 0 } key
            ? new TableSort(key, o["dir"]?.ToString() == "desc" ? "desc" : "asc")
            : null;
}

/// <summary>
/// What a plugin Table does locally, the same as the web's TableView (web/src/plugin-ui/render.tsx): the next sort when
/// a header is pressed, sorting rows when the table isn't server-side (no total), selection, and the page count.
/// </summary>
public static class TableLogic
{
    /// <summary>Pressing column <paramref name="key"/>: ascending first, then flips while it stays the sorted column.</summary>
    public static TableSort NextSort(TableSort? current, string key) =>
        new(key, current?.Key == key && current.Dir == "asc" ? "desc" : "asc");

    /// <summary>Rows in <paramref name="sort"/> order: numbers numerically, everything else as text (culture-aware); stable.</summary>
    public static List<JsonObject> Sort(IReadOnlyList<JsonObject> rows, TableSort? sort)
    {
        if (sort is null) return [.. rows];
        var indexed = rows.Select((r, i) => (Row: r, Index: i)).ToList();
        indexed.Sort((a, b) =>
        {
            var c = Compare(a.Row[sort.Key], b.Row[sort.Key]);
            if (sort.Dir == "desc") c = -c;
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return [.. indexed.Select(x => x.Row)];
    }

    private static int Compare(JsonNode? x, JsonNode? y)
    {
        if (Number(x) is { } a && Number(y) is { } b) return a.CompareTo(b);
        return string.Compare(Text(x), Text(y), CultureInfo.CurrentCulture, CompareOptions.None);
    }

    private static double? Number(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number ? PluginUiProtocol.ToDouble(v) : null;

    private static string Text(JsonNode? n) => n switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        _ => n.ToJsonString(),
    };

    /// <summary>A row's key: its <paramref name="rowKey"/> field, else its position.</summary>
    public static string KeyOf(JsonObject row, string rowKey, int index) => row[rowKey] switch
    {
        null => index.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        var n => n.ToJsonString(),
    };

    /// <summary>Selection after pressing row <paramref name="key"/>: single replaces, multiple toggles.</summary>
    public static List<string> Toggle(IReadOnlyList<string> selected, string key, bool multiple)
    {
        if (!multiple) return [key];
        return selected.Contains(key) ? [.. selected.Where(k => k != key)] : [.. selected, key];
    }

    /// <summary>The header checkbox: everything when not all are selected, else nothing.</summary>
    public static List<string> ToggleAll(IReadOnlyList<string> selected, IReadOnlyList<string> all) =>
        all.Count > 0 && all.All(selected.Contains) ? [] : [.. all];

    /// <summary>Pages for <paramref name="total"/> rows at <paramref name="pageSize"/> a page (at least one).</summary>
    public static int Pages(double total, double pageSize) =>
        (int)Math.Max(1, Math.Ceiling(total / (pageSize > 0 ? pageSize : 20)));
}
