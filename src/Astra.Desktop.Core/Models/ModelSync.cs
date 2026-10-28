using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Core.Models;

/// <summary>One leaf that changed (<c>DiffLine</c> of web/src/pages/ModelsPage.tsx); a null side means "absent" (the web's <c>undefined</c>).</summary>
/// <param name="Path">Dotted leaf path without the leading <c>pricing.</c> ("" for a scalar).</param>
/// <param name="Before">The old value, null when it did not exist.</param>
/// <param name="After">The new value, null when it is gone.</param>
public sealed record SyncDiffLine(string Path, JsonNode? Before, JsonNode? After)
{
    /// <summary>The value as the diff prints it: a string as written, anything else as compact JSON (<c>leafText</c>).</summary>
    public static string LeafText(JsonNode? value)
        => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : PricingDocument.Canonical(value);
}

/// <summary>
/// Pure logic of the model sync preview sheet (<c>SyncDiff</c> / <c>SyncGroups</c> / <c>SyncSheet</c> in web/src/pages/ModelsPage.tsx):
/// leaf diff of one change, grouping by model, the tri-state checkboxes and what is applied.
/// </summary>
public static class ModelSync
{
    /// <summary>How many diff lines a row shows before "+N more" (<c>SYNC_DIFF_LINES</c>).</summary>
    public const int DiffLinesShown = 4;

    /// <summary>The leaf paths of a JSON value (<c>flatten</c>): objects with members recurse; everything else non-null is a leaf; null/absent is dropped.</summary>
    public static List<KeyValuePair<string, JsonNode>> Flatten(JsonNode? value, string prefix = "")
    {
        var output = new List<KeyValuePair<string, JsonNode>>();
        FlattenInto(value, prefix, output);
        return output;
    }

    private static void FlattenInto(JsonNode? value, string prefix, List<KeyValuePair<string, JsonNode>> output)
    {
        if (value is JsonObject { Count: > 0 } o)
        {
            foreach (var (k, v) in o) FlattenInto(v, prefix.Length > 0 ? prefix + "." + k : k, output);
        }
        else if (value is not null)
        {
            var at = output.FindIndex(kv => kv.Key == prefix);
            var leaf = new KeyValuePair<string, JsonNode>(prefix, value);
            if (at >= 0) output[at] = leaf; else output.Add(leaf);
        }
    }

    /// <summary>Only the leaves that differ, so a price row reads "base.input 0.5 → 0.6" (<c>diffLines</c>).</summary>
    public static List<SyncDiffLine> DiffLines(JsonNode? before, JsonNode? after)
    {
        var a = Flatten(before);
        var b = Flatten(after);
        var paths = b.Select(kv => kv.Key).Concat(a.Select(kv => kv.Key).Where(k => b.All(x => x.Key != k))).ToList();
        var lines = new List<SyncDiffLine>();
        foreach (var path in paths)
        {
            var left = a.FirstOrDefault(kv => kv.Key == path).Value;
            var right = b.FirstOrDefault(kv => kv.Key == path).Value;
            if (PricingDocument.Canonical(left) == PricingDocument.Canonical(right)) continue;
            lines.Add(new SyncDiffLine(path.StartsWith("pricing.", StringComparison.Ordinal) ? path["pricing.".Length..] : path, left, right));
        }
        return lines;
    }

    /// <summary>The row's tooltip: both sides pretty-printed, "∅" for an empty side (<c>title</c> of <c>SyncDiff</c>).</summary>
    public static string Tooltip(JsonNode? before, JsonNode? after) => $"{Pretty(before)}\n→\n{Pretty(after)}";

