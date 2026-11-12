using System.Text.Json.Nodes;
using Astra.Plugins.Packages;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.Tests.V2;

public class PluginV2ManifestTests
{
    private static PluginV2Manifest Parse(string json, params string[] files)
    {
        var dir = Directory.CreateTempSubdirectory("astra-v2-").FullName;
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "export default {}");
        return PluginV2Manifest.Parse(dir, (JsonObject)JsonNode.Parse(json)!);
    }

    [Fact]
    public void A_full_manifest_reads_into_its_contributions()
    {
        var m = Parse("""
            {
              "manifestVersion": 2, "id": "acme.word-guard", "version": "2.1.0",
              "name": { "zh": "敏感词守卫", "en": "Word Guard" }, "engines": { "astra": ">=0.14.0", "api": "^2.0" },
              "main": "main.js", "gateway": "gateway.js",
              "permissions": ["ui", "storage", "gateway.request:reject", "net:https://api.acme.example", "jobs", "events"],
              "optionalPermissions": ["net:{config.endpoint}"],
              "activation": ["onStartup"],
              "contributes": {
                "configuration": { "properties": { "words": { "type": "array", "items": { "type": "string" }, "default": [] } } },
                "gateway.hooks": [{ "id": "guard", "on": ["request"], "view": "ir", "match": { "protocols": ["anthropic"], "models": ["claude-*"] }, "reads": ["words"] }],
                "ui.pages": [{ "id": "dashboard", "title": "Guard", "icon": "shield", "nav": "tools",
                               "routes": [{ "path": "", "view": "list" }, { "path": ":id", "view": "detail" }] }],
                "ui.slots": [{ "slot": "request.detail.sections", "view": "hits" }],
                "commands": [{ "id": "clear", "title": "Clear" }],
                "jobs": [{ "id": "sync", "every": "6h" }, { "id": "daily", "cron": "0 9 * * *" }],
                "events": [{ "name": "hit" }],
                "future.thing": { "x": 1 }
              },
              "replaces": [{ "v1": "@astragate-community/middleware-word-guard" }]
            }
            """, "main.js", "gateway.js");

        Assert.Equal("acme.word-guard", m.Id);
        Assert.Equal("敏感词守卫", m.DisplayName("zh-CN"));
        Assert.Equal("Word Guard", m.DisplayName("fr"));
        Assert.Single(m.Hooks);
        Assert.True(m.Hooks[0].Match.Matches("anthropic", "claude-sonnet-4", null, true));
        Assert.False(m.Hooks[0].Match.Matches("chat", "claude-sonnet-4", null, true));
        Assert.Equal(2, m.Pages[0].Routes.Count);
        Assert.Equal(TimeSpan.FromHours(6), m.Jobs[0].Every);
        Assert.Equal(["@astragate-community/middleware-word-guard"], m.Replaces);
        Assert.Contains(m.Warnings, w => w.Contains("future.thing"));
        Assert.True(m.RunsOn("0.15.0-preview.2"));
        Assert.False(m.RunsOn("0.13.9"));
    }

    [Theory]
    [InlineData("""{"manifestVersion":2,"id":"Bad","version":"1","name":"x"}""", "id must be")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","permissions":["everything"]}""", "unknown permission")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","permissions":["net:http://x.example"]}""", "https origin")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","contributes":{"ui.pages":[{"id":"p"}]}}""", "needs the permission \"ui\"")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","permissions":["ui"],"contributes":{"ui.pages":[{"id":"p"}]}}""", "main is required")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","engines":{"api":"^3.0"}}""", "needs API")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","main":"../x.js"}""", "outside the plugin")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","permissions":["ui"],"main":"main.js","contributes":{"ui.slots":[{"slot":"nowhere","view":"v"}]}}""", "not a slot")]
    [InlineData("""{"manifestVersion":2,"id":"a.b","version":"1.0.0","name":"x","permissions":["gateway.request:read"],"gateway":"main.js","contributes":{"gateway.hooks":[{"id":"h","on":["text"]}]}}""", "needs view")]
    public void A_bad_manifest_says_what_is_wrong(string json, string expected)
    {
        var e = Assert.Throws<PluginManifestException>(() => Parse(json, "main.js"));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void A_keybinding_the_host_already_uses_is_a_warning()
    {
        var m = Parse("""
            { "manifestVersion": 2, "id": "a.b", "version": "1.0.0", "name": "x", "main": "main.js",
              "contributes": { "commands": [{ "id": "go", "title": "Go", "keybinding": "mod+k" }, { "id": "ok", "title": "Ok", "keybinding": "mod+shift+g" }] } }
            """, "main.js");
        var warning = Assert.Single(m.Warnings);
        Assert.Contains("commands.go", warning);
        Assert.Contains("command palette", warning);
    }

    [Fact]
    public void A_v2_package_is_nothing_to_the_v1_runtimes()
    {
        var dir = Directory.CreateTempSubdirectory("astra-v2-").FullName;
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"x","main":"index.js","magpie":{"middleware":"./m.middleware.js"}}""");
        File.WriteAllText(Path.Combine(dir, "index.js"), "");
        File.WriteAllText(Path.Combine(dir, "main.js"), "");
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """{"manifestVersion":2,"id":"acme.x","version":"2.0.0","name":"X","main":"main.js"}""");

        var m = PackageManifest.Read(dir);
        Assert.True(m.IsV2);
        Assert.Equal("acme.x", m.Name);
        Assert.Null(m.MiddlewareFile);
        Assert.False(m.HasEntry);
        Assert.NotNull(m.V2);
    }

    [Theory]
    [InlineData("1.2.3", ">=1.2.0", true)]
    [InlineData("1.2.3", "^1.0", true)]
    [InlineData("2.0.0", "^1.0", false)]
    [InlineData("0.15.0", "^0.14.0", false)]
    [InlineData("0.14.9", "~0.14.1", true)]
    [InlineData("1.0.0-beta", ">=1.0.0", false)]
    [InlineData("1.5.0", ">=1.2 <2", true)]
    [InlineData("3.0.0", "^1.0 || ^3.0", true)]
    [InlineData("1.2.3", "1.2.x", true)]
    [InlineData("1.3.0", "1.2.x", false)]
    public void Semver_ranges(string version, string range, bool expected)
    {
        Assert.True(SemVer.TryParse(version, out var v));
        Assert.Equal(expected, SemVer.Satisfies(v, range));
    }

    [Fact]
    public void Cron_finds_the_next_minute()
    {
        Assert.True(Cron.TryParse("*/15 9-17 * * 1-5", out var cron));
        var next = cron.Next(new DateTime(2026, 11, 13, 17, 50, 0)); // a Friday
        Assert.Equal(new DateTime(2026, 11, 16, 9, 0, 0), next); // the next Monday
        Assert.False(Cron.TryParse("61 * * * *", out _));
    }

    [Fact]
    public void Diff_then_apply_gives_the_new_tree()
    {
        static JsonObject N(string type, string? key, string text, params JsonObject[] kids)
        {
            var n = new JsonObject { ["type"] = type, ["props"] = new JsonObject { ["value"] = text } };
            if (key is not null) n["key"] = key;
            if (kids.Length > 0) n["children"] = new JsonArray([.. kids]);
            return n;
        }
        var a = N("Stack", null, "", N("Text", "a", "1"), N("Text", "b", "2"), N("Text", "c", "3"), N("Button", null, "x"));
        var b = N("Stack", null, "", N("Text", "c", "3!"), N("Text", "a", "1"), N("Text", "d", "4"), N("Button", null, "y"));
        var ops = UiDiff.Diff(a, b);
        Assert.True(JsonNode.DeepEquals(b, UiDiff.Apply(a, ops)));
        Assert.Contains(ops, o => o!["op"]!.GetValue<string>() == "move");
        Assert.Contains(ops, o => o!["op"]!.GetValue<string>() == "remove");
    }

    [Fact]
    public void Markdown_keeps_only_safe_links()
    {
        Assert.Equal("https://a.example", MarkdownUi.SafeLink("https://a.example"));
        Assert.Equal("/plugins/x", MarkdownUi.SafeLink("/plugins/x"));
        Assert.Null(MarkdownUi.SafeLink("javascript:alert(1)"));
        Assert.Null(MarkdownUi.SafeLink("http://plain.example"));
        Assert.Null(MarkdownUi.SafeLink("//evil.example"));
        var spans = MarkdownUi.Spans("a *b* `c` ~~d~~ [e](data:x)");
        Assert.Contains(spans, s => s!["italic"] is not null && s["text"]!.GetValue<string>() == "b");
        Assert.Contains(spans, s => s!["code"] is not null);
        Assert.Contains(spans, s => s!["strike"] is not null);
        Assert.DoesNotContain(spans, s => s!["link"] is not null);
    }
}

public class PluginMarketV2Tests
{
    [Fact]
    public void V2_entries_come_from_their_own_array_with_what_they_replace()
    {
        var list = Astra.Plugins.Market.PluginMarket.Parse("""
            {
              "plugins": [{ "package": "@x/middleware-guard", "kind": "middleware" }],
              "pluginsV2": [{
                "id": "acme.guard", "name": "Guard", "permissions": ["storage", "gateway.request:reject"],
                "replaces": [{ "v1": "@x/middleware-guard" }], "notes": { "en": "Now with a page." },
                "tarballs": [{ "version": "2.0.0", "url": "https://x.example/a.tgz", "sha256": "0000000000000000000000000000000000000000000000000000000000000000" }]
              }]
            }
            """, null)!;
        Assert.Equal(2, list.Count);
        var v2 = list.Single(l => l.ApiVersion == 2);
        Assert.Equal("acme.guard", v2.Package);
        Assert.Equal("v2", v2.Kind);
        Assert.Equal(["@x/middleware-guard"], v2.ReplacesV1);
        Assert.Equal(["storage", "gateway.request:reject"], v2.Permissions);
        Assert.Equal("Now with a page.", v2.Notes!["en"]);
        Assert.Equal(1, list.Single(l => l.ApiVersion == 1).ApiVersion);
    }
}
