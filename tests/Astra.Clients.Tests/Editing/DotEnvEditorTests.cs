using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class DotEnvEditorTests
{
    [Fact]
    public void Get_UnquotesDoubleAndSingleQuotes()
    {
        var editor = new DotEnvEditor("A=plain\nB=\"hello world\"\nC='lit\\eral'\n# D=commented\n");
        Assert.Equal("plain", editor.Get("A"));
        Assert.Equal("hello world", editor.Get("B"));
        Assert.Equal("lit\\eral", editor.Get("C"));
        Assert.Null(editor.Get("D"));
        Assert.False(editor.Has("D"));
    }

    [Fact]
    public void Get_SupportsExportPrefixAndSpaces()
    {
        var editor = new DotEnvEditor("export  KEY = value with spaces\n");
        Assert.Equal("value with spaces", editor.Get("KEY"));
    }

    [Fact]
    public void Set_ExistingKey_PreservesOtherLinesAndComments()
    {
        const string text = "# config\nA=1\n\nB=\"two\"\n";
        var result = new DotEnvEditor(text).Set("A", "renamed");
        Assert.Equal("# config\nA=renamed\n\nB=\"two\"\n", result.Text);
    }

    [Fact]
    public void Set_QuotesValuesWithSpaces()
    {
        var result = new DotEnvEditor("A=1\n").Set("B", "two words");
        Assert.Equal("A=1\nB=\"two words\"\n", result.Text);
        Assert.Equal("two words", result.Get("B"));
    }

    [Fact]
    public void Set_SafeValuesStayUnquoted()
    {
        var result = new DotEnvEditor("A=1\n").Set("B", "astra-gemini-abc-123");
        Assert.Equal("A=1\nB=astra-gemini-abc-123\n", result.Text);
    }

    [Fact]
    public void Set_NewKeyWithoutTrailingNewline_RemoveLeavesSingleTrailingNewline()
    {
        const string original = "A=1";
        var edited = new DotEnvEditor(original).Set("B", "2").Text;
        Assert.Equal("A=1\nB=2\n", edited);
        // The editor removes the whole line including its newline; restoring a missing final
        // newline byte-exactly is the applier's job (first-write fallback).
        var restored = new DotEnvEditor(edited).Remove("B").Text;
        Assert.Equal("A=1\n", restored);
    }

    [Fact]
    public void Remove_MiddleLine_KeepsOthers()
    {
        const string text = "A=1\nB=2\nC=3\n";
        var result = new DotEnvEditor(text).Remove("B");
        Assert.Equal("A=1\nC=3\n", result.Text);
    }

    [Fact]
    public void Remove_LastLine_Works()
    {
        const string text = "A=1\nB=2\n";
        var result = new DotEnvEditor(text).Remove("B");
        Assert.Equal("A=1\n", result.Text);
    }

    [Fact]
    public void CrlfFiles_KeepCrlf()
    {
        var result = new DotEnvEditor("A=1\r\nB=2\r\n").Set("C", "3");
        Assert.Equal("A=1\r\nB=2\r\nC=3\r\n", result.Text);
        var replaced = new DotEnvEditor("A=1\r\nB=2\r\n").Set("B", "renamed");
        Assert.Equal("A=1\r\nB=renamed\r\n", replaced.Text);
    }

    [Fact]
    public void UnicodeValue_IsQuotedAndPreserved()
    {
        var result = new DotEnvEditor("").Set("NAME", "沪江");
        Assert.Equal("NAME=\"沪江\"\n", result.Text);
        Assert.Equal("沪江", result.Get("NAME"));
    }

    [Fact]
    public void SetRaw_RestoresOriginalQuoting()
    {
        const string original = "NAME=\"Doe, Jane\"\nOTHER=1\n";
        var edited = new DotEnvEditor(original).Set("NAME", "changed").Text;
        Assert.NotEqual(original, edited);
        var restored = new DotEnvEditor(edited).SetRaw("NAME", "\"Doe, Jane\"").Text;
        Assert.Equal(original, restored);
    }

    [Fact]
    public void MultiLineValue_Throws()
    {
        Assert.Throws<EditorException>(() => new DotEnvEditor("A=1\n").Set("B", "two\nlines"));
    }
}
