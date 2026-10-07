using Astra.Core.Clients;

namespace Astra.Core.Tests;

public class ClaudeDesktopRolesTests
{
    [Theory]
    [InlineData("claude-sonnet-4-5", "sonnet")]
    [InlineData("claude-sonnet-4-5-20250929", "sonnet")]
    [InlineData("claude-3-5-sonnet-20241022", "sonnet")]
    [InlineData("claude-opus-5-5[1m]", "opus")]
    [InlineData("CLAUDE-HAIKU-4-5", "haiku")]
    [InlineData("claude-fable-5", null)]
    [InlineData("gpt-5", null)]
    [InlineData("sonnet", null)]
    public void Role_Of_A_Model_Id(string model, string? role) => Assert.Equal(role, ClaudeDesktopRoles.RoleOf(model));

    [Fact]
    public void Map_Uses_The_Role_Then_The_First_Mapped_Role_And_Passes_Other_Ids_Through()
    {
        var map = ClaudeDesktopRoles.Parse("""{"roleMap":{"haiku":"glm-4.5-air","opus":" ","sonnet":"glm-4.6","extra":"x"}}""");
        Assert.Equal(["sonnet", "haiku"], map.Select(m => m.Key)); // Roles order, blanks and unknown keys dropped
        Assert.Equal("glm-4.5-air", ClaudeDesktopRoles.Map(map, "claude-haiku-4-5"));
        Assert.Equal("glm-4.6", ClaudeDesktopRoles.Map(map, "claude-opus-5-5"));
        Assert.Equal("my-model", ClaudeDesktopRoles.Map(map, "my-model"));
        Assert.Equal("claude-sonnet-4-5", ClaudeDesktopRoles.Map([], "claude-sonnet-4-5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"roleMap":"sonnet"}""")]
    [InlineData("""{"models":{}}""")]
    public void Parse_Tolerates_Missing_Or_Malformed_Extras(string? json) => Assert.Empty(ClaudeDesktopRoles.Parse(json));
}
