using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class ImportApiTests
{
    private const string Secret = "sk-import-secret-0123456789";

    private static void Write(TestHost host, string relative, string content)
    {
        var path = Path.Combine(host.ClientHome, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void SeedMagpie(TestHost host) => Write(host, ".config/magpie/providers.json", $$"""
        {"providers":[
          {"id":"ds","name":"My DeepSeek","key":"{{Secret}}","chat":"https://api.deepseek.com/v1","anthropic":"https://api.deepseek.com/anthropic","models":["deepseek-chat"],
           "headers":{"X-Team":"blue","Host":"evil.example"},"proxy":"not a proxy"},
          {"id":"anth","name":"Direct Anthropic","key":"sk-ant-direct-key-1234","anthropic":"https://api.anthropic.com"},
          {"id":"self","name":"Astra itself","key":"k","chat":"http://127.0.0.1:17321/v1"}
        ]}
        """);

    private static async Task<JsonNode> SourceAsync(TestHost host, string id) =>
        (await host.GetJsonAsync("/api/providers/import/sources")).AsArray().Single(s => s!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task Preview_Lists_Candidates_Without_Ever_Returning_The_Key()
    {
        await using var host = await TestHost.StartAsync();
        SeedMagpie(host);
        var res = await host.Client.GetAsync("/api/providers/import/sources");
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain("sk-ant-direct-key-1234", text);

        var magpie = JsonNode.Parse(text)!.AsArray().Single(s => s!["id"]!.GetValue<string>() == "magpie")!;
        Assert.True(magpie["found"]!.GetValue<bool>());
        Assert.EndsWith("magpie/providers.json", magpie["path"]!.GetValue<string>());
        var ds = magpie["items"]!.AsArray().Single(i => i!["ref"]!.GetValue<string>() == "magpie:ds")!;
        Assert.Equal("new", ds["status"]!.GetValue<string>());
        Assert.Equal("sk-…6789", ds["keyMasked"]!.GetValue<string>());
        Assert.Equal(8, ds["keyFingerprint"]!.GetValue<string>().Length);
        Assert.Equal("deepseek", ds["templateId"]!.GetValue<string>());
        Assert.Equal(["openai-chat", "anthropic"], ds["endpoints"]!.AsArray().Select(e => e!["protocol"]!.GetValue<string>()));
        Assert.Contains("header:Host", ds["ignoredFields"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Contains("proxy", ds["ignoredFields"]!.AsArray().Select(f => f!.GetValue<string>()));

        var self = magpie["items"]!.AsArray().Single(i => i!["ref"]!.GetValue<string>() == "magpie:self")!;
        Assert.Equal("skip", self["status"]!.GetValue<string>());
        Assert.Equal("points-to-astra", self["skipReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Import_Creates_Providers_With_Encrypted_Keys_And_Matched_Templates()
    {
        await using var host = await TestHost.StartAsync();
        SeedMagpie(host);
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[]
        {
            new { source = "magpie", @ref = "magpie:ds" }, new { source = "magpie", @ref = "magpie:anth" }, new { source = "magpie", @ref = "magpie:self" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain(Secret, body!.ToJsonString());
        Assert.Equal(2, body["added"]!.AsArray().Count);
        Assert.Equal("points-to-astra", body["skipped"]!.AsArray().Single()!["reason"]!.GetValue<string>());

        var providers = await host.Db.Providers.ListAsync();
        var ds = providers.Single(p => p.Name == "My DeepSeek");
        Assert.Equal("deepseek", ds.TemplateId);
        Assert.Equal("deepseek", ds.PriceKey);
        Assert.NotEqual(Secret, ds.ApiKeyEnc);
        Assert.Equal(Secret, host.App.Services.GetRequiredService<Astra.Core.Clients.ISecretProtector>().Unprotect(ds.ApiKeyEnc!));
        Assert.Equal([ApiProtocol.OpenAIChat, ApiProtocol.Anthropic], ds.Endpoints.Select(e => e.Protocol));
        Assert.Equal("blue", ds.ExtraHeaders["X-Team"]);
        Assert.False(ds.ExtraHeaders.ContainsKey("Host"));
        Assert.Null(ds.HttpProxy);
        Assert.StartsWith("导入自 Magpie", ds.Notes);
        Assert.Contains("deepseek-chat", (await host.Db.Providers.ListModelsAsync(ds.Id)).Select(m => m.ModelId));

        // The Anthropic API host matches the API template, never the subscription one.
        var anth = providers.Single(p => p.Name == "Direct Anthropic");
        Assert.Equal("anthropic", anth.TemplateId);
        Assert.NotEqual("oauth-subscription", anth.AuthScheme);
        Assert.Equal(AuthSchemes.XApiKey, anth.AuthScheme);
    }

    [Fact]
    public async Task Second_Import_Is_Detected_As_Same_And_Skipped()
    {
        await using var host = await TestHost.StartAsync();
        SeedMagpie(host);
        await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[] { new { source = "magpie", @ref = "magpie:ds" } });

        var item = (await SourceAsync(host, "magpie"))["items"]!.AsArray().Single(i => i!["ref"]!.GetValue<string>() == "magpie:ds")!;
        Assert.Equal("same", item["status"]!.GetValue<string>());
        Assert.Equal("My DeepSeek", item["existing"]!["name"]!.GetValue<string>());

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[] { new { source = "magpie", @ref = "magpie:ds" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!["added"]!.AsArray());
        Assert.Equal("same", body["skipped"]!.AsArray().Single()!["reason"]!.GetValue<string>());
        Assert.Single(await host.Db.Providers.ListAsync());
    }

    [Fact]
    public async Task Same_Address_With_Another_Key_Is_Offered_As_A_New_Provider_With_A_Distinct_Name()
    {
        await using var host = await TestHost.StartAsync();
        SeedMagpie(host);
        await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "My DeepSeek", templateId = "deepseek", apiKey = "sk-another-key-9999", models = new[] { "deepseek-chat" },
        });
        var item = (await SourceAsync(host, "magpie"))["items"]!.AsArray().Single(i => i!["ref"]!.GetValue<string>() == "magpie:ds")!;
        Assert.Equal("sameHost", item["status"]!.GetValue<string>());

        await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[] { new { source = "magpie", @ref = "magpie:ds" } });
        var names = (await host.Db.Providers.ListAsync()).Select(p => p.Name).Order().ToList();
        Assert.Equal(["My DeepSeek", "My DeepSeek (2)"], names);
    }

    [Fact]
    public async Task A_Changed_Source_Rejects_The_Whole_Batch()
    {
        await using var host = await TestHost.StartAsync();
        SeedMagpie(host);
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[]
        {
            new { source = "magpie", @ref = "magpie:ds" }, new { source = "magpie", @ref = "magpie:vanished" },
        });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.NotNull(body!["error"]);
        Assert.Empty(await host.Db.Providers.ListAsync());
    }

    [Fact]
    public async Task Empty_Selection_And_Unknown_Source_Are_Rejected()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/providers/import", Array.Empty<object>())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[] { new { source = "nope", @ref = "x" } })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/api/providers/import/sources?source=nope")).Status);
    }

    [Fact]
    public async Task Import_Requires_The_Admin_Header()
    {
        await using var host = await TestHost.StartAsync();
        using var plain = host.App.GetTestClient();
        using var res = await plain.PostAsJsonAsync("/api/providers/import", new[] { new { source = "magpie", @ref = "x" } });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Codex_And_Claude_Code_Sources_Import_Into_One_Merged_Provider_Per_Source()
    {
        await using var host = await TestHost.StartAsync();
        Write(host, ".codex/config.toml", """
            [model_providers.relay]
            name = "Codex Relay"
            base_url = "https://relay.example/v1"
            experimental_bearer_token = "tok-codex"
            """);
        Write(host, ".claude/settings.json", """{"env":{"ANTHROPIC_BASE_URL":"https://relay.example","ANTHROPIC_AUTH_TOKEN":"tok-codex"}}""");

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[]
        {
            new { source = "codex", @ref = "codex:relay" }, new { source = "claude-code", @ref = "claude-code:default" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!["added"]!.AsArray().Count); // different sources are never merged
        var codex = (await host.Db.Providers.ListAsync()).Single(p => p.Name == "Codex Relay");
        Assert.Equal("custom-responses", codex.TemplateId);
        Assert.Equal("https://relay.example/v1", codex.Endpoints.Single().BaseUrl);
    }

    [Fact]
    public async Task CcSwitch_Database_Is_Read_Through_The_Real_Read_Only_Opener()
    {
        await using var host = await TestHost.StartAsync();
        var path = Path.Combine(host.ClientHome, ".cc-switch", "cc-switch.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $$$"""
                CREATE TABLE providers (id TEXT, app_type TEXT, name TEXT, settings_config TEXT, website_url TEXT);
                INSERT INTO providers VALUES ('1','claude','CC Relay','{"env":{"ANTHROPIC_BASE_URL":"https://cc.example","ANTHROPIC_AUTH_TOKEN":"{{{Secret}}}"}}',NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var source = await SourceAsync(host, "cc-switch");
        Assert.Equal("CC Relay", source["items"]!.AsArray().Single()!["name"]!.GetValue<string>());
        Assert.DoesNotContain(Secret, source.ToJsonString());
    }
}
