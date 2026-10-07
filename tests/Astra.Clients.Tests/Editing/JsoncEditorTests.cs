using System.Text.Json.Nodes;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class JsoncEditorTests
{
    [Fact]
    public void Get_NestedValue_WithCommentsAndTrailingCommas()
    {
        const string text = """
            {
              // comment
              "env": {
                "A": "1",
              },
            }
            """;
        var editor = new JsoncEditor(text);
        Assert.Equal("1", editor.Get("env")?["A"]?.GetValue<string>());
        Assert.True(editor.Has("env.A"));
        Assert.False(editor.Has("env.B"));
    }

    [Fact]
    public void Set_ExistingMember_ReplacesValueSpanOnly()
    {
        const string text = "{\n  \"a\": 1, // keep\n  \"b\": 2\n}\n";
        var result = new JsoncEditor(text).Set("a", JsonValue.Create(42));
        Assert.Equal("{\n  \"a\": 42, // keep\n  \"b\": 2\n}\n", result.Text);
    }

    [Fact]
    public void Set_NewMember_AppendsWithMatchingIndent()
    {
        const string text = """
            {
              "env": {
                "A": "1"
              }
            }
            """;
        var result = new JsoncEditor(text).Set("env.B", JsonValue.Create("2"));
        Assert.Equal(
            """
            {
              "env": {
                "A": "1",
                "B": "2"
              }
            }
            """, result.Text);
    }

    [Fact]
    public void Set_NewMember_InEmptyInlineObject()
    {
        var result = new JsoncEditor("{\n  \"env\": {}\n}\n").Set("env.X", JsonValue.Create("1"));
        Assert.Equal("{\n  \"env\": { \"X\": \"1\" }\n}\n", result.Text);
    }

    [Fact]
    public void Set_CreatesIntermediateObjects()
    {
        var result = new JsoncEditor("{\n  \"a\": 1\n}\n").Set("x.y.z", JsonValue.Create(true));
        var node = result.Get("x.y.z");
        Assert.NotNull(node);
        Assert.True(node!.GetValue<bool>());
    }

    [Fact]
    public void Set_OnEmptyText_CreatesDocument()
    {
        var result = new JsoncEditor("").Set("a", JsonValue.Create("b"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":"b"}"""), JsonNode.Parse(result.Text)));
    }

    [Fact]
    public void Set_ObjectValue_ReplacesWholeSubtree()
    {
        const string text = "{\n  \"provider\": {\"old\": true} // note\n}\n";
        var result = new JsoncEditor(text).Set("provider", new JsonObject { ["npm"] = "@ai-sdk/openai-compatible" });
        Assert.Equal("{\n  \"provider\": {\"npm\":\"@ai-sdk/openai-compatible\"} // note\n}\n", result.Text);
    }

    [Fact]
    public void Remove_MiddleMember_KeepsCommasBalanced()
    {
        const string text = """
            {
              "a": 1,
              "b": 2,
              "c": 3
            }
            """;
        var result = new JsoncEditor(text).Remove("b");
        Assert.Equal(
            """
            {
              "a": 1,
              "c": 3
            }
            """, result.Text);
    }

    [Fact]
    public void Remove_LastMember_KeepsTrailingCommaDocumentValid()
    {
        const string text = """
            {
              "a": 1,
              "b": 2
            }
            """;
        var result = new JsoncEditor(text).Remove("b");
        // The comma the removal strands before '}' is a valid JSONC trailing comma; keeping it lets a
        // re-insert restore the original bytes exactly.
        Assert.Equal(
            """
            {
              "a": 1,
            }
            """, result.Text);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\": 1}"), JsonNode.Parse(result.Text,
            nodeOptions: new JsonNodeOptions(),
            documentOptions: new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true })));
    }

    [Fact]
    public void Remove_MultilineObjectMember_RemovesWholeBlock()
    {
        const string text = """
            {
              "keep": 1,
              "mine": {
                "x": 1,
                "y": 2
              },
              "after": 3
            }
            """;
        var result = new JsoncEditor(text).Remove("mine");
        Assert.Equal(
            """
            {
              "keep": 1,
              "after": 3
            }
            """, result.Text);
    }

    [Fact]
    public void Remove_InlineObject_CommaHandling()
    {
        var result = new JsoncEditor("{\"a\": 1, \"b\": {\"x\": 1}}\n").Remove("b");
        Assert.Equal("{\"a\": 1}\n", result.Text);
    }

    [Fact]
    public void Remove_KeepTrailingCommaDocumentValid()
    {
        const string text = """
            {
              "a": 1,
              "b": 2,
            }
            """;
        var result = new JsoncEditor(text).Remove("a");
        var options = new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"b\": 2}"), JsonNode.Parse(result.Text, nodeOptions: new JsonNodeOptions(), documentOptions: options)));
    }

    [Fact]
    public void Unicode_ValuesSurviveSplicing()
    {
        var result = new JsoncEditor("{\n  \"greeting\": \"héllo\"\n}\n").Set("emoji", JsonValue.Create(" ok 🚀"));
        Assert.Equal(" ok 🚀", result.Get("emoji")?.GetValue<string>());
        Assert.Equal("héllo", result.Get("greeting")?.GetValue<string>());
    }

    [Fact]
    public void SetRaw_RestoresOriginalFormatting()
    {
        const string original = """
            {
              "mine": {
                "x": 1,
                "y": 2
              }
            }
            """;
        var edited = new JsoncEditor(original).Set("mine", new JsonObject { ["z"] = 9 }).Text;
        Assert.NotEqual(original, edited);
        var restored = new JsoncEditor(edited).SetRaw("mine", "{\n    \"x\": 1,\n    \"y\": 2\n  }").Text;
        Assert.Equal(original, restored);
    }

    [Fact]
    public void InvalidJson_Throws()
    {
        // The constructor is lazy; the first operation parses and must reject a broken document.
        Assert.Throws<EditorException>(() => new JsoncEditor("{ invalid").Has("a"));
    }

    [Fact]
    public void RootNonObject_Throws()
    {
        Assert.Throws<EditorException>(() => new JsoncEditor("[1, 2]").Set("a", JsonValue.Create(1)));
    }

    [Fact]
    public void Crlf_KeepsNewlineStyle()
    {
        var result = new JsoncEditor("{\r\n  \"a\": 1\r\n}\r\n").Set("b", JsonValue.Create("2"));
        Assert.Equal("{\r\n  \"a\": 1,\r\n  \"b\": \"2\"\r\n}\r\n", result.Text);
    }
}
