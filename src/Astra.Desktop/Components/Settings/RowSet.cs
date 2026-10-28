using Astra.Desktop.Components.Kit;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// A run of rows a group shows that comes and goes with data (listen address rows, a password editor): the parts that own it expose their
/// current rows, and the group takes them from there. Rows are the same instances each time, so what a row holds (a typed password)
/// survives the group being reassembled.
/// </summary>
public interface IRowSet
{
    /// <summary>The rows to show now, in order.</summary>
    IReadOnlyList<Control> Rows { get; }

    /// <summary>The set of rows changed (a row appeared or went away); the group re-reads <see cref="Rows"/>.</summary>
    event Action? RowsChanged;
}

/// <summary>Group assembly from fixed rows and <see cref="IRowSet"/>s.</summary>
public static class RowAssembly
{
    /// <summary>
    /// Makes <paramref name="group"/> show <paramref name="desired"/>, touching it only when the sequence differs (the group redraws its
    /// separators on every collection change).
    /// </summary>
    public static void Sync(SettingsGroup group, IReadOnlyList<Control> desired)
    {
        if (group.Rows.Count == desired.Count && group.Rows.Zip(desired).All(p => ReferenceEquals(p.First, p.Second))) return;
        group.Rows.Clear();
        foreach (var row in desired) group.Rows.Add(row);
    }

    /// <summary>A flat list from rows and row sets.</summary>
    public static List<Control> Flatten(params object?[] parts)
    {
        var list = new List<Control>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case null: break;
                case IRowSet set: list.AddRange(set.Rows); break;
                case Control c: list.Add(c); break;
                case IEnumerable<Control> many: list.AddRange(many); break;
            }
        }
        return list;
    }
}
