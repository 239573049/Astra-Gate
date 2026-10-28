using System.Net;
using System.Net.Sockets;
using Astra.Core;
using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

/// <summary>
/// Spawns a real astra-server (set ASTRA_TEST_SERVER_BIN to a built binary; skipped otherwise): the start → healthy →
/// shutdown round trip through the shell wrapper that detaches the child and appends its output to the day's log.
/// </summary>
public class RealServerTests
{
    [Fact]
    public async Task Starts_adopts_and_stops_a_real_server()
    {
        var bin = Environment.GetEnvironmentVariable("ASTRA_TEST_SERVER_BIN");
        if (string.IsNullOrEmpty(bin) || !File.Exists(bin)) return;

        var home = Path.Combine(Path.GetTempPath(), "astra-desktop-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        File.WriteAllText(Path.Combine(home, "config.json"), $$"""{"port":{{port}}}""");
        var oldHome = Environment.GetEnvironmentVariable("ASTRA_HOME");
        Environment.SetEnvironmentVariable("ASTRA_HOME", home);
        try
        {
            var manager = new ServiceManager(new ServiceManagerOptions
            {
                Paths = new AstraPaths(home),
                GetEnvironmentVariable = n => n == "ASTRA_SERVER_BIN" ? bin : null,
                Log = _ => { },
            });
            var started = await manager.EnsureRunningAsync();
            Assert.True(started.WeStarted);
            Assert.False(started.ApiVersionMismatch);
            Assert.True(File.Exists(manager.Paths.DailyLogFile(DateTime.Now)));

            // A second manager (another desktop run) adopts it instead of spawning.
            var second = new ServiceManager(new ServiceManagerOptions
            {
                Paths = new AstraPaths(home),
                GetEnvironmentVariable = n => n == "ASTRA_SERVER_BIN" ? bin : null,
                Log = _ => { },
            });
            var adopted = await second.EnsureRunningAsync();
            Assert.False(adopted.WeStarted);
            Assert.Equal(started.Port, adopted.Port);

            await second.StopIfOursAsync();
            Assert.True((await manager.ProbeAsync()).Running); // not ours: left alone

            Assert.Equal(StopResult.Stopped, await manager.StopServiceAsync());
            Assert.False((await manager.ProbeAsync()).Running);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASTRA_HOME", oldHome);
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }
}
