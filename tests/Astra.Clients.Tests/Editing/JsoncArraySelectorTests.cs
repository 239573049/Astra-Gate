using System.Text.Json.Nodes;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

/// <summary>Array-element selectors ("providers.[name=astra]") used for Copilot CLI's providers.json.</summary>
public class JsoncArraySelectorTests
{
    [Fact]
    public void SplitKeyPath_KeepsDotsInsideSelectors()
    {
        Assert.Equal(["models", "[provider=astra&id=gpt-4.1]"], JsoncEditor.SplitKeyPath("models.[provider=astra&id=gpt-4.1]"));
        Assert.Equal(["a", "b"], JsoncEditor.SplitKeyPath("a.b"));
    }

    [Fact]
    public void Selector_RejectsReservedCharacters()
    {
        Assert.Equal("[name=astra]", JsoncEditor.Selector(("name", "astra")));
        Assert.Throws<EditorException>(() => JsoncEditor.Selector(("id", "a&b")));
    }

    [Fact]
    public void Set_AppendsElement_ThenReplacesIt_AndRemoveRestoresBytes()
    {
        const string text = """
            {
              "providers": [
                {"name": "mine", "baseUrl": "http://localhost:11434/v1"}
              ]
            }
            """;
        var path = "providers." + JsoncEditor.Selector(("name", "astra"));
        var added = new JsoncEditor(text).SetRaw(path, "{\"name\":\"astra\",\"baseUrl\":\"http://127.0.0.1:1/v1\"}");
        Assert.Equal("http://127.0.0.1:1/v1", added.Get(path)!["baseUrl"]!.GetValue<string>());
        Assert.Equal("http://localhost:11434/v1", added.Get("providers.[name=mine]")!["baseUrl"]!.GetValue<string>());

        var replaced = added.SetRaw(path, "{\"name\":\"astra\",\"baseUrl\":\"http://127.0.0.1:2/v1\"}");
        Assert.Equal(2, JsonNode.Parse(replaced.Text)!["providers"]!.AsArray().Count);
        Assert.Equal("http://127.0.0.1:2/v1", replaced.Get(path)!["baseUrl"]!.GetValue<string>());

        // Strict JSON stays strict: no stranded trailing comma.
        Assert.Equal(text, replaced.Remove(path).Text);
    }

    [Fact]
    public void Set_CreatesMissingArray()
    {
        var result = new JsoncEditor("").SetRaw("models.[provider=astra&id=m.1]", "{\"id\":\"m.1\",\"provider\":\"astra\"}");
        var node = JsonNode.Parse(result.Text)!;
        Assert.Equal("m.1", node["models"]![0]!["id"]!.GetValue<string>());
        Assert.True(result.Has("models.[provider=astra&id=m.1]"));
        Assert.False(result.Has("models.[provider=other&id=m.1]"));
    }

    [Fact]
    public void Set_ValueNotMatchingSelector_FailsSelfCheck()
    {
        Assert.Throws<EditorException>(() => new JsoncEditor("{\"p\": []}").SetRaw("p.[name=astra]", "{\"name\":\"other\"}"));
    }
}
