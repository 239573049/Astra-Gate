using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

public class DiffToolTests
{
    [Fact]
    public void Unified_AddsHeaderAndHunk()
    {
        const string before = "a\nb\nc\n";
        const string after = "a\nB\nc\n";
        var diff = DiffTool.Unified("settings.json", before, after);
        Assert.StartsWith("--- a/settings.json\n+++ b/settings.json\n", diff);
        Assert.Contains("@@ -1,3 +1,3 @@", diff);
        Assert.Contains("-b\n+B\n", diff);
        Assert.Contains(" a\n", diff);
    }

    [Fact]
    public void Unified_TwoDistantChanges_ProduceTwoHunks()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var before = string.Join("\n", lines) + "\n";
        lines[2] = "CHANGED-3";
        lines[17] = "CHANGED-18";
        var after = string.Join("\n", lines) + "\n";
        var diff = DiffTool.Unified("f", before, after);
        Assert.Equal(2, diff.Split("@@ -").Length - 1);
        Assert.Contains("-line3\n+CHANGED-3\n", diff);
        Assert.Contains("-line18\n+CHANGED-18\n", diff);
    }

    [Fact]
    public void Unified_NoChange_EmptyDiffBody()
    {
        var diff = DiffTool.Unified("f", "a\n", "a\n");
        Assert.Equal("--- a/f\n+++ b/f\n", diff);
    }

    [Fact]
    public void Unified_AdditionAtEnd()
    {
        var diff = DiffTool.Unified("f", "a\n", "a\nb\n");
        Assert.Contains("+b\n", diff);
    }
}
