using System.Text.Json.Nodes;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

// Gemini's `parameters` and `responseSchema` are an OpenAPI-subset Schema; the IR holds JSON Schema (GeminiSchemas).
public class GeminiSchemaTests
{
    // Compared as compact text, so a change in the order of properties or keywords shows too.
    private static string Convert(string schema) => GeminiSchemas.ToJsonSchema(JsonNode.Parse(schema)!).ToJsonString();

    [Theory]
    [InlineData("STRING", "string")]
    [InlineData("NUMBER", "number")]
    [InlineData("INTEGER", "integer")]
    [InlineData("BOOLEAN", "boolean")]
    [InlineData("ARRAY", "array")]
    [InlineData("OBJECT", "object")]
    [InlineData("NULL", "null")]
    [InlineData("object", "object")] // some clients already write lower case
    public void Type_Names_Are_Lower_Case(string written, string expected) =>
        Assert.Equal($$"""{"type":"{{expected}}"}""", Convert($$"""{"type":"{{written}}"}"""));

    [Fact]
    public void An_Unspecified_Type_Is_No_Type() =>
        Assert.Equal("""{"description":"any value"}""", Convert("""{"type":"TYPE_UNSPECIFIED","description":"any value"}"""));

    [Fact]
    public void Properties_Items_And_Alternatives_Are_Converted_In_Place()
    {
        Assert.Equal(
            """{"type":"object","properties":{"xs":{"type":"array","items":{"type":"string"}},"u":{"anyOf":[{"type":"string"},{"type":"number"}]}}}""",
            Convert("""{"type":"OBJECT","properties":{"xs":{"type":"ARRAY","items":{"type":"STRING"}},"u":{"anyOf":[{"type":"STRING"},{"type":"NUMBER"}]}}}"""));
    }

    [Fact]
    public void The_Order_Of_Keywords_And_Properties_Is_Kept()
    {
        Assert.Equal(
            """{"required":["a"],"type":"object","properties":{"b":{"type":"string"},"a":{"type":"string"}}}""",
            Convert("""{"required":["a"],"type":"OBJECT","properties":{"b":{"type":"STRING"},"a":{"type":"STRING"}}}"""));
    }

    [Fact]
    public void A_Property_Named_Like_A_Keyword_Is_A_Property()
    {
        Assert.Equal(
            """{"type":"object","properties":{"type":{"type":"string"},"nullable":{"type":"boolean"},"propertyOrdering":{"type":"string"}}}""",
            Convert("""{"type":"OBJECT","properties":{"type":{"type":"STRING"},"nullable":{"type":"BOOLEAN"},"propertyOrdering":{"type":"STRING"}}}"""));
    }

    [Theory]
    [InlineData("""{"type":"STRING","nullable":true}""", """{"type":["string","null"]}""")]
    [InlineData("""{"type":"ARRAY","items":{"type":"STRING","nullable":true},"nullable":true}""", """{"type":["array","null"],"items":{"type":["string","null"]}}""")]
    [InlineData("""{"type":"STRING","enum":["a","b"],"nullable":true}""", """{"type":["string","null"],"enum":["a","b",null]}""")]
    [InlineData("""{"type":"STRING","enum":["a",null],"nullable":true}""", """{"type":["string","null"],"enum":["a",null]}""")]
    [InlineData("""{"anyOf":[{"type":"STRING"},{"type":"NUMBER"}],"nullable":true}""", """{"anyOf":[{"type":"string"},{"type":"number"},{"type":"null"}]}""")]
    [InlineData("""{"anyOf":[{"type":"STRING"},{"type":"NULL"}],"nullable":true}""", """{"anyOf":[{"type":"string"},{"type":"null"}]}""")]
    [InlineData("""{"type":"NULL","nullable":true}""", """{"type":"null"}""")]
    [InlineData("""{"type":"STRING","nullable":false}""", """{"type":"string"}""")]
    public void Nullable_Becomes_A_Null_Type(string schema, string expected) => Assert.Equal(expected, Convert(schema));

    [Fact]
    public void The_Property_Ordering_Hint_Is_Dropped() =>
        Assert.Equal("""{"type":"object","properties":{"b":{"type":"string"},"a":{"type":"string"}}}""",
            Convert("""{"type":"OBJECT","properties":{"b":{"type":"STRING"},"a":{"type":"STRING"}},"propertyOrdering":["a","b"]}"""));

    [Fact]
    public void Other_Keywords_Go_Through_As_Written() =>
        Assert.Equal(
            """{"type":"integer","format":"int32","description":"d","minimum":1,"maximum":9,"example":3,"default":2,"enum":[1,2,3]}""",
            Convert("""{"type":"INTEGER","format":"int32","description":"d","minimum":1,"maximum":9,"example":3,"default":2,"enum":[1,2,3]}"""));

    [Theory]
    [InlineData("true")]
    [InlineData("""{"type":["STRING","NULL"]}""")] // not what Gemini's Schema allows; not this method's to reinterpret
    [InlineData("{}")]
    public void What_Is_Not_A_Gemini_Schema_Is_Left_Alone(string schema) => Assert.Equal(schema, Convert(schema));

    [Fact]
    public void The_Input_Is_Not_Changed()
    {
        const string text = """{"type":"OBJECT","nullable":true,"properties":{"a":{"type":"STRING","nullable":true}},"propertyOrdering":["a"]}""";
        var input = JsonNode.Parse(text)!;

        var converted = GeminiSchemas.ToJsonSchema(input);

        Assert.Equal(text, input.ToJsonString());
        Assert.NotSame(input, converted);
    }
}
