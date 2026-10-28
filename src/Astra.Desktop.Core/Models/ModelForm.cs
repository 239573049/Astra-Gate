using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Core.Models;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Core.Models;

/// <summary>The seven capability checkboxes of the detail page, in the web's order (<c>CAPABILITIES</c>): the property and its i18n key.</summary>
public static class ModelCapabilityList
{
    /// <summary>Capability key (the JSON property) and the catalog key of its label.</summary>
    public static readonly IReadOnlyList<(string Key, string LabelKey)> All =
    [
        ("vision", "cap.vision"), ("tools", "cap.tools"), ("reasoning", "cap.reasoning"), ("promptCache", "cap.promptCache"),
        ("audio", "cap.audio"), ("pdf", "cap.pdf"), ("structuredOutput", "cap.structuredOutput"),
    ];

    /// <summary>Reads a capability flag by key.</summary>
    public static bool? Get(ModelCapabilities c, string key) => key switch
    {
        "vision" => c.Vision, "tools" => c.Tools, "reasoning" => c.Reasoning, "promptCache" => c.PromptCache,
        "audio" => c.Audio, "pdf" => c.Pdf, "structuredOutput" => c.StructuredOutput, _ => null,
    };

    /// <summary>A copy of <paramref name="c"/> with one flag set.</summary>
    public static ModelCapabilities With(ModelCapabilities c, string key, bool value)
    {
        var copy = c.Clone();
        switch (key)
        {
            case "vision": copy.Vision = value; break;
            case "tools": copy.Tools = value; break;
            case "reasoning": copy.Reasoning = value; break;
            case "promptCache": copy.PromptCache = value; break;
            case "audio": copy.Audio = value; break;
            case "pdf": copy.Pdf = value; break;
            case "structuredOutput": copy.StructuredOutput = value; break;
        }
        return copy;
    }

    /// <summary>Whether two capability sets have the same flags (a null flag differs from false, like <c>JSON.stringify</c> in the web's dirty check).</summary>
    public static bool Same(ModelCapabilities a, ModelCapabilities b) => All.All(c => Get(a, c.Key) == Get(b, c.Key));
}

/// <summary>
/// The editable state of the model detail page (<c>ModelInput</c> built by <c>toInput</c> in web/src/pages/ModelDetailPage.tsx):
/// what the form shows, whether it differs from the saved model (the "dirty" flag that enables Save / Revert) and the request body.
/// Immutable; every edit is a <c>With…</c> copy so the page keeps the saved base and the working copy side by side.
/// </summary>
public sealed partial record ModelForm(
    string DisplayName, string Vendor, string? Family, string AliasText, long? ContextWindow, long? MaxOutputTokens,
    ModelCapabilities Capabilities, JsonNode? Pricing, bool Enabled)
{
    /// <summary>The form of a saved model (<c>toInput</c> and the <c>aliases.join(', ')</c> of the alias box).</summary>
    public static ModelForm From(ModelDto m) => new(
        m.DisplayName, m.Vendor, m.Family, string.Join(", ", m.Aliases), m.ContextWindow, m.MaxOutputTokens,
        m.Capabilities.Clone(), m.Pricing?.DeepClone(), m.Enabled);

    /// <summary>The aliases typed in the alias box: split on commas and line breaks, trimmed, empties dropped.</summary>
    public IReadOnlyList<string> Aliases => AliasSeparators().Split(AliasText).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>The request body of <c>PUT /api/models/{id}</c>.</summary>
    public ModelInput ToInput() => new()
    {
        DisplayName = DisplayName,
        Vendor = Vendor,
        Family = Family,
        Aliases = Aliases.ToList(),
        ContextWindow = ContextWindow,
        MaxOutputTokens = MaxOutputTokens,
        Capabilities = Capabilities.Clone(),
        Pricing = Pricing?.DeepClone(),
        Enabled = Enabled,
    };

    /// <summary>The model's family; an empty box means none (<c>e.target.value || null</c>).</summary>
    public ModelForm WithFamily(string? text) => this with { Family = string.IsNullOrEmpty(text) ? null : text };

    /// <summary>One capability checkbox changed.</summary>
    public ModelForm WithCapability(string key, bool value) => this with { Capabilities = ModelCapabilityList.With(Capabilities, key, value) };

    /// <summary>Whether this working copy differs from <paramref name="saved"/> (<c>JSON.stringify(current) !== JSON.stringify(base)</c>; aliases compare as the parsed list, prices as the same decimals).</summary>
    public bool IsDirty(ModelForm saved)
        => DisplayName != saved.DisplayName || Vendor != saved.Vendor || Family != saved.Family
           || !Aliases.SequenceEqual(saved.Aliases) || ContextWindow != saved.ContextWindow || MaxOutputTokens != saved.MaxOutputTokens
           || !ModelCapabilityList.Same(Capabilities, saved.Capabilities) || !PricingDocument.Same(Pricing, saved.Pricing) || Enabled != saved.Enabled;

    [GeneratedRegex(@"[,\n]")]
    private static partial Regex AliasSeparators();
}

/// <summary>The "new model" sheet (<c>CreateModelSheet</c> of web/src/pages/ModelsPage.tsx): its fields and the request they make.</summary>
public sealed class CreateModelDraft
{
    /// <summary>The catalog id typed in the first box.</summary>
    public string Id { get; set; } = "";

    /// <summary>Display name; empty falls back to the id.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Vendor key (required).</summary>
    public string Vendor { get; set; } = "";

    /// <summary>Context window in tokens.</summary>
    public long? ContextWindow { get; set; }

    /// <summary>Base input price per 1M tokens (decimal).</summary>
    public decimal? InputPrice { get; set; }

    /// <summary>Base output price per 1M tokens (decimal).</summary>
    public decimal? OutputPrice { get; set; }

    /// <summary>Whether Create is enabled: an id and a vendor are typed (<c>!id.trim() || !vendor.trim()</c> disables it).</summary>
    public bool CanSubmit => Id.Trim().Length > 0 && Vendor.Trim().Length > 0;

    /// <summary>The request body of <c>POST /api/models</c>; null while <see cref="CanSubmit"/> is false.</summary>
    public ModelInput? ToInput()
    {
        if (!CanSubmit) return null;
        var id = Id.Trim();
        JsonNode? pricing = null;
        if (InputPrice is not null || OutputPrice is not null)
        {
            // { currency: 'USD', unit: 'per_1m_tokens', base: { input, output } }: a missing side is sent as null
            pricing = PricingDocument.Empty().WithBasePrice("input", InputPrice).WithBasePrice("output", OutputPrice).ToNode();
        }
        return new ModelInput
        {
            Id = id,
            DisplayName = DisplayName.Trim().Length > 0 ? DisplayName.Trim() : id,
            Vendor = Vendor.Trim(),
            ContextWindow = ContextWindow,
            Pricing = pricing,
        };
    }

    /// <summary>What the sheet clears after a successful create (the web keeps vendor, context and prices): the id and the display name.</summary>
    public void ResetAfterCreate()
    {
        Id = "";
        DisplayName = "";
    }
}
