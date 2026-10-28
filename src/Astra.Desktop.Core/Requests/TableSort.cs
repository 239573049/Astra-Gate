using System.Globalization;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// The row order of web Arc's <c>SortableDataTable</c> (web/src/components/arc/sortable-data-table/sortable-data-table.tsx, <c>sortedRows</c>):
/// empty values (null / "") stay at the bottom in both directions, numbers and dates compare numerically, text with the "en" collator
/// (<c>numeric: true, sensitivity: "base"</c>: digit runs as numbers, case and accents ignored), and equal values keep their input order.
/// </summary>
public static class TableSort
{
    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("en").CompareInfo;
    private static readonly CompareOptions TextOptions = ProbeOptions();

    /// <summary>
    /// <paramref name="rows"/> ordered by <paramref name="value"/>; unsorted (input order) when <paramref name="value"/> is null.
    /// </summary>
    public static List<T> Order<T>(IReadOnlyList<T> rows, Func<T, object?>? value, bool descending)
    {
        if (value is null) return [.. rows];
        var keyed = rows.Select((row, index) => (row, index, key: value(row))).ToList();
        keyed.Sort((a, b) =>
        {
            var emptyA = IsEmpty(a.key);
            var emptyB = IsEmpty(b.key);
            if (emptyA || emptyB) return emptyA == emptyB ? a.index - b.index : emptyA ? 1 : -1;
            var result = CompareValues(a.key!, b.key!);
            if (descending) result = -result;
            return result != 0 ? result : a.index - b.index;
        });
        return keyed.Select(k => k.row).ToList();
    }

    /// <summary>Web <c>isEmpty</c>: null or the empty string.</summary>
    public static bool IsEmpty(object? value) => value is null || value is string { Length: 0 };

    /// <summary>Ascending comparison of two non-empty values.</summary>
    public static int CompareValues(object a, object b)
    {
        if (AsNumber(a) is { } x && AsNumber(b) is { } y) return x.CompareTo(y);
        return Natural(Convert.ToString(a, CultureInfo.InvariantCulture) ?? "", Convert.ToString(b, CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>A number, or a date as its epoch milliseconds (web <c>comparable</c>: <c>Date.getTime()</c>).</summary>
    private static double? AsNumber(object v) => v switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        DateTimeOffset t => t.ToUnixTimeMilliseconds(),
        DateTime t => new DateTimeOffset(t).ToUnixTimeMilliseconds(),
        _ => null,
    };

    /// <summary>Natural text order: digit runs compare as numbers, other runs ignore case and accents.</summary>
    public static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var digitA = char.IsAsciiDigit(a[i]);
            var digitB = char.IsAsciiDigit(b[j]);
            if (digitA && digitB)
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var runA = a.AsSpan(si, i - si).TrimStart('0');
                var runB = b.AsSpan(sj, j - sj).TrimStart('0');
                if (runA.Length != runB.Length) return runA.Length.CompareTo(runB.Length);
                var digits = runA.SequenceCompareTo(runB);
                if (digits != 0) return Math.Sign(digits);
                continue;
            }
            // Digits sort before letters, as in the ICU root collation.
            if (digitA != digitB) return digitA ? -1 : 1;
            int startA = i, startB = j;
            while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
            while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
            var text = Compare.Compare(a, startA, i - startA, b, startB, j - startB, TextOptions);
            if (text != 0) return Math.Sign(text);
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static CompareOptions ProbeOptions()
    {
        // Invariant globalization (possible in AOT builds) may not support linguistic options.
        try
        {
            _ = Compare.Compare("a", "B", CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            return CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        }
        catch (Exception)
        {
            return CompareOptions.OrdinalIgnoreCase;
        }
    }
}
