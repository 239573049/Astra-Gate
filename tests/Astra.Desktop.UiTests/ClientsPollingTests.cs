using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Clients;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Issue #16: during a Claude Code update the selected client's panel jumped every second or two — each poll of the job (800 ms) and of the
/// client list (2 s) rebuilt the whole panel, though only the job's output had changed. The panel is rebuilt only when what it shows changes.
/// </summary>
public class ClientsPollingTests
{
    [Fact]
    public void A_watched_query_calls_back_only_when_what_was_read_changed()
    {
        using var queries = new QueryClient();
        var job = queries.Query<ClientInstallJobDto?>("client-install-job", _ => Task.FromResult<ClientInstallJobDto?>(null));
        job.SetData(Job("running", 1));
        var redraws = 0;
        var watch = new QueryWatch(() => redraws++) { Attached = true };
        watch.BeginPass();
        watch.Use(job, j => (j?.Command, j?.State));
        watch.EndPass();

        job.SetData(Job("running", 2)); // more output: not shown by the panel
        Assert.Equal(0, redraws);
        job.SetData(Job("succeeded", 3));
        Assert.Equal(1, redraws);
        watch.Clear();
    }

    [AvaloniaFact]
    public void Polls_during_an_update_leave_the_panel_alone_until_the_run_ends()
    {
        var server = new UpdateServer();
        var fixtures = Scenes.Fixtures();
        var api = Scenes.FixtureApi(new UpdateHandler(fixtures.Handler(), server));
        ShellView? view = null;
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, feedback: new ArcFeedback(() => view), clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(AppRoute.Clients, "claude-code");
        view = new ShellView { DataContext = shell };
        var window = new Window { Content = view, Width = 1440, Height = 900 };
        window.Show();
        try
        {
            SceneCapture.Await(context.Queries.WhenIdleAsync(TimeSpan.FromSeconds(3)), TimeSpan.FromSeconds(10));
            SceneCapture.Pump(20);
            var panel = ((ClientsPageViewModel)shell.Navigator.Current).Panel!;
            var root = (StackPanel)panel.Content!;
            var header = root.Children[0];

            // A few job polls (each with more output) and a client-list poll with the same answer.
            var polls = server.JobPolls;
            PumpFor(TimeSpan.FromSeconds(2.6));
            Assert.True(server.JobPolls - polls >= 2, $"the job was polled {server.JobPolls - polls} times");
            Assert.Same(header, root.Children[0]);

            // The run ends: the list refetches (busy off, new version) and the panel shows it.
            server.Finished = true;
            var limit = System.Diagnostics.Stopwatch.StartNew();
            while (ReferenceEquals(header, root.Children[0]) && limit.Elapsed < TimeSpan.FromSeconds(6)) PumpFor(TimeSpan.FromMilliseconds(100));
            Assert.NotSame(header, root.Children[0]);
        }
        finally
        {
            shell.Navigator.Current.Deactivate();
            context.Queries.Dispose();
            window.Close();
        }
    }

    private static void PumpFor(TimeSpan time)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < time) SceneCapture.Pump(1);
    }

    private static ClientInstallJobDto Job(string state, int lines) =>
        new("claude-code", "update", state, "claude update", DateTimeOffset.Parse("2026-10-30T00:26:00Z"), null, null,
            [.. Enumerable.Range(0, lines).Select(i => $"line {i}")], null);

    /// <summary>A Claude Code update in progress: the job's output grows on every poll until <see cref="Finished"/>.</summary>
    private sealed class UpdateServer
    {
        private int _polls;
        public volatile bool Finished;
        public int JobPolls => Volatile.Read(ref _polls);

        public string JobJson()
        {
            var n = Interlocked.Increment(ref _polls);
            var log = new JsonArray("$ claude update", "Current version: 2.1.312");
            for (var i = 0; i < n; i++) log.Add("Updating… " + i);
            return new JsonObject
            {
                ["kind"] = "claude-code", ["action"] = "update", ["state"] = Finished ? "succeeded" : "running", ["command"] = "claude update",
                ["startedAt"] = "2026-10-30T00:26:00Z", ["finishedAt"] = Finished ? "2026-10-30T00:27:00Z" : null, ["exitCode"] = Finished ? 0 : null,
                ["log"] = log, ["hint"] = null, ["elevated"] = false,
            }.ToJsonString();
        }

        public string ClientsJson()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "clients.json");
            var list = (JsonArray)JsonNode.Parse(File.ReadAllText(path))!;
            var claude = list.OfType<JsonObject>().First(c => (string?)c["kind"] == "claude-code");
            claude["install"] = new JsonObject
            {
                ["method"] = "script", ["package"] = null, ["homepageUrl"] = "https://example.com", ["installed"] = true,
                ["executable"] = "C:/Users/me/.local/bin/claude.exe", ["version"] = Finished ? "2.1.314" : "2.1.312", ["latestVersion"] = "2.1.314",
                ["latestCheckedAt"] = "2026-10-30T00:00:00Z", ["latestError"] = null, ["updateAvailable"] = !Finished,
                ["installCommand"] = null, ["updateCommand"] = Finished ? null : "claude update", ["updateVia"] = "self", ["blocker"] = null, ["busy"] = !Finished,
            };
            return list.ToJsonString();
        }
    }

    private sealed class UpdateHandler(HttpMessageHandler inner, UpdateServer server) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string? body = null;
            if (path is "/api/clients" or "/api/clients/check-updates") body = server.ClientsJson();
            else if (path == "/api/clients/claude-code/direct") body = """{"mode":"gateway","accountId":null,"accounts":[]}""";
            else if (path == "/api/clients/claude-code/models") body = """["claude-sonnet-4-5","claude-opus-4-1","claude-haiku-4-5"]""";
            else if (path == "/api/clients/claude-code/install") body = server.JobJson();
            if (body is null) return await base.SendAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
