using Astra.Desktop.Components.Kit;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Settings;

/// <summary>Shorthand for the rows the settings pages are made of (web <c>Row label detail icon</c> + children).</summary>
public static class RowBuilder
{
    /// <summary>A settings row with its trailing controls.</summary>
    public static SettingsRow Row(object? label, object? detail = null, params Control[] trailing)
    {
        var row = new SettingsRow { Label = label, Detail = detail };
        foreach (var c in trailing) row.Trailing.Add(c);
        return row;
    }

    /// <summary>A settings row whose explanation is an ⓘ tooltip on the label instead of a second line.</summary>
    public static SettingsRow Hinted(object? label, string? hint, params Control[] trailing)
    {
        var row = Row(label, null, trailing);
        row.Hint = hint;
        return row;
    }

    /// <summary>Gives <paramref name="row"/> an ⓘ tooltip; returns it.</summary>
    public static SettingsRow WithHint(this SettingsRow row, string? hint)
    {
        row.Hint = hint;
        return row;
    }

    /// <summary>Gives <paramref name="group"/> an ⓘ tooltip after its title; returns it.</summary>
    public static SettingsGroup WithHint(this SettingsGroup group, string? hint)
    {
        group.Hint = hint;
        return group;
    }

    /// <summary>A group with its rows.</summary>
    public static SettingsGroup Group(string? title, string? footer, params Control[] rows)
    {
        var group = new SettingsGroup { Title = title, Footer = footer };
        foreach (var r in rows) group.Rows.Add(r);
        return group;
    }
}
