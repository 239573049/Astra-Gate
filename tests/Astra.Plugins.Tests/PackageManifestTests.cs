using Astra.Plugins.Packages;

namespace Astra.Plugins.Tests;

public class PackageManifestTests
{
    private static string Folder(string packageJson, params string[] files)
    {
        var dir = Directory.CreateTempSubdirectory("astra-manifest-").FullName;
        File.WriteAllText(Path.Combine(dir, "package.json"), packageJson);
        foreach (var f in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
            File.WriteAllText(Path.Combine(dir, f), "export default {}");
        }
        return dir;
    }

    [Fact]
    public void Middleware_only_package_runs_in_process()
    {
        var dir = Folder("""
            {"name":"@magpie-community/middleware-word-guard","version":"0.1.1",
             "magpie":{"middleware":"./word-guard.middleware.js","options":{"words":["x"],"action":"reject"}}}
            """, "word-guard.middleware.js");

        var m = PackageManifest.Read(dir);

        Assert.Equal("@magpie-community/middleware-word-guard", m.Name);
        Assert.Equal("0.1.1", m.Version);
        Assert.Equal(Path.Combine(dir, "word-guard.middleware.js"), m.MiddlewareFile);
        Assert.True(m.InProcessOnly);
        Assert.Null(m.NotPluginReason);
        Assert.Equal("reject", m.OptionsExample?["action"]?.GetValue<string>());
    }

    [Fact]
    public void An_index_file_beside_middleware_does_not_make_it_a_host_plugin()
    {
        var m = PackageManifest.Read(Folder("""{"name":"x","magpie":{"middleware":"./x.middleware.js"}}""", "x.middleware.js", "index.js"));
        Assert.True(m.HasEntry);
        Assert.True(m.InProcessOnly);
    }

    [Fact]
    public void Agent_with_main_is_both()
    {
        var m = PackageManifest.Read(Folder("""{"name":"a","main":"./index.js","magpie":{"agent":"./a.agent.js"}}""", "a.agent.js", "index.js"));
        Assert.NotNull(m.AgentFile);
        Assert.True(m.HasEntry);
        Assert.False(m.InProcessOnly);
    }

    [Fact]
    public void OpenCode_plugin_is_a_host_plugin()
    {
        var m = PackageManifest.Read(Folder("""{"name":"opencode-gemini-auth","exports":{".":"./dist/index.js"}}"""));
        Assert.True(m.HasEntry);
        Assert.False(m.InProcessOnly);
        Assert.Null(m.MiddlewareFile);
    }

    [Theory]
    [InlineData("""{"name":"p","pi":{"extensions":["./e.ts"]}}""")]
    [InlineData("""{"name":"p","keywords":["pi-package"]}""")]
    [InlineData("""{"name":"p","peerDependencies":{"@earendil-works/pi-coding-agent":"*"}}""")]
    [InlineData("""{"name":"p","dependencies":{"@mariozechner/pi-coding-agent":"*"}}""")]
    public void Pi_packages_are_recognised(string packageJson)
    {
        var m = PackageManifest.Read(Folder(packageJson));
        Assert.True(m.IsPi);
        Assert.Null(m.NotPluginReason);
    }

    [Fact]
    public void A_command_only_package_is_not_a_plugin()
    {
        var m = PackageManifest.Read(Folder("""{"name":"magpie-x-search","bin":{"x":"./cli.js"}}"""));
        Assert.Contains("isn't an OpenCode or magpie plugin", m.NotPluginReason);
        Assert.Contains("only a command (bin)", m.NotPluginReason);
    }

    [Theory]
    [InlineData("guard.middleware.js", true, false)]
    [InlineData("Aider.Agent.MJS", false, true)]
    public void Single_files_by_suffix(string file, bool middleware, bool agent)
    {
        var dir = Directory.CreateTempSubdirectory("astra-manifest-").FullName;
        var path = Path.Combine(dir, file);
        File.WriteAllText(path, "export function onRequest(b) { return b }");

        var m = PackageManifest.Read(path);

        Assert.Equal(middleware, m.MiddlewareFile is not null);
        Assert.Equal(agent, m.AgentFile is not null);
        Assert.True(m.InProcessOnly);
        Assert.Equal(dir, m.ModuleRoot);
    }
}
