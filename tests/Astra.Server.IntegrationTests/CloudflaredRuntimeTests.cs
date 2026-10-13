using System.Net;
using Astra.Core;
using Astra.Server.Tunnel;

namespace Astra.Server.IntegrationTests;

/// <summary>cloudflared's download sources: the order they are tried in, and handing over when one fails or stalls.</summary>
public sealed class CloudflaredRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-cf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Answers per host: "fail" = 404, "stall" = headers then a body that never sends, else that text.</summary>
    private sealed class FakeSources(Dictionary<string, string> byHost) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return byHost.GetValueOrDefault(request.RequestUri!.Host) switch
            {
                "stall" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NeverStream()) }),
                null or "fail" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
                var text => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) }),
            };
        }
    }

    private sealed class NeverStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private CloudflaredRuntime Runtime(FakeSources handler, TunnelSettings settings) =>
        new(new AstraPaths(_root), () => new HttpClient(handler, disposeHandler: false), _ => null, () => settings,
            githubBase: "https://github.test/releases/download", mirrorBase: "https://mirror.test/api/tools/cloudflared")
        {
            StallTimeout = TimeSpan.FromMilliseconds(300),
        };

    [Fact]
    public async Task Auto_tries_GitHub_then_the_mirror_and_reports_both()
    {
        var handler = new FakeSources(new() { ["github.test"] = "stall", ["mirror.test"] = "not the real file" });
        var runtime = Runtime(handler, new TunnelSettings());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.EnsureAsync());

        Assert.Equal(2, handler.Requested.Count);
        Assert.StartsWith("https://github.test/releases/download/" + CloudflaredRuntime.Version + "/", handler.Requested[0]);
        Assert.StartsWith("https://mirror.test/api/tools/cloudflared/" + CloudflaredRuntime.Version + "/", handler.Requested[1]);
        Assert.Contains("github.test", error.Message);
        Assert.Contains("no data", error.Message);
        Assert.Contains("checksum", error.Message);
        Assert.False(runtime.Status.Downloading);
        Assert.NotNull(runtime.Status.Error);
    }

    [Theory]
    [InlineData(TunnelSettings.SourceGitHub, "github.test")]
    [InlineData(TunnelSettings.SourceAstra, "mirror.test")]
    public async Task A_single_source_is_the_only_one_asked(string source, string host)
    {
        var handler = new FakeSources([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(handler, new TunnelSettings { DownloadSource = source }).EnsureAsync());
        Assert.Equal(host, new Uri(Assert.Single(handler.Requested)).Host);
    }

    [Fact]
    public async Task A_custom_source_uses_the_release_layout_under_its_base()
    {
        var handler = new FakeSources([]);
        var settings = new TunnelSettings { DownloadSource = TunnelSettings.SourceCustom, DownloadBaseUrl = "https://ghproxy.test/https://github.com/x" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(handler, settings).EnsureAsync());
        Assert.StartsWith($"https://ghproxy.test/https://github.com/x/{CloudflaredRuntime.Version}/cloudflared-", Assert.Single(handler.Requested));
    }

    [Fact]
    public void An_installed_cloudflared_wins_over_downloading_once_chosen()
    {
        var installed = Path.Combine(_root, "bin", "cloudflared");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "");
        var runtime = Runtime(new FakeSources([]), new TunnelSettings { CloudflaredPath = installed });

        Assert.Equal(installed, runtime.Current);
        Assert.True(runtime.Status.Installed);
        Assert.Null(Runtime(new FakeSources([]), new TunnelSettings { CloudflaredPath = installed + "-gone" }).Current);
    }
}
