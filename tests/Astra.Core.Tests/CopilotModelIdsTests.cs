using Astra.Core.Models;

namespace Astra.Core.Tests;

public class CopilotModelIdsTests
{
    [Theory]
    [InlineData("claude-haiku-5-5", "claude-haiku-5.5")]
    [InlineData("claude-haiku-4-5", "claude-haiku-4.5")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4.5")]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-4.5")]
    [InlineData("claude-opus-4-1-20250805", "claude-opus-4.1")]
    [InlineData("claude-sonnet-4-20250514", "claude-sonnet-4")]
    [InlineData("claude-3-5-sonnet", "claude-3.5-sonnet")]
    [InlineData("claude-3-7-sonnet-20250219", "claude-3.7-sonnet")]
    // unchanged: already dotted, no minor, other vendors, unrelated names
    [InlineData("claude-haiku-4.5", "claude-haiku-4.5")]
    [InlineData("claude-sonnet-4", "claude-sonnet-4")]
    [InlineData("gpt-4o", "gpt-4o")]
    [InlineData("gemini-2-5-pro", "gemini-2-5-pro")]
    [InlineData("anthropic/claude-haiku-4-5", "anthropic/claude-haiku-4-5")]
    public void ToUpstream_Dots_The_Claude_Version(string input, string expected) =>
        Assert.Equal(expected, CopilotModelIds.ToUpstream(input));

    [Fact]
    public void MapModel_Only_Rewrites_For_Copilot_And_Explicit_Map_Wins()
    {
        var copilot = new Provider { Id = "p1", TemplateId = "github-copilot-subscription" };
        var other = new Provider { Id = "p2", TemplateId = "claude-subscription" };
        Assert.Equal("claude-haiku-5.5", copilot.MapModel("claude-haiku-5-5"));
        Assert.Equal("claude-haiku-5-5", other.MapModel("claude-haiku-5-5"));

        copilot.Settings[Provider.ModelMapKey] = new System.Text.Json.Nodes.JsonObject { ["claude-haiku-5-5"] = "my-model" };
        Assert.Equal("my-model", copilot.MapModel("claude-haiku-5-5"));
        Assert.Equal("claude-sonnet-4.5", copilot.MapModel("claude-sonnet-4-5"));
    }
}
