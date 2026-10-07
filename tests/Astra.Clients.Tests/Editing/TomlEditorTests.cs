using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class TomlEditorTests
{
    [Fact]
    public void Set_ExistingTopLevelKey_ReplacesValueOnly()
    {
        var editor = new TomlEditor("a = 1 # keep\nb = \"x\"\n");
        var result = editor.Set("a", "42");
        Assert.Equal("a = 42 # keep\nb = \"x\"\n", result.Text);
    }

    [Fact]
    public void Set_NewTopLevelKey_InsertsAfterLastTopLevelKey()
    {
        var editor = new TomlEditor("# header comment\n\nalpha = 1\n\n[t]\nx = 1\n");
        var result = editor.SetString("beta", "b");
        Assert.Equal("# header comment\n\nalpha = 1\nbeta = \"b\"\n\n[t]\nx = 1\n", result.Text);
    }

    [Fact]
    public void Set_NewTopLevelKey_NoTables_AppendsAtEnd()
    {
        var editor = new TomlEditor("a = 1");
        var result = editor.SetString("b", "x");
        Assert.Equal("a = 1\nb = \"x\"\n", result.Text);
    }

    [Fact]
    public void Set_IntoExistingTable_AppendsInsideTable()
    {
        var editor = new TomlEditor("[t]\nx = 1 # keep\n");
        var result = editor.SetString("t.y", "2");
        Assert.Equal("[t]\nx = 1 # keep\ny = \"2\"\n", result.Text);
    }

    [Fact]
    public void Set_MissingTable_CreatesTableAtEndOfFile()
    {
        var editor = new TomlEditor("[a]\nx = 1\n");
        var result = editor.Set("b.c.key", "\"v\"");
        Assert.Equal("[a]\nx = 1\n\n[b.c]\nkey = \"v\"\n", result.Text);
    }

    [Fact]
    public void Set_NoTrailingNewline_NewKeyRestoresExactly()
    {
        var editor = new TomlEditor("a = 1");
        var withKey = editor.Set("b", "2").Text;
        Assert.Equal("a = 1\nb = 2\n", withKey);
        // Remove takes the whole line; the final-newline byte is reconciled by the applier.
        var restored = new TomlEditor(withKey).Remove("b");
        Assert.Equal("a = 1\n", restored.Text);
    }

    [Fact]
    public void CrlfFiles_StayCrlf()
    {
        var editor = new TomlEditor("a = 1\r\n\r\n[t]\r\nx = 1\r\n");
        var result = editor.Set("b", "2");
        Assert.Equal("a = 1\r\nb = 2\r\n\r\n[t]\r\nx = 1\r\n", result.Text);
        Assert.DoesNotContain(result.Text, "\n[b"); // sanity: no inserted LF line without CR
        var table = result.InsertTable("model.astra", [new KeyValuePair<string, string>("k", "\"v\"")]);
        Assert.Equal("a = 1\r\nb = 2\r\n\r\n[t]\r\nx = 1\r\n\r\n[model.astra]\r\nk = \"v\"\r\n", table.Text);
    }

    [Fact]
    public void RemoveTable_RemovesHeaderAndItems_KeepsNeighbors()
    {
        const string text = "top = 1\n\n[mine]\nk = 1\n\n[other]\nk = 2\n";
        var result = new TomlEditor(text).RemoveTable("mine");
        Assert.Equal("top = 1\n\n[other]\nk = 2\n", result.Text);
    }

    [Fact]
    public void RemoveTable_LastTable_Works()
    {
        const string text = "[a]\nx = 1\n\n[mine]\nk = 1\n";
        var result = new TomlEditor(text).RemoveTable("mine");
        Assert.Equal("[a]\nx = 1\n", result.Text);
    }

    [Fact]
    public void Remove_KeepsPrecedingCommentLines()
    {
        const string text = "# my choice\nmodel = \"x\"\nother = 1\n";
        var result = new TomlEditor(text).Remove("model");
        Assert.Equal("# my choice\nother = 1\n", result.Text);
    }

    [Fact]
    public void GetValue_StringBoolAndMissing()
    {
        var editor = new TomlEditor("s = \"a\\tb\"\nb = true\nn = 4141\n");
        Assert.Equal("a\tb", editor.GetStringValue("s"));
        Assert.Equal(true, editor.GetBoolValue("b"));
        Assert.Equal("4141", editor.GetValue("n"));
        Assert.Null(editor.GetValue("missing"));
        Assert.False(editor.KeyExists("missing"));
    }

    [Fact]
    public void TableExists_FindsDottedHeaders()
    {
        var editor = new TomlEditor("[model_providers.astra]\nname = \"x\"\n");
        Assert.True(editor.TableExists("model_providers.astra"));
        Assert.False(editor.TableExists("model_providers.other"));
        Assert.True(editor.KeyExists("model_providers.astra.name"));
    }

    [Fact]
    public void Set_UnicodeString_EscapesAndRoundTrips()
    {
        var editor = new TomlEditor("");
        var result = editor.SetString("k", "héllo \"wörld\"\n");
        Assert.Equal("héllo \"wörld\"\n", result.GetStringValue("k"));
    }

    [Fact]
    public void InsertTable_OnFileWithoutTrailingNewline()
    {
        var editor = new TomlEditor("a = 1");
        var result = editor.InsertTable("model_providers.astra",
        [
            new("name", "\"Astra\""),
            new("requires_openai_auth", "false"),
        ]);
        Assert.Equal("a = 1\n\n[model_providers.astra]\nname = \"Astra\"\nrequires_openai_auth = false\n", result.Text);
    }

    [Fact]
    public void InsertTable_ExistingTable_Throws()
    {
        var editor = new TomlEditor("[t]\nx = 1\n");
        Assert.Throws<EditorException>(() => editor.InsertTable("t", [new("y", "1")]));
    }

    [Fact]
    public void Set_SelfCheckDetectsBrokenDocument_AndThrows()
    {
        // A value that leaves the document invalid must abort (editors never return corrupt text).
        Assert.ThrowsAny<Exception>(() => new TomlEditor("a = 1\n").Set("b", "[not a value]\nother = "));
    }

    [Fact]
    public void InvalidDocument_ThrowsOnAnyOperation()
    {
        Assert.Throws<EditorException>(() => new TomlEditor("not = [valid").GetValue("x"));
    }

    [Fact]
    public void CommentBetweenItems_StaysAttachedAfterRemoval()
    {
        const string text = "a = 1\n# documents table\ntarget = 2\n\n[t]\nx = 1\n";
        var result = new TomlEditor(text).Remove("target");
        Assert.Equal("a = 1\n# documents table\n\n[t]\nx = 1\n", result.Text);
    }
}
