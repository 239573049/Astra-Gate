using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Tests.Api;

namespace Astra.Desktop.Tests.Providers;

/// <summary>The provider backup of the desktop (issue #33): the rules, the server calls and the strings the sheet uses.</summary>
[Collection("Loc")]
public sealed class ProviderBackupTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public ProviderBackupTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    private const string Preview = """
        {"secrets":"none","items":[
          {"index":0,"name":"Alpha","status":"new","modelCount":2,"hasKey":false,"keyMasked":null,"endpoints":[{"protocol":"openai-chat","baseUrl":"https://a.example/v1"}],"authScheme":"bearer","ignored":[]},
          {"index":1,"name":"Beta","status":"sameHost","modelCount":1,"hasKey":true,"keyMasked":"sk-…1234","existing":{"id":"p1","name":"My beta"},"ignored":["proxy"]},
          {"index":2,"name":"Gamma","status":"same","modelCount":0,"hasKey":true,"keyMasked":"sk-…9999","existing":{"id":"p2","name":"Gamma"},"ignored":[]},
          {"index":3,"name":"Delta","status":"invalid","modelCount":0,"hasKey":false,"error":"Configure one endpoint per protocol","ignored":[]}]}
        """;

    private static JsonObject File(string secrets = "none")
        => new() { ["format"] = "astra-providers", ["version"] = 1, ["secrets"] = secrets, ["providers"] = new JsonArray(new JsonObject { ["name"] = "Alpha" }, new JsonObject { ["name"] = "Beta" }) };

    private static ProviderQueries Make(ApiClient api) => new(new QueryClient(), api, () => DateTimeOffset.Parse("2026-11-11T12:00:00Z"));

    [Fact]
    public void Only_a_backup_file_is_accepted_and_its_keys_policy_and_size_are_read()
    {
        var file = ProviderBackupLogic.ParseFile(File("passphrase").ToJsonString())!;
        Assert.Equal(2, ProviderBackupLogic.Count(file));
        Assert.True(ProviderBackupLogic.NeedsPassphrase(file));
        Assert.False(ProviderBackupLogic.NeedsPassphrase(File()));

        Assert.Null(ProviderBackupLogic.ParseFile("""{"hello":1}"""));
        Assert.Null(ProviderBackupLogic.ParseFile("""{"format":"astra-providers"}"""));
        Assert.Null(ProviderBackupLogic.ParseFile("not json"));
        Assert.Null(ProviderBackupLogic.ParseFile("[1,2]"));
    }

    [Fact]
    public void A_passphrase_needs_eight_characters_and_the_other_policies_need_none()
    {
        Assert.True(ProviderBackupLogic.CanExport(ProviderBackupLogic.None, ""));
        Assert.True(ProviderBackupLogic.CanExport(ProviderBackupLogic.Plaintext, ""));
        Assert.False(ProviderBackupLogic.CanExport(ProviderBackupLogic.Passphrase, "short"));
        Assert.True(ProviderBackupLogic.CanExport(ProviderBackupLogic.Passphrase, "long enough"));
        Assert.Equal("astra-providers-2026-11-11.json", ProviderBackupLogic.FileName(DateTimeOffset.Parse("2026-11-11T23:30:00Z")));
    }

    [Fact]
    public void The_preview_ticks_only_new_entries_and_never_lets_same_or_invalid_ones_through()
    {
        var preview = ProviderBackupLogic.ParsePreview(JsonNode.Parse(Preview) as JsonObject);
        Assert.Equal(4, preview.Items.Count);
        Assert.Equal(["https://a.example/v1"], preview.Items[0].Addresses);
        Assert.Equal("My beta", preview.Items[1].ExistingName);
        Assert.Equal(["proxy"], preview.Items[1].Ignored);
        Assert.Equal("Configure one endpoint per protocol", preview.Items[3].Error);

        Assert.Equal([0], ProviderBackupLogic.DefaultSelection(preview));
        // a ticked "same" or "invalid" entry is still not imported; an address clash can be chosen
        Assert.Equal([0, 1], ProviderBackupLogic.Chosen(preview, new HashSet<int> { 0, 1, 2, 3 }));
        Assert.Equal(ImportTone.Green, ProviderBackupLogic.ToneOf("new"));
        Assert.Equal(ImportTone.Orange, ProviderBackupLogic.ToneOf("sameHost"));
        Assert.Equal(ImportTone.Red, ProviderBackupLogic.ToneOf("invalid"));
        Assert.Equal(ImportTone.Neutral, ProviderBackupLogic.ToneOf("same"));
    }

    [Fact]
    public void The_toast_says_how_many_were_created_and_skipped()
    {
        Assert.Equal("Imported 2 providers", ProviderBackupLogic.DoneText(new BackupResult(2, 0)));
        Assert.Equal("Imported 1, skipped 3", ProviderBackupLogic.DoneText(new BackupResult(1, 3)));
    }

    [Fact]
    public async Task Export_posts_the_policy_and_only_a_passphrase_that_belongs_to_it()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, File().ToJsonString()));
        using var api = new ApiClient(server.Url);
        var q = Make(api);

        var file = await q.ExportBackupAsync("none", "ignored");
        await q.ExportBackupAsync("passphrase", "correct horse battery");

        Assert.Equal(2, ProviderBackupLogic.Count(file));
        var bodies = server.Requests.Select(r => r.Body).ToList();
        Assert.Contains("""{"secrets":"none"}""", bodies);
        Assert.Contains("""{"secrets":"passphrase","passphrase":"correct horse battery"}""", bodies);
        Assert.All(server.Requests, r =>
        {
            Assert.Equal("POST", r.Method);
            Assert.Equal("/api/providers/backup/export", r.Url);
            Assert.Equal("1", r.Admin);
        });
    }

    [Fact]
    public async Task Preview_sends_the_file_and_the_passphrase_only_for_an_encrypted_one()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, Preview));
        using var api = new ApiClient(server.Url);
        var q = Make(api);

        var preview = await q.PreviewBackupAsync(File(), "unused");
        await q.PreviewBackupAsync(File("passphrase"), "correct horse battery");

        Assert.Equal(4, preview.Items.Count);
        var sent = server.Requests.Select(r => JsonNode.Parse(r.Body!)!).ToList();
        var plain = Assert.Single(sent, b => b["passphrase"] is null);
        Assert.Equal("astra-providers", (string?)plain["file"]!["format"]);
        Assert.Null(plain["indexes"]);
        var sealedBody = Assert.Single(sent, b => b["passphrase"] is not null);
        Assert.Equal("correct horse battery", (string?)sealedBody["passphrase"]);
        Assert.All(server.Requests, r => Assert.Equal("/api/providers/backup/preview", r.Url));
    }

    [Fact]
    public async Task Import_sends_the_ticked_indexes_and_refreshes_the_provider_lists()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, """{"added":[{"id":"n1","name":"Alpha"}],"skipped":[{"index":1,"reason":"same"}]}"""));
        using var api = new ApiClient(server.Url);
        var queries = new QueryClient();
        var list = queries.Query(QueryKeys.Providers, _ => Task.FromResult(new JsonArray()));
        await list.RefetchAsync();
        Assert.False(list.IsStale);
        var q = new ProviderQueries(queries, api, () => DateTimeOffset.Parse("2026-11-11T12:00:00Z"));

        var result = await q.ImportBackupAsync(File(), null, [0, 2]);

        Assert.Equal(new BackupResult(1, 1), result);
        var body = JsonNode.Parse(Assert.Single(server.Requests).Body!)!;
        Assert.Equal([0, 2], body["indexes"]!.AsArray().Select(i => (int)i!).ToArray());
        Assert.Equal("/api/providers/backup/import", Assert.Single(server.Requests).Url);
        Assert.True(list.IsStale);
    }

    [Fact]
    public async Task A_refusal_surfaces_the_servers_message()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 403, """{"error":"A backup with API keys can only be made on this machine"}"""));
        using var api = new ApiClient(server.Url);
        var q = Make(api);
        var e = await Assert.ThrowsAsync<ApiException>(() => q.ExportBackupAsync("plaintext", null));
        Assert.Equal("A backup with API keys can only be made on this machine", e.Message);
    }

    [Fact]
    public void Every_string_of_the_sheet_and_of_model_selection_is_in_the_catalog()
    {
        string[] keys =
        [
            "providers.backup", "backup.title", "backup.detail", "backup.export.title", "backup.export.secrets", "backup.secrets.none", "backup.secrets.passphrase",
            "backup.secrets.plaintext", "backup.secrets.hint", "backup.passphrase", "backup.passphrase.hint", "backup.plaintext.warn", "backup.export.run",
            "backup.export.done", "backup.import.title", "backup.import.choose", "backup.import.encrypted", "backup.import.preview", "backup.import.run",
            "backup.import.done", "backup.import.doneSkipped", "backup.import.badFile", "backup.import.noKeys", "backup.status.new", "backup.status.sameHost",
            "backup.status.same", "backup.status.invalid", "backup.models", "providers.selectAllModels", "providers.selectModel", "providers.selectedModels",
            "providers.removeSelected", "providers.removeSelectedConfirm", "import.ignored", "common.error", "common.delete",
        ];
        Assert.Equal("", string.Join(", ", keys.Where(k => !Loc.Has(k))));
    }
}
