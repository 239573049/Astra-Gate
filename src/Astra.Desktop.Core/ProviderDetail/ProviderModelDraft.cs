using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>Where a provider model's field comes from, as the edit sheet's badge shows it (web <c>FieldOrigin</c>).</summary>
public enum FieldOrigin
{
    /// <summary>Set on this provider model.</summary>
    Overridden,

    /// <summary>Taken from the linked system model.</summary>
    Inherited,

    /// <summary>Nowhere.</summary>
    Unset,
}

/// <summary>
/// The working copy of the provider-model edit sheet (web <c>ProviderModelSheet</c>): the overrides object, the linked system model and the
/// "custom price" switch. The overrides are kept as the JSON object the server sends (nulls left out), so fields the sheet does not edit
/// (capabilities) go back unchanged. Immutable: edits make copies.
/// </summary>
public sealed record ProviderModelDraft(ProviderModelDto Model, JsonObject Overrides, string Link, bool CustomPrice)
{
    /// <summary>The fields with an inherit / override control.</summary>
    public static readonly IReadOnlyList<string> Fields = ["displayName", "contextWindow", "maxOutputTokens"];

    /// <summary>The draft of a saved row (web <c>useEffect([pm])</c>).</summary>
    public static ProviderModelDraft From(ProviderModelDto pm)
    {
        var o = SavedOverrides(pm);
        return new ProviderModelDraft(pm, o, pm.SystemModelId ?? "", o["pricing"] is not null);
    }

    /// <summary>The saved overrides as JSON (null fields dropped).</summary>
    public static JsonObject SavedOverrides(ProviderModelDto pm)
    {
        var node = JsonSerializer.SerializeToNode(pm.Overrides, JsonContexts.Info<ModelOverridesDto>(Json.Api)) as JsonObject ?? new JsonObject();
        foreach (var key in node.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList()) node.Remove(key);
        return node;
    }

    /// <summary>The overrides to save: the pricing only while the custom price is on (web <c>overrides</c>).</summary>
    public JsonObject Effective()
    {
        var o = (JsonObject)Overrides.DeepClone();
        var pricing = CustomPrice ? o["pricing"]?.DeepClone() : null;
        o.Remove("pricing");
        o["pricing"] = pricing;
        return o;
    }

    /// <summary>Whether the draft differs from the saved row (web <c>dirty</c>).</summary>
    public bool IsDirty
    {
        get
        {
            var saved = SavedOverrides(Model);
            var pricing = saved["pricing"]?.DeepClone();
            saved.Remove("pricing");
            saved["pricing"] = pricing;
            return PricingDocument.CompactJson(Effective()) != PricingDocument.CompactJson(saved) || Link != (Model.SystemModelId ?? "");
        }
    }

    /// <summary>The PATCH body (web <c>save</c>): overrides plus the link (empty = unlinked).</summary>
    public JsonObject Body() => new() { ["overrides"] = Effective(), ["systemModelId"] = Link.Length > 0 ? Link : null };

    /// <summary>The value of an override field, or null.</summary>
    public JsonNode? Get(string key) => Overrides[key];

    /// <summary>A copy with an override set (null removes it = inherit).</summary>
    public ProviderModelDraft With(string key, JsonNode? value)
    {
        var o = (JsonObject)Overrides.DeepClone();
        if (value is null) o.Remove(key);
        else o[key] = value;
        return this with { Overrides = o };
    }

    /// <summary>The badge of a field (web <c>OriginBadge origin=…</c>): overridden in the draft, else what the server says, a server "overridden" read as inherited.</summary>
    public FieldOrigin OriginOf(string key)
    {
        if (Overrides[key] is not null) return FieldOrigin.Overridden;
        Model.Origins.TryGetValue(key, out var o);
        return o switch
        {
            "overridden" or "inherited" => FieldOrigin.Inherited,
            _ => FieldOrigin.Unset,
        };
    }

    /// <summary>
    /// Turns the custom price on or off (web <c>onChange</c> of the switch): turning it on with no price yet starts from the effective price,
    /// or an empty USD per-1M schedule.
    /// </summary>
    public ProviderModelDraft WithCustomPrice(bool on)
    {
        var next = this with { CustomPrice = on };
        if (on && Overrides["pricing"] is null)
            next = next.With("pricing", Model.EffectivePricing?.DeepClone() ?? new JsonObject
            {
                ["currency"] = "USD", ["unit"] = "per_1m_tokens", ["base"] = new JsonObject { ["input"] = null, ["output"] = null },
            });
        return next;
    }
}
