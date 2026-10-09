using Astra.Clients.Import;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Import;
using Astra.Core.Models;
using Astra.Data.Foreign;
using Microsoft.Data.Sqlite;

namespace Astra.Clients.Tests;

public sealed class ImportReaderTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _state = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _home.Dispose();
    }

    private ImportContext Context() => new(_home.Env, new ForeignSqliteOpener(), _state, url => url.Contains(":17321", StringComparison.Ordinal));

    private ImportSourceResult Read(string id) => ImportSourceRegistry.CreateDefault(Context()).Read(id)!;

    private void Sql(string path, params string[] statements)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    // ------------------------------------------------------------------ CC Switch

    private string CcSwitchDb => _home.File(".cc-switch", "cc-switch.db");

    private void CcRow(string id, string app, string name, string settings, string? website = null) =>
        Sql(CcSwitchDb, $"INSERT INTO providers (id, app_type, name, settings_config, website_url) VALUES ({Quote(id)}, {Quote(app)}, {Quote(name)}, {Quote(settings)}, {(website is null ? "NULL" : Quote(website))});");

    private void CcSchema() => Sql(CcSwitchDb,
        "CREATE TABLE providers (id TEXT NOT NULL, app_type TEXT NOT NULL, name TEXT NOT NULL, settings_config TEXT NOT NULL, website_url TEXT, category TEXT, PRIMARY KEY (id, app_type));",
        "PRAGMA user_version = 20;");

    [Fact]
    public void CcSwitch_Reports_Not_Found_When_Absent()
    {
        var r = Read(ImportSourceIds.CcSwitch);
        Assert.False(r.Found);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void CcSwitch_Maps_Every_App_Type_And_Skips_Official_Logins()
    {
        CcSchema();
        CcRow("c1", "claude", "Relay A", """{"env":{"ANTHROPIC_BASE_URL":"https://relay-a.example/","ANTHROPIC_AUTH_TOKEN":"sk-claude-1","ANTHROPIC_MODEL":"claude-sonnet-4"}}""", "https://relay-a.example");
        CcRow("c2", "claude", "Official", """{"env":{}}""");
        CcRow("x1", "codex", "Relay B", """
            {"auth":{"OPENAI_API_KEY":"sk-codex-1"},"config":"model_provider = \"my.relay\"\nmodel = \"gpt-5\"\n\n[model_providers.\"my.relay\"]\nbase_url = \"https://relay-b.example/v1\"\nwire_api = \"chat\"\n"}
            """.Trim());
        CcRow("g1", "gemini", "Gemini relay", """{"env":{"GEMINI_API_KEY":"g-key-1","GOOGLE_GEMINI_BASE_URL":"https://gem.example/v1beta","GEMINI_MODEL":"gemini-2.5-pro"}}""");
        CcRow("o1", "opencode", "OC", """{"npm":"@ai-sdk/anthropic","options":{"baseURL":"https://oc.example","apiKey":"oc-key"},"models":{"m-1":{},"m-2":{}}}""");

        var r = Read(ImportSourceIds.CcSwitch);
        Assert.True(r.Found);
        Assert.Null(r.Error);
        var byName = r.Items.ToDictionary(i => i.Name);

        var a = byName["Relay A"];
        Assert.Equal(AuthSchemes.Bearer, a.AuthScheme);
        Assert.Equal("sk-claude-1", a.ApiKey);
        Assert.Equal([new ImportEndpoint(ApiProtocol.Anthropic, "https://relay-a.example")], a.Endpoints); // trailing slash trimmed
        Assert.Equal(["claude-sonnet-4"], a.Models);
        Assert.Equal("https://relay-a.example", a.Website);

        Assert.Equal(ImportSkipReasons.OfficialLogin, byName["Official"].SkipReason);

        var b = byName["Relay B"];
        Assert.Equal([new ImportEndpoint(ApiProtocol.OpenAIChat, "https://relay-b.example/v1")], b.Endpoints);
        Assert.Equal("sk-codex-1", b.ApiKey);
        Assert.Equal(["gpt-5"], b.Models);

        var g = byName["Gemini relay"];
        Assert.Equal(AuthSchemes.XGoogApiKey, g.AuthScheme);
        Assert.Equal(ApiProtocol.Gemini, g.Endpoints[0].Protocol);

        var o = byName["OC"];
        Assert.Equal(ApiProtocol.Anthropic, o.Endpoints[0].Protocol);
        Assert.Equal(["m-1", "m-2"], o.Models);
        Assert.Equal(AuthSchemes.XApiKey, o.AuthScheme);
    }

    [Fact]
    public void CcSwitch_Codex_Defaults_To_Responses_With_Inline_Bearer_Token()
    {
        CcSchema();
        CcRow("x2", "codex", "Inline", """
            {"auth":{},"config":"model_provider = \"r\"\n[model_providers.r]\nbase_url = \"https://r.example/v1\"\nexperimental_bearer_token = \"tok-inline\"\n"}
            """.Trim());
        var item = Read(ImportSourceIds.CcSwitch).Items.Single();
        Assert.Equal(ApiProtocol.OpenAIResponses, item.Endpoints[0].Protocol);
        Assert.Equal("tok-inline", item.ApiKey);
    }

    [Fact]
    public void CcSwitch_Merges_The_Same_Relay_Configured_For_Two_Apps()
    {
        CcSchema();
        CcRow("c1", "claude", "Relay", """{"env":{"ANTHROPIC_BASE_URL":"https://same.example","ANTHROPIC_AUTH_TOKEN":"sk-same"}}""");
        CcRow("x1", "codex", "Relay (codex)", """
            {"auth":{"OPENAI_API_KEY":"sk-same"},"config":"model_provider = \"r\"\n[model_providers.r]\nbase_url = \"https://same.example/v1\"\n"}
            """.Trim());
        var item = Read(ImportSourceIds.CcSwitch).Items.Single();
        Assert.Equal("Relay", item.Name);
        Assert.Equal([ApiProtocol.Anthropic, ApiProtocol.OpenAIResponses], item.Endpoints.Select(e => e.Protocol));
        Assert.Equal("claude, codex", item.FromApp);
        Assert.Equal("cc-switch:claude:c1", item.Ref);
    }

    [Fact]
    public void CcSwitch_Does_Not_Merge_Different_Keys_Or_Hosts()
    {
        CcSchema();
        CcRow("c1", "claude", "A", """{"env":{"ANTHROPIC_BASE_URL":"https://same.example","ANTHROPIC_AUTH_TOKEN":"k1"}}""");
        CcRow("x1", "codex", "B", """{"auth":{"OPENAI_API_KEY":"k2"},"config":"model_provider = \"r\"\n[model_providers.r]\nbase_url = \"https://same.example/v1\"\n"}""");
        Assert.Equal(2, Read(ImportSourceIds.CcSwitch).Items.Count);
    }

    [Fact]
    public void CcSwitch_Reads_The_Legacy_Config_Json()
    {
        _home.WriteFile(_home.File(".cc-switch", "config.json"), """
            {"claude":{"providers":{"p1":{"name":"Old","settingsConfig":{"env":{"ANTHROPIC_BASE_URL":"https://old.example","ANTHROPIC_API_KEY":"sk-old"}},"websiteUrl":"https://old.example"}}}}
            """);
        var item = Read(ImportSourceIds.CcSwitch).Items.Single();
        Assert.Equal("Old", item.Name);
        Assert.Equal(AuthSchemes.XApiKey, item.AuthScheme);
        Assert.Equal("sk-old", item.ApiKey);
    }

    [Fact]
    public void CcSwitch_With_An_Unknown_Layout_Is_An_Error_Not_A_Crash()
    {
        Sql(CcSwitchDb, "CREATE TABLE providers (x TEXT);", "PRAGMA user_version = 99;");
        var r = Read(ImportSourceIds.CcSwitch);
        Assert.True(r.Found);
        Assert.Contains("user_version 99", r.Error);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void CcSwitch_Reading_Does_Not_Modify_The_Database()
    {
        CcSchema();
        CcRow("c1", "claude", "A", """{"env":{"ANTHROPIC_BASE_URL":"https://a.example","ANTHROPIC_AUTH_TOKEN":"k"}}""");
        var before = File.ReadAllBytes(CcSwitchDb);
        Read(ImportSourceIds.CcSwitch);
        Assert.Equal(before, File.ReadAllBytes(CcSwitchDb));
    }

    // ------------------------------------------------------------------ Claude Code

    private string ClaudeSettings => _home.File(".claude", "settings.json");

    [Fact]
    public void ClaudeCode_Reads_Env_From_Jsonc_Settings()
    {
        _home.WriteFile(ClaudeSettings, """
            {
              // gateway
              "env": { "ANTHROPIC_BASE_URL": "https://cc.example", "ANTHROPIC_AUTH_TOKEN": "tok", "ANTHROPIC_MODEL": "m-1", },
            }
            """);
        var item = Read(ImportSourceIds.ClaudeCode).Items.Single();
        Assert.Equal("Claude Code · cc.example", item.Name);
        Assert.Equal("tok", item.ApiKey);
        Assert.Equal(["m-1"], item.Models);
        Assert.Equal("claude-code:default", item.Ref);
    }

    [Fact]
    public void ClaudeCode_Honors_Claude_Config_Dir()
    {
        var dir = _home.File("custom-claude");
        _home.Variables["CLAUDE_CONFIG_DIR"] = dir;
        _home.WriteFile(Path.Combine(dir, "settings.json"), """{"env":{"ANTHROPIC_BASE_URL":"https://x.example","ANTHROPIC_API_KEY":"k"}}""");
        var item = Read(ImportSourceIds.ClaudeCode).Items.Single();
        Assert.Equal(AuthSchemes.XApiKey, item.AuthScheme);
    }

    [Fact]
    public void ClaudeCode_Signed_In_With_An_Account_Yields_Nothing()
    {
        _home.WriteFile(ClaudeSettings, """{"theme":"dark"}""");
        var r = Read(ImportSourceIds.ClaudeCode);
        Assert.True(r.Found);
        Assert.Empty(r.Items);
    }

    [Fact]
    public void ClaudeCode_Taken_Over_By_Astra_Reads_The_Recorded_Originals()
    {
        _home.WriteFile(ClaudeSettings, """{"env":{"ANTHROPIC_BASE_URL":"http://127.0.0.1:17321","ANTHROPIC_AUTH_TOKEN":"astra-local-key"}}""");
        _state.Upsert(State("env.ANTHROPIC_BASE_URL", absent: false, original: "\"https://orig.example\"", applied: "\"http://127.0.0.1:17321\""));
        _state.Upsert(State("env.ANTHROPIC_AUTH_TOKEN", absent: false, original: "\"orig-token\"", applied: "\"astra-local-key\""));
        // The API key Astra removed on takeover is restored from its record too.
        _state.Upsert(State("env.ANTHROPIC_API_KEY", absent: false, original: "\"orig-api-key\"", applied: null));

        var item = Read(ImportSourceIds.ClaudeCode).Items.Single();
        Assert.Equal("https://orig.example", item.Endpoints[0].BaseUrl);
        Assert.Equal("orig-token", item.ApiKey);
        Assert.Null(item.SkipReason);
    }

    [Fact]
    public void ClaudeCode_Taken_Over_With_No_Original_Has_Nothing_To_Import()
    {
        _home.WriteFile(ClaudeSettings, """{"env":{"ANTHROPIC_BASE_URL":"http://127.0.0.1:17321","ANTHROPIC_AUTH_TOKEN":"astra-local-key"}}""");
        _state.Upsert(State("env.ANTHROPIC_BASE_URL", absent: true, original: null, applied: "\"http://127.0.0.1:17321\""));
        _state.Upsert(State("env.ANTHROPIC_AUTH_TOKEN", absent: true, original: null, applied: "\"astra-local-key\""));
        Assert.Empty(Read(ImportSourceIds.ClaudeCode).Items);
    }

    [Fact]
    public void ClaudeCode_Pointing_At_Astra_Without_A_Record_Is_Skipped()
    {
        _home.WriteFile(ClaudeSettings, """{"env":{"ANTHROPIC_BASE_URL":"http://127.0.0.1:17321","ANTHROPIC_AUTH_TOKEN":"astra-local-key"}}""");
        Assert.Equal(ImportSkipReasons.PointsToAstra, Read(ImportSourceIds.ClaudeCode).Items.Single().SkipReason);
    }

    private ClientConfigStateEntry State(string keyPath, bool absent, string? original, string? applied) => new()
    {
        ClientKind = ClientKinds.ClaudeCode, FilePath = ClaudeSettings, KeyPath = keyPath, OriginalAbsent = absent,
        OriginalValueJson = original, AppliedValueJson = applied,
    };

    // ------------------------------------------------------------------ Codex

    private string CodexConfig => _home.File(".codex", "config.toml");

    [Fact]
    public void Codex_Imports_Custom_Providers_With_Inline_Tokens_And_Skips_The_Rest()
    {
        _home.WriteFile(CodexConfig, """
            model_provider = "relay"
            model = "gpt-5"

            [profiles.fast]
            model_provider = "relay"
            model = "gpt-5-mini"

            [model_providers.relay]
            name = "My Relay"
            base_url = "https://relay.example/v1"
            wire_api = "chat"
            experimental_bearer_token = "tok-relay"

            [model_providers.relay.http_headers]
            X-Team = "blue"

            [model_providers."env.only"]
            base_url = "https://env.example/v1"
            env_key = "SOME_KEY"

            [model_providers.astra]
            name = "Astra"
            base_url = "http://127.0.0.1:17321/v1"
            experimental_bearer_token = "astra-local"
            """);
        var r = Read(ImportSourceIds.Codex);
        Assert.Null(r.Error);
        var relay = r.Items.Single(i => i.Name == "My Relay");
        Assert.Equal(ApiProtocol.OpenAIChat, relay.Endpoints[0].Protocol);
        Assert.Equal("tok-relay", relay.ApiKey);
        Assert.Equal("blue", relay.Headers["X-Team"]);
        Assert.Equal(["gpt-5", "gpt-5-mini"], relay.Models.Order());
        Assert.Equal("codex:relay", relay.Ref);

        Assert.Equal(ImportSkipReasons.NoInlineKey, r.Items.Single(i => i.Ref == "codex:env.only").SkipReason);
        Assert.Equal(ImportSkipReasons.PointsToAstra, r.Items.Single(i => i.Ref == "codex:astra").SkipReason);
    }

    [Fact]
    public void Codex_Never_Reads_Auth_Json()
    {
        _home.WriteFile(CodexConfig, "model = \"gpt-5\"\n");
        _home.WriteFile(_home.File(".codex", "auth.json"), """{"OPENAI_API_KEY":"sk-must-not-leak"}""");
        Assert.Empty(Read(ImportSourceIds.Codex).Items);
    }

    [Fact]
    public void Codex_Invalid_Toml_Is_Reported()
    {
        _home.WriteFile(CodexConfig, "this = is = not toml [");
        Assert.NotNull(Read(ImportSourceIds.Codex).Error);
    }

    // ------------------------------------------------------------------ Alma

    private string AlmaDb => _home.File("Library", "Application Support", "alma", "chat_threads.db");

    [Fact]
    public void Alma_Maps_Types_Formats_And_Skips_Logins()
    {
        Sql(AlmaDb,
            "CREATE TABLE providers (id TEXT PRIMARY KEY, name TEXT, type TEXT, api_key TEXT, models TEXT, base_url TEXT, enabled INTEGER, api_format TEXT, is_response_api INTEGER, custom_headers TEXT);",
            "INSERT INTO providers VALUES ('a1','DeepSeek','deepseek','sk-ds','[\"deepseek-chat\"]','',1,NULL,0,NULL);",
            "INSERT INTO providers VALUES ('a2','Claude login','claude-subscription','',NULL,'',1,NULL,0,NULL);",
            "INSERT INTO providers VALUES ('a3','Relay','custom','sk-r','[{\"id\":\"m-a\"},\"m-b\"]','https://relay.example/v1',0,'anthropic',0,'{\"X-A\":\"1\"}');",
            "INSERT INTO providers VALUES ('a4','Resp','custom','sk-x',NULL,'https://resp.example/v1',1,NULL,1,NULL);",
            "INSERT INTO providers VALUES ('a5','Mystery','custom','sk-m',NULL,'',1,NULL,0,NULL);");
        var r = Read(ImportSourceIds.Alma);
        var byName = r.Items.ToDictionary(i => i.Name);

        Assert.Equal("https://api.deepseek.com/v1", byName["DeepSeek"].Endpoints[0].BaseUrl);
        Assert.Equal(["deepseek-chat"], byName["DeepSeek"].Models);
        Assert.Equal(ImportSkipReasons.OfficialLogin, byName["Claude login"].SkipReason);

        var relay = byName["Relay"];
        Assert.True(relay.Off);
        Assert.Equal(ApiProtocol.Anthropic, relay.Endpoints[0].Protocol);
        Assert.Equal(["m-a", "m-b"], relay.Models);
        Assert.Equal("1", relay.Headers["X-A"]);

        Assert.Equal(ApiProtocol.OpenAIResponses, byName["Resp"].Endpoints[0].Protocol);
        Assert.Equal(ImportSkipReasons.NoBaseUrl, byName["Mystery"].SkipReason);
    }

    // ------------------------------------------------------------------ Magpie

    private string MagpieFile => _home.File(".config", "magpie", "providers.json");

    [Fact]
    public void Magpie_Turns_Each_Key_Into_A_Candidate_And_Ignores_Pick_Records()
    {
        _home.WriteFile(MagpieFile, """
            {"providers":[
              {"id":"relay","name":"Relay","key":"k-main","chat":"https://r.example/v1","anthropic":"https://r.example",
               "models":["m-1"],"headers":{"X-T":"1"},"proxy":"127.0.0.1:7890",
               "keys":[{"name":"backup","key":"k-2"},{"name":"anth","key":"k-3","protocol":"anthropic"},{"name":"old","key":"k-4","off":true}]},
              {"id":"claude","models":["claude-opus"]},
              {"id":"gone","name":"Gone","key":"k","chat":"https://g.example/v1","hidden":true}
            ]}
            """);
        var r = Read(ImportSourceIds.Magpie);
        Assert.Null(r.Error);
        Assert.Equal(["Relay", "Relay · backup", "Relay · anth", "Relay · old", "Gone"], r.Items.Select(i => i.Name));

        var main = r.Items[0];
        Assert.Equal("magpie:relay", main.Ref);
        Assert.Equal([ApiProtocol.OpenAIChat, ApiProtocol.Anthropic], main.Endpoints.Select(e => e.Protocol));
        Assert.Equal("http://127.0.0.1:7890", main.Proxy);
        Assert.Equal("1", main.Headers["X-T"]);

        Assert.Equal("magpie:relay#1", r.Items[1].Ref);
        Assert.Equal([ApiProtocol.Anthropic], r.Items[2].Endpoints.Select(e => e.Protocol)); // key limited to one protocol
        Assert.Equal(AuthSchemes.XApiKey, r.Items[2].AuthScheme);
        Assert.True(r.Items[3].Off);
        Assert.True(r.Items[4].Off); // hidden: importable but unticked
    }

    [Fact]
    public void Magpie_Rejects_A_File_That_Is_Not_A_Provider_List()
    {
        _home.WriteFile(MagpieFile, """{"nope":1}""");
        Assert.NotNull(Read(ImportSourceIds.Magpie).Error);
    }

    // ------------------------------------------------------------------ registry

    [Fact]
    public void One_Broken_Source_Does_Not_Hide_The_Others()
    {
        _home.WriteFile(MagpieFile, """{"providers":[{"id":"r","key":"k","chat":"https://r.example/v1"}]}""");
        Sql(CcSwitchDb, "CREATE TABLE providers (x TEXT);");
        var all = ImportSourceRegistry.CreateDefault(Context()).ReadAll();
        Assert.Equal(ImportSourceIds.All, all.Select(r => r.Id));
        Assert.NotNull(all.Single(r => r.Id == ImportSourceIds.CcSwitch).Error);
        Assert.Single(all.Single(r => r.Id == ImportSourceIds.Magpie).Items);
    }

    [Fact]
    public void Invalid_Urls_Are_Skipped_Not_Imported()
    {
        _home.WriteFile(MagpieFile, """{"providers":[{"id":"r","key":"k","chat":"relay.example/v1"}]}""");
        Assert.Equal(ImportSkipReasons.InvalidBaseUrl, Read(ImportSourceIds.Magpie).Items.Single().SkipReason);
    }
}