    private static string Pretty(JsonNode? node)
    {
        // pretty(): a string value is parsed as JSON first and printed indented when it is JSON, else as written
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var s = v.GetValue<string>();
            try
            {
                return s.Length == 0 ? "∅" : PricingDocument.PrettyJson(JsonNode.Parse(s));
            }
            catch (JsonException)
            {
                return s;
            }
        }
        var text = PricingDocument.PrettyJson(node);
        return text.Length == 0 ? "∅" : text;
    }

    /// <summary>The label of a change's badge: "new model", "price · openai" or "field · displayName".</summary>
    public static string Label(SyncChangeDto c) => c.Kind switch
    {
        "new_model" => Loc.T("models.sync.newModel"),
        "price" => Loc.T("models.sync.price", ("key", c.PriceKey ?? "")),
        _ => Loc.T("models.sync.field", ("field", c.Field ?? "")),
    };

    /// <summary>The changes grouped by model, in the order the models first appear.</summary>
    public static List<SyncGroup> Group(IEnumerable<SyncChangeDto> changes)
    {
        var groups = new List<SyncGroup>();
        foreach (var c in changes)
        {
            var group = groups.Find(g => g.ModelId == c.ModelId);
            if (group is null) groups.Add(group = new SyncGroup(c.ModelId, []));
            group.Items.Add(c);
        }
        return groups;
    }

    /// <summary>The ids ticked when a preview arrives: every change that does not overwrite a user edit.</summary>
    public static HashSet<string> InitialSelection(SyncPreviewDto preview)
        => preview.Changes.Where(c => !c.SkippedBecauseUserModified).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
}

/// <summary>The changes of one catalog model.</summary>
/// <param name="ModelId">The model.</param>
/// <param name="Items">Its changes in preview order.</param>
public sealed record SyncGroup(string ModelId, List<SyncChangeDto> Items)
{
    /// <summary>A single change shows as one row without a collapse arrow.</summary>
    public bool IsSingle => Items.Count == 1;

    /// <summary>The change ids of the group.</summary>
    public List<string> Ids => Items.Select(c => c.Id).ToList();
}

/// <summary>A checkbox state over some change ids.</summary>
public enum CheckState
{
    /// <summary>None ticked.</summary>
    Unchecked,

    /// <summary>All ticked.</summary>
    Checked,

    /// <summary>Some ticked.</summary>
    Mixed,
}

/// <summary>The ticked changes of a sync preview and the operations the sheet's checkboxes do on them (<c>stateOf</c> / <c>setMany</c>).</summary>
public sealed class SyncSelection
{
    private readonly HashSet<string> _checked;

    /// <summary>A selection starting with <paramref name="ids"/> ticked.</summary>
    public SyncSelection(IEnumerable<string>? ids = null) => _checked = ids is null ? [] : new HashSet<string>(ids, StringComparer.Ordinal);

    /// <summary>How many changes are ticked.</summary>
    public int Count => _checked.Count;

    /// <summary>Whether a change is ticked.</summary>
    public bool Contains(string id) => _checked.Contains(id);

    /// <summary>The ticked ids, in a stable order (what <c>apply</c> sends).</summary>
    public List<string> Ids() => _checked.Order(StringComparer.Ordinal).ToList();

    /// <summary>The state of a checkbox that covers <paramref name="ids"/>.</summary>
    public CheckState StateOf(IReadOnlyCollection<string> ids)
    {
        var n = ids.Count(_checked.Contains);
        return n == 0 ? CheckState.Unchecked : n == ids.Count ? CheckState.Checked : CheckState.Mixed;
    }

    /// <summary>Ticks or unticks every id.</summary>
    public void SetMany(IEnumerable<string> ids, bool on)
    {
        foreach (var id in ids)
        {
            if (on) _checked.Add(id);
            else _checked.Remove(id);
        }
    }

    /// <summary>A checkbox click: a mixed or unchecked box becomes fully ticked (<c>setMany(ids, s !== false)</c>), a checked one clears.</summary>
    public void Toggle(IReadOnlyCollection<string> ids) => SetMany(ids, StateOf(ids) != CheckState.Checked);

    /// <summary>"3 of 12 changes selected · 5 models" (<c>models.sync.selectedOf</c>).</summary>
    public string Summary(int total, int models) => Loc.T("models.sync.selectedOf", ("n", Count), ("total", total), ("models", models));
}
