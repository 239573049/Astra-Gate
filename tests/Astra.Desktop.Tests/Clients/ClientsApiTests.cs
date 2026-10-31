using System.ComponentModel;
using System.Net;
using System.Text;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;

namespace Astra.Desktop.Tests.Clients;

/// <summary>
/// Issue #16: while an install / update runs, the client list polls every 2 s and the job every 800 ms, and the Clients page redraws
/// the selected client's panel from them. A poll that brings the same answer must not report new data, or every poll rebuilt the panel.
/// </summary>
public sealed class ClientsApiTests
{
    private sealed class Answers(Func<string, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request.RequestUri!.AbsolutePath), Encoding.UTF8, "application/json") });
    }

    private static string Job(int lines) =>
        $$"""{"kind":"claude-code","action":"update","state":"running","command":"claude update","startedAt":"2026-10-30T00:26:00Z","finishedAt":null,"exitCode":null,"log":[{{string.Join(",", Enumerable.Range(0, lines).Select(i => $"\"line {i}\""))}}],"hint":null,"elevated":false}""";

    private const string Clients = """
        [{"kind":"claude-code","name":"Claude Code","protocol":"anthropic","availability":"available","availabilityReason":null,"mode":"switch",
          "detection":{"installed":true,"configExists":true,"version":"2.1.312","configPaths":["C:/Users/me/.claude/settings.json"]},
          "status":"enabled","enabled":true,"providerId":"prov_anthropic","accountId":null,"selectedModel":null,"extras":{"options":{"context1m":true}},
          "bindings":[{"providerId":"prov_anthropic","accountId":null}],"appliedAt":"2026-10-30T00:26:00Z","warnings":[],"requiresRestart":false,
          "configOutdated":false,"tokenId":"tok_default","install":{"method":"script","package":null,"homepageUrl":"https://example.com","installed":true,
          "executable":"C:/Users/me/.local/bin/claude.exe","version":"2.1.312","latestVersion":"2.1.314","latestCheckedAt":"2026-10-30T00:00:00Z",
          "latestError":null,"updateAvailable":true,"installCommand":null,"updateCommand":"claude update","updateVia":"self","blocker":null,"busy":true},
          "options":null,"repairs":null,"protocols":null}]
        """;

    private static List<string> Watch(INotifyPropertyChanged source)
    {
        var seen = new List<string>();
        source.PropertyChanged += (_, e) => seen.Add(e.PropertyName!);
        return seen;
    }

    [Fact]
    public async Task Polls_that_bring_nothing_new_report_no_new_data()
    {
        var lines = 2;
        using var api = new ApiClient("http://fixture", new Answers(path => path.EndsWith("/install", StringComparison.Ordinal) ? Job(lines) : Clients));
        using var queries = new QueryClient();
        var clientsApi = new ClientsApi(api, queries);
        var job = clientsApi.InstallJob("claude-code");
        var list = clientsApi.Clients();
        await job.RefetchAsync();
        await list.RefetchAsync();
        var (heldJob, heldList) = (job.Data, list.Data);
        var jobEvents = Watch(job);
        var listEvents = Watch(list);

        await job.RefetchAsync();
        await list.RefetchAsync();
        Assert.Same(heldJob, job.Data);
        Assert.Same(heldList, list.Data);
        Assert.DoesNotContain("Data", jobEvents);
        Assert.DoesNotContain("Data", listEvents);

        // New output is new data (the log sheet follows it).
        lines = 3;
        await job.RefetchAsync();
        Assert.Contains("Data", jobEvents);
        Assert.Equal(3, job.Data!.Log.Count);
    }
}
