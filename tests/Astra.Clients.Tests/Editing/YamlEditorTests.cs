using System.Text.Json.Nodes;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class YamlEditorTests
{
    private const string Hermes = """
        # Hermes Agent CLI Configuration
        _config_version: 50

        model:
          # Default model to use
          default: "anthropic/claude-opus-4.6"

          # Inference provider selection
          provider: "auto"
          base_url: "https://openrouter.ai/api/v1"

        terminal:
          backend: "local"   # keep me
          timeout: 180
        """;

    [Fact]
    public void Get_ReadsScalarsMappingsAndTypes()
    {
        var editor = new YamlEditor(Hermes);
        Assert.Equal("\"auto\"", editor.GetJsonText("model.provider"));
        Assert.Equal("180", editor.GetJsonText("terminal.timeout"));
        Assert.Equal("anthropic/claude-opus-4.6", editor.Get("model")!["default"]!.GetValue<string>());
        Assert.Null(editor.GetJsonText("model.api_key"));
        Assert.Equal("null", new YamlEditor("a:\n").GetJsonText("a"));
    }

    [Fact]
    public void Set_ExistingScalar_ReplacesOnlyThatLine()
    {
        var result = new YamlEditor(Hermes).SetRaw("model.provider", "\"custom\"");
        Assert.Equal(Hermes.Replace("provider: \"auto\"", "provider: \"custom\""), result.Text);
    }

    [Fact]
    public void Set_NewKey_AppendsAfterLastEntryOfThatMapping()
    {
        var result = new YamlEditor(Hermes).SetRaw("model.api_key", "\"astra-key\"");
        Assert.Contains("  base_url: \"https://openrouter.ai/api/v1\"\n  api_key: \"astra-key\"\n\nterminal:", result.Text);
        Assert.Contains("backend: \"local\"   # keep me", result.Text);
        // Removing it again gives the original bytes back.
        Assert.Equal(Hermes, result.Remove("model.api_key").Text);
    }

    [Fact]
    public void Set_NestedObject_RendersBlockYaml_AndRemoveRestores()
    {
        const string text = "logLevel: info\ndefaultModel: minimax/MiniMax-M3\n";
        var value = new JsonObject
        {
            ["name"] = "Astra",
            ["api"] = "openai-completions",
            ["options"] = new JsonObject { ["baseURL"] = "http://127.0.0.1:1/v1", ["apiKey"] = "k" },
            ["models"] = new JsonObject { ["gpt-4.1"] = new JsonObject { ["name"] = "GPT 4.1" }, ["yes"] = new JsonObject() },
        };
        var result = new YamlEditor(text).SetRaw("custom_provider.astra", value.ToJsonString());
        Assert.Equal("""
            logLevel: info
            defaultModel: minimax/MiniMax-M3
            custom_provider:
              astra:
                name: "Astra"
                api: "openai-completions"
                options:
                  baseURL: "http://127.0.0.1:1/v1"
                  apiKey: "k"
                models:
                  gpt-4.1:
                    name: "GPT 4.1"
                  "yes": {}

            """.Replace("\r\n", "\n"), result.Text);
        Assert.True(JsonNode.DeepEquals(value, result.Get("custom_provider.astra")));

        // Removing the only child leaves an empty mapping (not a null) behind for the caller to prune.
        var removed = result.Remove("custom_provider.astra");
        Assert.Equal("{}", removed.GetJsonText("custom_provider"));
        Assert.Equal(text, removed.Remove("custom_provider").Text);
    }

    [Fact]
    public void Set_IntoNullOrFlowMapping_RebuildsThatEntry()
    {
        var fromNull = new YamlEditor("model:\nother: 1\n").SetRaw("model.provider", "\"custom\"");
        Assert.Equal("model:\n  provider: \"custom\"\nother: 1\n", fromNull.Text);

        var fromFlow = new YamlEditor("model: {provider: auto}\n").SetRaw("model.base_url", "\"http://x/v1\"");
        Assert.Equal("auto", fromFlow.Get("model")!["provider"]!.GetValue<string>());
        Assert.Equal("http://x/v1", fromFlow.Get("model")!["base_url"]!.GetValue<string>());
    }

    [Fact]
    public void Set_OnEmptyOrCommentOnlyText_CreatesEntries()
    {
        Assert.Equal("model:\n  provider: \"custom\"\n", new YamlEditor("").SetRaw("model.provider", "\"custom\"").Text);
        Assert.Equal("# hi\nmodel:\n  provider: \"custom\"\n", new YamlEditor("# hi\n").SetRaw("model.provider", "\"custom\"").Text);
    }

    [Fact]
    public void Remove_MappingBlock_RemovesNestedLines()
    {
        var result = new YamlEditor(Hermes).Remove("model");
        Assert.DoesNotContain("base_url", result.Text);
        Assert.Contains("terminal:\n  backend: \"local\"   # keep me", result.Text);
        Assert.Null(result.Get("model"));
    }

    [Fact]
    public void Quote_EscapesSpecialCharacters()
    {
        var result = new YamlEditor("").SetRaw("k", "\"a\\\"b\\\\c\\nd: #e\"");
        Assert.Equal("a\"b\\c\nd: #e", result.Get("k")!.GetValue<string>());
    }

    [Fact]
    public void Crlf_KeepsNewlineStyle()
    {
        var result = new YamlEditor("a: 1\r\nb: 2\r\n").SetRaw("c", "\"x\"");
        Assert.Equal("a: 1\r\nb: 2\r\nc: \"x\"\r\n", result.Text);
        Assert.Equal("a: 1\r\nc: \"x\"\r\n", result.Remove("b").Text);
    }

    [Fact]
    public void InvalidOrNonMappingYaml_Throws()
    {
        Assert.Throws<EditorException>(() => new YamlEditor("a: [1, 2\n").Get("a"));
        Assert.Throws<EditorException>(() => new YamlEditor("- 1\n- 2\n").SetRaw("a", "1"));
    }
}
