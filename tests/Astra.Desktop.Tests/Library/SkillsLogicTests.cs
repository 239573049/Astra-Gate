using Astra.Api.Contracts;
using Astra.Desktop.Core.Library;

namespace Astra.Desktop.Tests.Library;

/// <summary>The Skills page's logic that needs no window: how a client's chip looks, and how a source reads.</summary>
public sealed class SkillsLogicTests
{
    private static SkillTargetDto Target(string kind = "claude-code", bool detected = true, bool supported = true)
        => new(kind, supported, detected, "/h/skills", false, []);

    [Theory]
    [InlineData("own", "symlink", false, SkillChipLook.On)]
    [InlineData("via-shared", "symlink", false, SkillChipLook.Shared)]
    [InlineData("via-claude-code", "symlink", false, SkillChipLook.Locked)]
    [InlineData("own", "copy", true, SkillChipLook.Copy)]
    [InlineData("off", null, false, SkillChipLook.Off)]
    public void A_chip_looks_the_way_the_server_says_the_client_sees_the_skill(string state, string? how, bool modified, SkillChipLook expected)
        => Assert.Equal(expected, SkillsApi.LookOf(new SkillClientDto("claude-code", state, how, modified), Target()));

    [Fact]
    public void A_client_that_is_missing_or_unsupported_cannot_be_switched_on()
    {
        var off = new SkillClientDto("droid", "off", null, false);
        Assert.Equal(SkillChipLook.Disabled, SkillsApi.LookOf(off, Target("droid", detected: false)));
        Assert.Equal(SkillChipLook.Disabled, SkillsApi.LookOf(off, Target("droid", supported: false)));
        Assert.Equal(SkillChipLook.Disabled, SkillsApi.LookOf(off, null));
    }

    [Fact]
    public void A_skill_already_on_stays_clickable_even_if_its_client_is_no_longer_detected()
        => Assert.Equal(SkillChipLook.On, SkillsApi.LookOf(new SkillClientDto("droid", "own", "symlink", false), Target("droid", detected: false)));

    [Fact]
    public void A_source_reads_as_repository_and_short_commit_or_as_a_folder()
    {
        Assert.Equal("github.com/anthropics/skills · a1b2c3d",
            SkillsApi.SourceText("{\"url\":\"https://github.com/anthropics/skills.git\",\"commit\":\"a1b2c3d4e5f60718\"}"));
        Assert.Equal("github.com/o/r", SkillsApi.SourceText("{\"url\":\"https://github.com/o/r\"}"));
        Assert.Equal("/Users/me/skills/x", SkillsApi.SourceText("{\"path\":\"/Users/me/skills/x\"}"));
        Assert.Equal("/h/.codex/skills/x", SkillsApi.SourceText("{\"from\":\"/h/.codex/skills/x\"}"));
        Assert.Equal("", SkillsApi.SourceText(null));
        Assert.Equal("", SkillsApi.SourceText("not json"));
    }

    [Fact]
    public void Client_names_are_the_brand_names_and_an_unknown_kind_shows_itself()
    {
        Assert.Equal("Claude Code", SkillClientNames.Of("claude-code"));
        Assert.Equal("Copilot CLI", SkillClientNames.Of("copilot-cli"));
        Assert.Equal("some-new-client", SkillClientNames.Of("some-new-client"));
    }
}
