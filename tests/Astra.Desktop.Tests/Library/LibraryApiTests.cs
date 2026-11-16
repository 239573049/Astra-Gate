using Astra.Api.Contracts;
using Astra.Desktop.Core.Library;
using Xunit;

namespace Astra.Desktop.Tests.Library;

/// <summary>The library page's logic: the sheet's draft and what it sends, the state tones, the client names (mirror of the web's <c>ResourceSheet</c>).</summary>
public sealed class LibraryApiTests
{
    [Fact]
    public void A_Hook_Draft_Sends_Event_Matcher_And_Timeout_And_Only_Narrows_Tool_And_Start_Events()
    {
        var draft = new ResourceDraft("hook") { Name = "fmt", Body = "prettier --write", Event = "after-tool", Matcher = "edit", Timeout = 5 };
        draft.Targets.Add(("claude-code", ""));
        var body = draft.ToBody();
        Assert.Equal("after-tool", body.Event);
        Assert.Equal("edit", body.Matcher);
        Assert.Equal(5, body.Timeout);
        Assert.Equal([new ResourceTargetDto("claude-code", "")], body.Targets);

        // a turn end takes no matcher, whatever was typed before the event changed
        draft.Event = "turn-end";
        Assert.Equal("", draft.ToBody().Matcher);
        // a client's own event keeps its matcher text as the client's own
        draft.Event = "claude-code:PermissionRequest";
        Assert.Equal("edit", draft.ToBody().Matcher);
    }

    [Fact]
    public void An_Agent_Needs_A_Description_An_Env_Variable_Needs_No_Value_And_Others_Need_A_Body()
    {
        Assert.False(new ResourceDraft("agent") { Name = "reviewer", Body = "p" }.IsValid);
        Assert.True(new ResourceDraft("agent") { Name = "reviewer", Body = "p", Description = "d" }.IsValid);
        Assert.True(new ResourceDraft("env") { Name = "FLAG" }.IsValid);
        Assert.False(new ResourceDraft("instruction") { Name = "rules" }.IsValid);
        Assert.False(new ResourceDraft("command") { Name = " ", Body = "x" }.IsValid);
    }

    [Fact]
    public void Editing_Starts_From_The_Items_Fields_And_Targets_And_Targets_Are_Sent_In_A_Stable_Order()
    {
        var item = new ResourceItemDto("i1", "agent", "reviewer", "Reviews", "You review.", null, null, 0, "gpt-5", true,
            [new ResourceTargetStateDto("codex", "", "applied", null, null), new ResourceTargetStateDto("claude-code", "/work/app", "drifted", null, null)],
            DateTimeOffset.UtcNow);
        var draft = new ResourceDraft("agent", item);
        Assert.Equal("reviewer", draft.Name);
        Assert.Equal("gpt-5", draft.Model);
        var body = draft.ToBody();
        Assert.Equal("gpt-5", body.Model);
        Assert.Equal([new ResourceTargetDto("claude-code", "/work/app"), new ResourceTargetDto("codex", "")], body.Targets);
        Assert.Null(body.Event);
    }

    [Fact]
    public void States_Map_To_Tones_And_The_Added_Clients_Have_Names()
    {
        Assert.Equal("green", LibraryApi.ToneOf("applied"));
        Assert.Equal("orange", LibraryApi.ToneOf("drifted"));
        Assert.Equal("orange", LibraryApi.ToneOf("project-missing"));
        Assert.Equal("red", LibraryApi.ToneOf("conflict"));
        Assert.Equal("neutral", LibraryApi.ToneOf("pending"));
        Assert.Equal("Qwen Code", SkillClientNames.Of("qwen-code"));
        Assert.Equal("Crush", SkillClientNames.Of("crush"));
        Assert.Equal(["instructions", "agents", "commands", "hooks", "env"], LibraryApi.Segments);
    }
}
