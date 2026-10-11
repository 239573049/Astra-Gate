using Astra.Plugins.Store;

namespace Astra.Plugins.Tests;

public class PluginSpecTests
{
    [Theory]
    [InlineData("./mine", true)]
    [InlineData("/abs/plugin", true)]
    [InlineData("file:///abs/x.js", true)]
    [InlineData("~/dev/plugin", true)]
    [InlineData(@"C:\plugins\x", true)]
    [InlineData("@magpie-community/middleware-word-guard", false)]
    [InlineData("github:someone/word-guard", false)]
    public void Paths_are_told_apart(string spec, bool isPath) => Assert.Equal(isPath, PluginSpec.IsPath(spec));

    [Theory]
    [InlineData("github:someone/word-guard", true)]
    [InlineData("github:someone/word-guard#v1", true)]
    [InlineData("git+https://example.com/x.git", true)]
    [InlineData("https://github.com/someone/repo", true)]
    [InlineData("someone/repo", true)]
    [InlineData("@scope/pkg", false)]
    [InlineData("opencode-gemini-auth@1.2.0", false)]
    [InlineData("./local", false)]
    public void Git_specs_are_recognised(string spec, bool isGit) => Assert.Equal(isGit, PluginSpec.IsGit(spec));

    [Theory]
    [InlineData("@magpie-community/middleware-word-guard@0.1.1", "@magpie-community/middleware-word-guard")]
    [InlineData("@magpie-community/middleware-word-guard@latest", "@magpie-community/middleware-word-guard")]
    [InlineData("@magpie-community/middleware-word-guard", "@magpie-community/middleware-word-guard")]
    [InlineData("opencode-gemini-auth@1.2.0", "opencode-gemini-auth")]
    public void Name_drops_the_version(string spec, string name) => Assert.Equal(name, PluginSpec.Name(spec, "/nowhere"));

    [Fact]
    public void A_git_spec_is_named_by_the_dependency_bun_recorded()
    {
        var dir = Directory.CreateTempSubdirectory("astra-spec-").FullName;
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"dependencies":{"word-guard":"github:someone/word-guard"}}""");

        Assert.Equal("word-guard", PluginSpec.Name("github:someone/word-guard", dir));
        Assert.Equal(Path.Combine(dir, "node_modules", "word-guard"), PluginSpec.Target("github:someone/word-guard", dir));
        Assert.Equal("github:someone/other", PluginSpec.Name("github:someone/other", dir));
    }

    [Theory]
    [InlineData("@magpie-community/middleware-model-map", "model-map")]
    [InlineData("@magpie-community/opencode-zed-auth", "zed")]
    [InlineData("opencode-gemini-auth", "gemini")]
    [InlineData("plain", "plain")]
    public void Short_names_drop_scope_and_kind_words(string package, string shortName) =>
        Assert.Equal(shortName, PluginSpec.ShortName(package));

    [Theory]
    [InlineData("x@1.2.0", true)]
    [InlineData("x@next", true)]
    [InlineData("x@latest", false)]
    [InlineData("@s/x", false)]
    [InlineData("github:o/r", false)]
    public void Pinned_is_a_version_or_tag_other_than_latest(string spec, bool pinned) =>
        Assert.Equal(pinned, PluginSpec.Pinned(spec));

    [Fact]
    public void Npm_targets_are_under_node_modules()
    {
        var target = PluginSpec.Target("@magpie-community/middleware-word-guard@latest", "/p");
        Assert.Equal(Path.Combine("/p", "node_modules", "@magpie-community", "middleware-word-guard"), target);
    }
}
