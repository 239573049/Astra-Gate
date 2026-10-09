using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class ShellBlockEditorTests
{
    [Fact]
    public void Get_AbsentBlock_IsNull()
    {
        var editor = new ShellBlockEditor("export A=1\n");
        Assert.Null(editor.Get("muse-code"));
        Assert.False(editor.Has("muse-code"));
    }

    [Fact]
    public void Set_AppendsBlockAfterUserLines_AddingMissingNewline()
    {
        var editor = new ShellBlockEditor("export A=1").Set("muse-code", "export B=2\nalias muse='x'");
        Assert.Equal("export A=1\n# >>> astra muse-code >>>\nexport B=2\nalias muse='x'\n# <<< astra muse-code <<<\n", editor.Text);
        Assert.Equal("export B=2\nalias muse='x'", editor.Get("muse-code"));
    }

    [Fact]
    public void Set_ReplacesExistingBlock_KeepingSurroundingLines()
    {
        const string text = "before\n# >>> astra muse-code >>>\nold\n# <<< astra muse-code <<<\nafter\n";
        var editor = new ShellBlockEditor(text).Set("muse-code", "new one");
        Assert.Equal("before\n# >>> astra muse-code >>>\nnew one\n# <<< astra muse-code <<<\nafter\n", editor.Text);
    }

    [Fact]
    public void Set_OnEmptyFile_WritesOnlyTheBlock()
    {
        var editor = new ShellBlockEditor(string.Empty).Set("muse-code", "x");
        Assert.Equal("# >>> astra muse-code >>>\nx\n# <<< astra muse-code <<<\n", editor.Text);
    }

    [Fact]
    public void Set_KeepsCrlfLineEndings()
    {
        var editor = new ShellBlockEditor("export A=1\r\n").Set("muse-code", "one\ntwo");
        Assert.Equal("export A=1\r\n# >>> astra muse-code >>>\r\none\r\ntwo\r\n# <<< astra muse-code <<<\r\n", editor.Text);
        Assert.Equal("one\ntwo", editor.Get("muse-code"));
    }

    [Fact]
    public void Remove_OfAppendedBlock_RestoresOriginalText()
    {
        const string original = "export A=1\nalias ll='ls -l'\n";
        var editor = new ShellBlockEditor(original).Set("muse-code", "export B=2").Remove("muse-code");
        Assert.Equal(original, editor.Text);
        Assert.Null(editor.Get("muse-code"));
    }

    [Fact]
    public void Remove_Absent_IsNoOp()
    {
        const string text = "export A=1\n";
        Assert.Equal(text, new ShellBlockEditor(text).Remove("muse-code").Text);
    }

    [Fact]
    public void Blocks_WithDifferentIds_AreIndependent()
    {
        var editor = new ShellBlockEditor(string.Empty).Set("one", "1").Set("two", "2").Remove("one");
        Assert.Null(editor.Get("one"));
        Assert.Equal("2", editor.Get("two"));
    }

    [Fact]
    public void UnterminatedBlock_Throws()
    {
        var editor = new ShellBlockEditor("# >>> astra muse-code >>>\nexport A=1\n");
        Assert.Throws<EditorException>(() => editor.Get("muse-code"));
    }

    [Fact]
    public void InvalidId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ShellBlockEditor(string.Empty).Set("bad id", "x"));
    }
}
