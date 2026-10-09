using System.Text.Json.Nodes;
using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

/// <summary>YAML documents whose root is a sequence of mappings (DeepSeek Harness's cordis.patch.yml), addressed by [id=…].</summary>
public class YamlRowEditorTests
{
    private const string Patch = """
        # Your patch layer
        - id: agent-default-model
          name: "@deepseek-ai/dsh-agent-default-model"
          config:
            provider: deepseek-account
            model: deepseek-flash
            reasoningEffort: high
        # keep me
        - id: ui-chat
          name: "@deepseek-ai/dsh-client-ui-chat"
          config:
            transcriptView: detailed
        """ + "\n";

    private static string Row(string id) => JsoncEditor.Selector(("id", id));

    [Fact]
    public void Reads_Rows_And_Nested_Values()
    {
        var editor = new YamlEditor(Patch);
        Assert.True(editor.Has(Row("ui-chat")));
        Assert.False(editor.Has(Row("llm-pi-ai")));
        Assert.Equal("\"deepseek-flash\"", editor.GetJsonText(Row("agent-default-model") + ".config.model"));
        Assert.Null(editor.GetJsonText(Row("agent-default-model") + ".config.nope"));
        Assert.Null(editor.GetJsonText(Row("llm-pi-ai") + ".config"));
        Assert.Equal("ui-chat", editor.Get(Row("ui-chat"))!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Set_Inside_A_Row_Rewrites_Only_That_Row()
    {
        var updated = new YamlEditor(Patch).SetRaw(Row("agent-default-model") + ".config.model", "\"astra-model\"").Text;
        Assert.Contains("# Your patch layer\n", updated);
        Assert.Contains("# keep me\n- id: ui-chat\n  name: \"@deepseek-ai/dsh-client-ui-chat\"\n  config:\n    transcriptView: detailed\n", updated);
        var doc = YamlEditor.ParseToJson(updated)!.AsArray();
        Assert.Equal("astra-model", doc[0]!["config"]!["model"]!.GetValue<string>());
        Assert.Equal("high", doc[0]!["config"]!["reasoningEffort"]!.GetValue<string>());
        Assert.Equal(2, doc.Count);
    }

    [Fact]
    public void Set_Creates_Nested_Containers_In_A_Row_And_Appends_New_Rows()
    {
        var withProvider = new YamlEditor(Patch)
            .SetRaw(Row("ui-chat") + ".config.extra.deep", "{\"a\":[1,2]}")
            .SetRaw(Row("llm-pi-ai"), "{\"id\":\"llm-pi-ai\",\"name\":\"@deepseek-ai/dsh-llm-pi-ai\"}")
            .SetRaw(Row("llm-pi-ai") + ".config.providers.astra", "{\"api\":\"openai-completions\",\"models\":[{\"id\":\"m\"}]}");
        var doc = YamlEditor.ParseToJson(withProvider.Text)!.AsArray();
        Assert.Equal(["agent-default-model", "ui-chat", "llm-pi-ai"], doc.Select(r => r!["id"]!.GetValue<string>()));
        Assert.Equal(2, doc[1]!["config"]!["extra"]!["deep"]!["a"]!.AsArray().Count);
        Assert.Equal("m", doc[2]!["config"]!["providers"]!["astra"]!["models"]![0]!["id"]!.GetValue<string>());
        Assert.Contains("# keep me\n", withProvider.Text);
    }

    [Fact]
    public void Set_On_A_Blank_Document_Starts_The_Sequence()
    {
        var editor = new YamlEditor("# nothing yet\n").SetRaw(Row("a"), "{\"id\":\"a\",\"name\":\"x\"}");
        Assert.Equal("# nothing yet\n- id: \"a\"\n  name: \"x\"\n", editor.Text.Replace("- id: a", "- id: \"a\""));
        Assert.Equal("a", YamlEditor.ParseToJson(editor.Text)![0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Remove_Takes_Out_A_Row_Or_A_Nested_Key()
    {
        var editor = new YamlEditor(Patch);
        var withoutEffort = editor.Remove(Row("agent-default-model") + ".config.reasoningEffort");
        Assert.DoesNotContain("reasoningEffort", withoutEffort.Text);
        Assert.Contains("model: deepseek-flash", withoutEffort.Text.Replace("\"", ""));

        var withoutRow = editor.Remove(Row("agent-default-model"));
        Assert.Equal("# Your patch layer\n# keep me\n- id: ui-chat\n  name: \"@deepseek-ai/dsh-client-ui-chat\"\n  config:\n    transcriptView: detailed\n", withoutRow.Text);
        Assert.Same(editor, editor.Remove(Row("nope")));
        Assert.Same(editor, editor.Remove(Row("ui-chat") + ".config.nope"));

        var last = new YamlEditor("- id: a\n  name: x\n").Remove(Row("a"));
        Assert.Equal("", last.Text);
    }

    [Fact]
    public void Selectors_Need_A_Sequence_Root_And_Block_Rows()
    {
        Assert.Throws<EditorException>(() => new YamlEditor("a: 1\n").Has(Row("x")));
        Assert.Throws<EditorException>(() => new YamlEditor("- {id: a}\n").SetRaw(Row("a") + ".k", "1"));
        Assert.Throws<EditorException>(() => new YamlEditor(Patch).SetRaw(Row("missing") + ".config.x", "1"));
    }
}
