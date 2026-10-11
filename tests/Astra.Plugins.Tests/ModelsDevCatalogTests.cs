using System.Net;
using System.Text;
using Astra.Plugins.Runtime;

namespace Astra.Plugins.Tests;

public class ModelsDevCatalogTests
{
    private sealed class Stub(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer());
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fetches_once_then_keeps_a_fresh_copy()
    {
        var plugins = new TestPlugins();
        var stub = new Stub(() => Json("""{"google":{"id":"google","models":{}}}"""));
        var catalog = new ModelsDevCatalog(plugins.Paths, () => new HttpClient(stub));

        var path = await catalog.EnsureAsync((_, _) => { });
        await catalog.EnsureAsync((_, _) => { });

        Assert.Equal(plugins.Paths.PluginModelsDevFile, path);
        Assert.Contains("google", File.ReadAllText(path!));
        Assert.Equal(1, stub.Calls);
    }

    [Fact]
    public async Task A_failed_or_bad_fetch_keeps_the_copy_there_is()
    {
        var plugins = new TestPlugins();
        Directory.CreateDirectory(plugins.Paths.PluginsDir);
        File.WriteAllText(plugins.Paths.PluginModelsDevFile, """{"old":{}}""");
        File.SetLastWriteTimeUtc(plugins.Paths.PluginModelsDevFile, DateTime.UtcNow.AddDays(-2));
        var logs = new List<string>();

        var failed = await new ModelsDevCatalog(plugins.Paths, () => new HttpClient(new Stub(() => new HttpResponseMessage(HttpStatusCode.BadGateway))))
            .EnsureAsync((_, l) => logs.Add(l));
        var bad = await new ModelsDevCatalog(plugins.Paths, () => new HttpClient(new Stub(() => Json("[1,2]"))))
            .EnsureAsync((_, l) => logs.Add(l));

        Assert.Equal(plugins.Paths.PluginModelsDevFile, failed);
        Assert.Equal(plugins.Paths.PluginModelsDevFile, bad);
        Assert.Equal("""{"old":{}}""", File.ReadAllText(plugins.Paths.PluginModelsDevFile));
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task No_copy_and_no_network_is_none()
    {
        var plugins = new TestPlugins();
        var catalog = new ModelsDevCatalog(plugins.Paths, () => new HttpClient(new Stub(() => throw new HttpRequestException("offline"))));
        Assert.Null(await catalog.EnsureAsync((_, _) => { }));
    }
}
