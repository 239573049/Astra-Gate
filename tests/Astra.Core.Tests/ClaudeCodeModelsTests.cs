using Astra.Core.Clients;

namespace Astra.Core.Tests;

public class ClaudeCodeModelsTests
{
    [Fact]
    public void Parse_Keeps_Known_NonEmpty_Slots_In_Slot_Order()
    {
        var slots = ClaudeCodeModels.Parse("""
            {"models":{"ANTHROPIC_DEFAULT_HAIKU_MODEL":" glm-4.5-air ","ANTHROPIC_MODEL":"glm-4.6","ANTHROPIC_DEFAULT_OPUS_MODEL":" ","extra":"x"}}
            """);
        Assert.Equal(["ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL"], slots.Select(s => s.Key));
        Assert.Equal("glm-4.6", slots[0].Value);
        Assert.Equal("glm-4.5-air", slots[1].Value); // trimmed
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"models":"ANTHROPIC_MODEL"}""")]
    [InlineData("""{"roleMap":{"sonnet":"glm-4.6"}}""")]
    public void Parse_Tolerates_Missing_Or_Malformed_Extras(string? json) => Assert.Empty(ClaudeCodeModels.Parse(json));
}
