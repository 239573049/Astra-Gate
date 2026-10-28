using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Settings;

/// <summary>JSON as text for display (a delivery's payload), without reflection: nodes write themselves to a <see cref="Utf8JsonWriter"/>.</summary>
public static class JsonText
{
    /// <summary>The node as indented JSON (web <c>JSON.stringify(payload, null, 2)</c>); "null" for none.</summary>
    public static string Indented(JsonNode? node)
    {
        if (node is null) return "null";
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
