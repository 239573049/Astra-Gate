using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Providers;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.ProviderDetail;
using Astra.Desktop.Pages.Providers;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Astra.Desktop.UiTests;

/// <summary>Issue #33 on the desktop: removing several of a provider's models at once, and the backup and restore sheet.</summary>
public class ProviderBackupUiTests
{
    private const string Preview = """
        {"secrets":"none","items":[
          {"index":0,"name":"Alpha","status":"new","modelCount":2,"hasKey":false,"endpoints":[{"protocol":"openai-chat","baseUrl":"https://a.example/v1"}],"ignored":[]},
          {"index":1,"name":"Beta","status":"same","modelCount":1,"hasKey":true,"keyMasked":"sk-…1234","existing":{"id":"p1","name":"Beta"},"ignored":[]}]}
        """;

    /// <summary>Answers some POSTs itself (and records them); everything else is the fixture set's.</summary>
    private sealed class Tap(HttpMessageHandler inner, Dictionary<string, string> answers) : DelegatingHandler(inner)
    {
        public List<(string Path, string Body)> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && answers.TryGetValue(path, out var json))
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Posts) Posts.Add((path, body));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeFiles(string? toRead = null) : IBackupFiles
    {
        public string? Saved { get; private set; }

        public string? SavedName { get; private set; }

        public Task<string?> ReadAsync() => Task.FromResult(toRead);

        public Task<bool> SaveAsync(string suggestedName, string text)
        {
            (SavedName, Saved) = (suggestedName, text);
            return Task.FromResult(true);
        }
    }

    private static (PageContext Context, Tap Tap) Context(Dictionary<string, string> answers)
    {
        var fx = Scenes.Fixtures();
        var tap = new Tap(fx.Handler(), answers);
        return (PageContext.Create(new ApiClient("http://fixture", tap), AvaloniaUiDispatcher.Instance, clock: new FixedClock(fx.Now)), tap);
    }

    private static TKCode.Arc.Button? ButtonNamed(Control root, string name)
        => root.GetLogicalDescendants().OfType<TKCode.Arc.Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == name);

    private static void Click(TKCode.Arc.Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Ticked_models_are_removed_in_one_request_and_the_selection_clears()
    {
        var (ctx, tap) = Context(new() { ["/api/providers/prov_openai/models/delete"] = """{"deleted":2}""" });
        var page = new ProviderDetailPage(ctx, "prov_openai");
        page.Activate();
        var view = new ProviderDetailView(page);
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        SceneCapture.Await(page.WhenSettledAsync(), TimeSpan.FromSeconds(20));
        SceneCapture.Pump(2);
        var tab = view.ModelsTab!;

        Assert.Null(ButtonNamed(tab, Loc.T("providers.removeSelected"))); // nothing ticked: no button
        tab.Tick(100, true);
        tab.Tick(103, true);
        SceneCapture.Pump(2);
        var remove = ButtonNamed(tab, Loc.T("providers.removeSelected"));
        Assert.NotNull(remove);

        Click(remove!);
        SceneCapture.Await(ctx.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
        SceneCapture.Pump(3);

        var post = Assert.Single(tap.Posts);
        Assert.Equal("/api/providers/prov_openai/models/delete", post.Path);
        Assert.Equal("""{"ids":[100,103]}""", post.Body);
        Assert.Null(ButtonNamed(tab, Loc.T("providers.removeSelected")));
        page.Deactivate();
        window.Close();
    }

    [AvaloniaFact]
    public void Declining_the_confirmation_removes_nothing()
    {
        var (ctx, tap) = Context(new() { ["/api/providers/prov_openai/models/delete"] = """{"deleted":1}""" });
        ((NullFeedback)ctx.Feedback).Answer = false;
        var page = new ProviderDetailPage(ctx, "prov_openai");
        page.Activate();
        var view = new ProviderDetailView(page);
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        SceneCapture.Await(page.WhenSettledAsync(), TimeSpan.FromSeconds(20));
        SceneCapture.Pump(2);
        view.ModelsTab!.Tick(101, true);
        SceneCapture.Pump(2);

        Click(ButtonNamed(view.ModelsTab!, Loc.T("providers.removeSelected"))!);
        SceneCapture.Await(ctx.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(10));
        SceneCapture.Pump(3);

        Assert.Empty(tap.Posts);
        Assert.NotNull(ButtonNamed(view.ModelsTab!, Loc.T("providers.removeSelected"))); // still ticked
        page.Deactivate();
        window.Close();
    }

    private static (ProvidersViewModel Vm, ProviderBackupSheet Sheet, Tap Tap, FakeFiles Files) Backup(string? fileToRead = null)
    {
        var (ctx, tap) = Context(new()
        {
            ["/api/providers/backup/export"] = """{"format":"astra-providers","version":1,"secrets":"passphrase","providers":[{"name":"Alpha"}]}""",
            ["/api/providers/backup/preview"] = Preview,
            ["/api/providers/backup/import"] = """{"added":[{"id":"n1","name":"Alpha"}],"skipped":[{"index":1,"reason":"same"}]}""",
        });
        var vm = new ProvidersViewModel(ctx, null);
        var files = new FakeFiles(fileToRead);
        var sheet = new ProviderBackupSheet(vm, files);
        var window = new Window { Content = sheet, Width = 900, Height = 800 };
        window.Show();
        return (vm, sheet, tap, files);
    }

    private const string PlainFile = """{"format":"astra-providers","version":1,"secrets":"none","providers":[{"name":"Alpha"},{"name":"Beta"}]}""";

    [AvaloniaFact]
    public void A_chosen_file_is_previewed_and_only_the_new_entries_are_imported()
    {
        var (_, sheet, tap, _) = Backup();
        sheet.Open();

        SceneCapture.Await(sheet.LoadAsync(PlainFile, null), TimeSpan.FromSeconds(10));
        SceneCapture.Pump(2);

        Assert.Equal(2, sheet.Preview!.Items.Count);
        Assert.Equal([0], sheet.Ticked);
        Assert.Contains("/api/providers/backup/preview", tap.Posts.Select(p => p.Path));

        SceneCapture.Await(sheet.ImportAsync(), TimeSpan.FromSeconds(10));
        var import = tap.Posts.Single(p => p.Path == "/api/providers/backup/import");
        Assert.Equal([0], JsonNode.Parse(import.Body)!["indexes"]!.AsArray().Select(i => (int)i!).ToArray());
        Assert.Null(sheet.ImportError);
    }

    [AvaloniaFact]
    public void A_file_that_is_not_a_backup_is_refused_without_asking_the_server()
    {
        var (_, sheet, tap, _) = Backup();
        sheet.Open();

        SceneCapture.Await(sheet.LoadAsync("""{"hello":1}""", null), TimeSpan.FromSeconds(10));

        Assert.Equal(Loc.T("backup.import.badFile"), sheet.ImportError);
        Assert.Null(sheet.Preview);
        Assert.Empty(tap.Posts);
    }

    [AvaloniaFact]
    public void An_encrypted_file_waits_for_its_passphrase_before_it_is_previewed()
    {
        var (_, sheet, tap, _) = Backup();
        sheet.Open();
        const string encrypted = """{"format":"astra-providers","version":1,"secrets":"passphrase","providers":[{"name":"Alpha"}]}""";

        SceneCapture.Await(sheet.LoadAsync(encrypted, null), TimeSpan.FromSeconds(10));
        Assert.Null(sheet.Preview);
        Assert.Empty(tap.Posts);

        sheet.TypeImportPassphrase("correct horse battery");
        SceneCapture.Await(sheet.PreviewAsync(), TimeSpan.FromSeconds(10));
        Assert.NotNull(sheet.Preview);
        Assert.Equal("correct horse battery", (string?)JsonNode.Parse(Assert.Single(tap.Posts).Body)!["passphrase"]);
    }

    [AvaloniaFact]
    public void Export_saves_the_servers_file_under_a_dated_name()
    {
        var (_, sheet, tap, files) = Backup();
        sheet.Open();
        sheet.SetMode(ProviderBackupLogic.Passphrase);
        sheet.TypeExportPassphrase("correct horse battery");

        var export = sheet.ExportAsync();
        SceneCapture.Await(export, TimeSpan.FromSeconds(10));

        Assert.True(export.Result);
        Assert.Equal("""{"secrets":"passphrase","passphrase":"correct horse battery"}""", Assert.Single(tap.Posts).Body);
        Assert.StartsWith("astra-providers-", files.SavedName);
        Assert.EndsWith(".json", files.SavedName);
        Assert.Equal("astra-providers", (string?)JsonNode.Parse(files.Saved!)!["format"]);
    }

    [AvaloniaFact]
    public void The_providers_page_asks_for_the_backup_sheet()
    {
        var (vm, _, _, _) = Backup();
        var asked = 0;
        vm.BackupRequested += () => asked++;
        vm.RequestBackup();
        Assert.Equal(1, asked);
    }
}
