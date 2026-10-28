using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Plugins;

/// <summary>A plugin's options as JSON text and back (web <c>OptionsSheet</c>).</summary>
public static class PluginOptions
{
    /// <summary>The text the editor starts with: the options set, else the package's example, else <c>{}</c>, indented by two spaces.</summary>
    public static string InitialText(JsonObject? options, JsonObject? example) => Indent(options ?? example ?? new JsonObject());

    /// <summary>The description under the sheet's title: nothing when options are set, else whether the text is an example or there is none.</summary>
    public static string? Description(JsonObject? options, JsonObject? example)
        => options is not null ? null : example is not null ? Loc.T("plugins.optionsExample") : Loc.T("plugins.optionsNone");

    /// <summary>
    /// Reads the editor's text (an empty text means <c>{}</c>). On failure <paramref name="error"/> is the sentence to show: the
    /// parser's own, or "options must be an object" for JSON that is not one.
    /// </summary>
    public static bool TryParse(string? text, out JsonObject? options, out string? error)
    {
        options = null;
        error = null;
        try
        {
            var node = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (node is JsonObject o)
            {
                options = o;
                return true;
            }
            error = Loc.T("plugins.optionsObject");
        }
        catch (JsonException e)
        {
            error = e.Message;
        }
        return false;
    }

    /// <summary>The PATCH body of an options save: <c>{"options": {...}}</c>, or <c>{"options": null}</c> to clear them.</summary>
    public static JsonObject PatchBody(JsonObject? options) => new() { ["options"] = options };

    /// <summary>The PATCH body of the enable switch.</summary>
    public static JsonObject EnabledBody(bool enabled) => new() { ["enabled"] = enabled };

    // Utf8JsonWriter directly: no JsonSerializerOptions, so nothing here depends on reflection (Native AOT).
    private static string Indent(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
