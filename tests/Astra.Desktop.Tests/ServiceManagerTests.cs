using System.Net;
using System.Net.Sockets;
using Astra.Core;
using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public sealed class ServiceManagerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "astra-desktop-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpListener _http = new();
    private readonly int _port;

    public ServiceManagerTests()
    {
        Directory.CreateDirectory(_home);
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        _http.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _http.Start();
        _ = Serve();
    }

    public void Dispose()
    {
        _http.Close();
        Directory.Delete(_home, true);
    }

    private async Task Serve()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _http.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var body = ctx.Request.Url!.AbsolutePath switch
            {
                "/api/health" => """{"status":"ok"}""",
                "/api/version" => """{"version":"0.11.7","apiVersion":"1.0"}""",
                _ => null,
            };
            ctx.Response.StatusCode = body is null ? 404 : 200;
            if (body is not null) await ctx.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
            ctx.Response.Close();
        }
    }

    private ServiceManager Manager(string? env = null)
        => new(new ServiceManagerOptions { Paths = new AstraPaths(_home), GetEnvironmentVariable = _ => env, Log = _ => { } });

    [Fact]
    public async Task Probe_follows_the_runtime_port_and_reads_the_version()
    {
        File.WriteAllText(Path.Combine(_home, "runtime.json"), $$"""{"pid":{{Environment.ProcessId}},"port":{{_port}}}""");
        var p = await Manager().ProbeAsync();
        Assert.True(p.Running);
        Assert.Equal(_port, p.Port);
        Assert.False(p.RuntimeStale);
        Assert.Equal("0.11.7", p.Version);
        Assert.Equal("1.0", p.ApiVersion);
    }

    [Fact]
    public async Task A_stale_runtime_file_sends_the_probe_to_the_config_port()
    {
        File.WriteAllText(Path.Combine(_home, "runtime.json"), """{"pid":2147483000,"port":1}""");
        File.WriteAllText(Path.Combine(_home, "config.json"), $$"""{"port":{{_port}}}""");
        var p = await Manager().ProbeAsync();
        Assert.True(p.RuntimeStale);
        Assert.Equal(_port, p.Port);
        Assert.True(p.Running);
    }

    [Fact]
    public async Task A_matching_running_server_is_adopted_without_spawning()
    {
        File.WriteAllText(Path.Combine(_home, "runtime.json"), $$"""{"pid":{{Environment.ProcessId}},"port":{{_port}}}""");
        var fakeBinary = Path.Combine(_home, "astra-server");
        File.WriteAllText(fakeBinary, "");
        File.WriteAllText(Path.Combine(_home, "install.json"), $$"""{"serverPath":"{{fakeBinary}}","serverVersion":"0.11.7"}""");
        var m = Manager();
        var s = await m.EnsureRunningAsync();
        Assert.Equal(_port, s.Port);
        Assert.False(s.WeStarted);
        Assert.False(s.ApiVersionMismatch);
    }

    [Fact]
    public async Task A_server_handed_over_by_the_previous_run_is_adopted_as_ours()
    {
        // The app restarted itself into the tray: the predecessor spawned this server and passed its pid, so Quit must still stop it.
        File.WriteAllText(Path.Combine(_home, "runtime.json"), $$"""{"pid":{{Environment.ProcessId}},"port":{{_port}}}""");
        var fakeBinary = Path.Combine(_home, "astra-server");
        File.WriteAllText(fakeBinary, "");
        File.WriteAllText(Path.Combine(_home, "install.json"), $$"""{"serverPath":"{{fakeBinary}}","serverVersion":"0.11.7"}""");
        var m = Manager();
        m.InheritedServerPid = Environment.ProcessId;
        var s = await m.EnsureRunningAsync();
        Assert.True(s.WeStarted);
        Assert.True(m.WeStarted);
        Assert.Equal(Environment.ProcessId, m.OwnedServerPid);
    }

    [Fact]
    public async Task A_handed_over_pid_that_is_not_the_running_server_is_not_adopted_as_ours()
    {
        // Another server took the port meanwhile (an `astra serve`): the hand-over was for a server that is gone, not this one.
        File.WriteAllText(Path.Combine(_home, "runtime.json"), $$"""{"pid":{{Environment.ProcessId}},"port":{{_port}}}""");
        var fakeBinary = Path.Combine(_home, "astra-server");
        File.WriteAllText(fakeBinary, "");
        File.WriteAllText(Path.Combine(_home, "install.json"), $$"""{"serverPath":"{{fakeBinary}}","serverVersion":"0.11.7"}""");
        var m = Manager();
        m.InheritedServerPid = Environment.ProcessId + 1;
        var s = await m.EnsureRunningAsync();
        Assert.False(s.WeStarted);
        Assert.Null(m.OwnedServerPid);
    }

    [Fact]
    public async Task Nothing_running_and_no_binary_is_a_startup_error_naming_the_log()
    {
        File.WriteAllText(Path.Combine(_home, "config.json"), """{"port":1}""");
        var e = await Assert.ThrowsAsync<StartupException>(() => Manager().EnsureRunningAsync());
        Assert.Contains("binary not found", e.Message);
        Assert.StartsWith(Path.Combine(_home, "logs"), e.LogPath);
    }
}
