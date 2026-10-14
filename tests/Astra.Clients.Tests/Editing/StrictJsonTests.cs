using Astra.Clients.Editing;

namespace Astra.Clients.Tests.Editing;

/// <summary>Strict-JSON repair for clients that read their config with JSON.parse (Claude Desktop).</summary>
public class StrictJsonTests
{
    [Fact]
    public void Trailing_Comma_Is_Found_And_Only_The_Comma_Is_Removed()
    {
        const string text = "{\n  \"preferences\": {\n    \"sidebarMode\": \"chat\"\n  },\n}\n";
        Assert.Equal((StrictJson.TrailingComma, 4), StrictJson.FindProblem(text));
        Assert.Equal("{\n  \"preferences\": {\n    \"sidebarMode\": \"chat\"\n  }\n}\n", StrictJson.Repair(text));
    }

    [Fact]
    public void Trailing_Commas_In_Arrays_And_Behind_Comments_Are_Removed()
    {
        const string text = "{\"a\": [1, 2,], \"b\": {\"c\": true, /* gone */ }, \"d\":/*x*/ 1,}";
        var repaired = StrictJson.Repair(text);
        Assert.Equal("{\"a\": [1, 2], \"b\": {\"c\": true }, \"d\": 1}", repaired);
        Assert.True(StrictJson.IsStrict(repaired!));
    }

    [Fact]
    public void Comments_Go_With_Their_Line_When_Alone_And_With_Their_Blanks_At_A_Line_End()
    {
        const string text = "{\r\n  // the key\r\n  \"a\": 1, // first\r\n  /* block\r\n     comment */\r\n  \"b\": 2\r\n}";
        Assert.Equal((StrictJson.Comment, 2), StrictJson.FindProblem(text));
        Assert.Equal("{\r\n  \"a\": 1,\r\n  \"b\": 2\r\n}", StrictJson.Repair(text));
    }

    [Fact]
    public void Comment_And_Comma_Lookalikes_Inside_Strings_Are_Left_Alone()
    {
        const string text = "{\"url\": \"http://x/*y*/\", \"s\": \",}\\\"//\", \"n\": 1,}";
        Assert.Equal("{\"url\": \"http://x/*y*/\", \"s\": \",}\\\"//\", \"n\": 1}", StrictJson.Repair(text));
    }

    [Theory]
    [InlineData("{\"a\": 1}")]          // strict already
    [InlineData("{\"a\": 1,,}")]        // not JSONC either: nothing safe to do
    [InlineData("{\"a\": /* open")]     // unterminated comment
    [InlineData("{\"a\": 1, \"a\": 2,}")] // duplicate members
    [InlineData("")]
    public void Nothing_To_Repair(string text)
    {
        Assert.Null(StrictJson.FindProblem(text));
        Assert.Null(StrictJson.Repair(text));
    }
}
