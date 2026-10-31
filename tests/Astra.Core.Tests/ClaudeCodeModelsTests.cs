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

    [Fact]
    public void Parse_Puts_The_Fable_Tier_Between_The_Default_Model_And_Opus()
    {
        var slots = ClaudeCodeModels.Parse("""
            {"models":{"ANTHROPIC_DEFAULT_OPUS_MODEL":"gpt-6.1-sol","ANTHROPIC_DEFAULT_FABLE_MODEL":" gpt-6-astra ","ANTHROPIC_MODEL":"gpt-6.1-terra"}}
            """);
        Assert.Equal(["ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL"], slots.Select(s => s.Key));
        Assert.Equal("gpt-6-astra", slots[1].Value);
    }

    [Theory]
    [InlineData("ANTHROPIC_MODEL", "gpt-6.1-terra", "gpt-6.1-terra[1m]")]
    [InlineData("ANTHROPIC_DEFAULT_FABLE_MODEL", "gpt-6-astra", "gpt-6-astra[1m]")]
    [InlineData("ANTHROPIC_DEFAULT_FABLE_MODEL", "gpt-6-astra[1M]", "gpt-6-astra[1M]")]
    [InlineData("ANTHROPIC_DEFAULT_OPUS_MODEL", "gpt-6.1-sol", "gpt-6.1-sol[1m]")]
    [InlineData("ANTHROPIC_DEFAULT_SONNET_MODEL", "gpt-6.1-terra", "gpt-6.1-terra[1m]")]
    [InlineData("ANTHROPIC_DEFAULT_HAIKU_MODEL", "gpt-6-luna", "gpt-6-luna")]
    public void With1M_Suffixes_Every_Tier_But_Haiku(string slot, string model, string expected) =>
        Assert.Equal(expected, ClaudeCodeModels.With1M(slot, model));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"models":"ANTHROPIC_MODEL"}""")]
    [InlineData("""{"roleMap":{"sonnet":"glm-4.6"}}""")]
    public void Parse_Tolerates_Missing_Or_Malformed_Extras(string? json) => Assert.Empty(ClaudeCodeModels.Parse(json));
}
