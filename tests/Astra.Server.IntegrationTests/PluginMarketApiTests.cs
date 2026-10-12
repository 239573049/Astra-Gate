using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Server.IntegrationTests;

/// <summary>The plugin market end to end: the setting that moves it, and a package the market hosts, installed through the API.</summary>
public class PluginMarketApiTests
{
    private const string Pkg = "@t/middleware-hosted";

    /// <summary>A stand-in for the docs site: a registry that lists one hosted package, and its tarball.</summary>
    private sealed class FakeMarket : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _tarball;

        public FakeMarket(string? minAstraVersion = null)
        {
            _tarball = MakeTarball();
            var port = FreePort();
            Base = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            MinAstraVersion = minAstraVersion;
            _ = Task.Run(Serve);
        }

        public string Base { get; }
        public string RegistryUrl => Base + "/api/plugin-market/registry.json";
        private string? MinAstraVersion { get; }

        private static byte[] MakeTarball()
        {
            using var tar = new MemoryStream();
            using (var writer = new TarWriter(tar, TarEntryFormat.Ustar, leaveOpen: true))
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/package.json")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes($$"""{"name":"{{Pkg}}","version":"1.0.0","magpie":{"middleware":"./mw.js"} }""")),
                });
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "package/mw.js")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("export default { onRequest(body) { return body; } };")),
                });
            }
            tar.Position = 0;
            using var gz = new MemoryStream();
            using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) tar.CopyTo(z);
            return gz.ToArray();
        }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
                var path = ctx.Request.Url!.AbsolutePath;
                byte[] body;
                if (path == "/api/plugin-market/registry.json")
                {
                    var min = MinAstraVersion is null ? "" : $$""","minAstraVersion":"{{MinAstraVersion}}" """;
                    var sha = Convert.ToHexStringLower(SHA256.HashData(_tarball));
                    body = Encoding.UTF8.GetBytes($$"""
                        {"version":2,"plugins":[{"package":"{{Pkg}}","name":"Hosted","kind":"middleware","origin":"astra","latest":"1.0.0" {{min}},
                        "tarballs":[{"version":"1.0.0","url":"/api/plugin-market/packages/x/1.0.0.tgz","sha256":"{{sha}}","size":{{_tarball.Length}} }]}]}
                        """);
                }
                else if (path == "/api/plugin-market/packages/x/1.0.0.tgz")
                {
                    body = _tarball;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    continue;
                }
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Close();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<JsonNode> WaitForJobAsync(TestHost host, JsonNode job)
    {
        var id = job["id"]!.GetValue<string>();
        JsonNode state = job;
        for (var i = 0; i < 100 && state["state"]!.GetValue<string>() == "running"; i++)
        {
            await Task.Delay(50);
            state = await host.GetJsonAsync($"/api/plugins/jobs/{id}");
        }
        return state;
    }

    [Fact]
    public async Task The_market_address_is_a_setting_that_can_be_reset_emptied_and_must_be_http()
    {
        await using var host = await TestHost.StartAsync();

        var defaults = await host.GetJsonAsync("/api/settings");
        Assert.Null(defaults["pluginMarketUrl"]);
        Assert.Equal("https://astra-gate.si/api/plugin-market/registry.json", defaults["pluginMarketDefaultUrl"]!.GetValue<string>());

        var (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = " https://market.example/api/plugin-market/registry.json " });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://market.example/api/plugin-market/registry.json", body!["pluginMarketUrl"]!.GetValue<string>());

        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = "ftp://market.example/r.json" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = "not a url" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("https://market.example/api/plugin-market/registry.json", (await host.GetJsonAsync("/api/settings"))["pluginMarketUrl"]!.GetValue<string>());

        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["pluginMarketUrl"]!.GetValue<string>());
        // emptied: only the copy fetched last and the built-in one, so the list still comes
        Assert.NotEmpty((await host.GetJsonAsync("/api/plugins/market")).AsArray());

        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = (string?)null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["pluginMarketUrl"]);
    }

    [Fact]
    public async Task A_package_the_market_hosts_is_installed_from_the_market_and_listed_as_its_own()
    {
        await using var market = new FakeMarket();
        await using var host = await TestHost.StartAsync();
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = market.RegistryUrl });

        var listings = (await host.GetJsonAsync("/api/plugins/market")).AsArray();
        var listing = listings.Single(l => l!["package"]!.GetValue<string>() == Pkg)!;
        Assert.Equal("astra", listing["origin"]!.GetValue<string>());
        Assert.Equal($"{market.Base}/api/plugin-market/packages/x/1.0.0.tgz", listing["tarballs"]![0]!["url"]!.GetValue<string>());

        // the CLI sends no source and the console sends "market": both find the hosted package
        var (status, job) = await host.SendAsync(HttpMethod.Post, "/api/plugins", new { spec = Pkg, source = "market" });
        Assert.Equal(HttpStatusCode.OK, status);
        var done = await WaitForJobAsync(host, job!);
        Assert.True(done["state"]!.GetValue<string>() == "succeeded", done.ToJsonString());

        var plugin = (await host.GetJsonAsync("/api/plugins"))["plugins"]!.AsArray().Single()!;
        Assert.Equal($"{Pkg}@latest", plugin["spec"]!.GetValue<string>());
        Assert.Equal("market-package", plugin["source"]!.GetValue<string>());
        Assert.Equal("1.0.0", plugin["version"]!.GetValue<string>());
        Assert.Contains("middleware", plugin["kinds"]!.ToJsonString());

        (_, job) = await host.SendAsync(HttpMethod.Delete, $"/api/plugins?spec={Uri.EscapeDataString($"{Pkg}@latest")}");
        Assert.Equal("succeeded", (await WaitForJobAsync(host, job!))["state"]!.GetValue<string>());
        Assert.Empty((await host.GetJsonAsync("/api/plugins"))["plugins"]!.AsArray());
    }

    [Fact]
    public async Task A_plugin_that_needs_a_newer_astra_is_not_installed()
    {
        await using var market = new FakeMarket(minAstraVersion: "99.0.0");
        await using var host = await TestHost.StartAsync();
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { pluginMarketUrl = market.RegistryUrl });

        var (_, job) = await host.SendAsync(HttpMethod.Post, "/api/plugins", new { spec = Pkg });
        var done = await WaitForJobAsync(host, job!);

        Assert.Equal("failed", done["state"]!.GetValue<string>());
        Assert.Contains("needs Astra 99.0.0", done["error"]!.GetValue<string>());
        Assert.Empty((await host.GetJsonAsync("/api/plugins"))["plugins"]!.AsArray());
    }
}
