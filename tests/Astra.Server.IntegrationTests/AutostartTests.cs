using System.Net;
using Astra.Core;
using Astra.Server.Autostart;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Starting at login (<see cref="AutostartService"/>): the entries `astra autostart` writes, in a fake home with recorded
/// commands and an in-memory Run key — no test touches a real LaunchAgent, systemd unit or registry value.
/// </summary>
public sealed class AutostartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-autostart-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _commands = [];
    private readonly MemoryRunKey _runKey = new();

    private string Home => Path.Combine(_root, "home");
    private AstraPaths Paths => new(Path.Combine(_root, ".astra"));
    private string Managed(string version) => Path.Combine(_root, ".astra", "server", $"astra-server-{version}");

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

    private sealed class MemoryRunKey : IAutostartRunKey
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }

    /// <summary>An autostart service on a fake machine; <paramref name="respond"/> answers each command (default: success, no output).</summary>
    private AutostartService Service(string platform, string? exe = null, bool hasSystemd = true, bool inContainer = false,
        Func<string, AutostartCommandResult>? respond = null)
    {
        var host = new AutostartHost
        {
            Platform = platform,
            UserHome = Home,
            Launch = [exe ?? "/opt/astra/astra-server"],
            HasSystemd = hasSystemd,
            InContainer = inContainer,
            RunKey = platform == "windows" ? _runKey : null,
            Run = (program, args, _) =>
            {
                var line = $"{program} {string.Join(' ', args)}";
                _commands.Add(line);
                return Task.FromResult(respond?.Invoke(line) ?? new AutostartCommandResult(0, "", ""));
            },
        };
        return new AutostartService(host, Paths, NullLogger<AutostartService>.Instance);
    }

    [Fact]
    public async Task MacOS_writes_the_launch_agent_without_loading_it()
    {
        var service = Service("darwin");
        var plist = Path.Combine(Home, "Library", "LaunchAgents", "io.astra.server.plist");

        var off = await service.StatusAsync();
        Assert.True(off.Supported);
        Assert.False(off.Enabled);
        Assert.Equal("launchd", off.Mechanism);
        Assert.Equal(plist, off.Location);

        var on = await service.SetAsync(true);
        Assert.True(on.Enabled);
        Assert.Equal("/opt/astra/astra-server", on.Program);
        var text = await File.ReadAllTextAsync(plist);
        Assert.Contains("<string>io.astra.server</string>", text);
        Assert.Contains("<string>/opt/astra/astra-server</string>\n    <string>serve</string>\n    <string>--started-by</string>\n    <string>autostart</string>", text);
        Assert.Contains($"<string>{Path.Combine(Paths.LogsDir, "server-stdout.log")}</string>", text);
        Assert.Contains("<key>RunAtLoad</key>\n  <true/>", text);
        // Loading it now would start a second server; unloading it later could stop this one.
        Assert.Empty(_commands);

        var disabled = await service.SetAsync(false);
        Assert.False(disabled.Enabled);
        Assert.False(File.Exists(plist));
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Linux_enables_the_systemd_user_unit_without_starting_or_stopping_it()
    {
        var enabled = false;
        var service = Service("linux", "/opt/my apps/astra-server", respond: line =>
        {
            if (line.EndsWith(" enable astra.service")) enabled = true;
            if (line.EndsWith(" disable astra.service")) enabled = false;
            return line.Contains("is-enabled") ? new AutostartCommandResult(enabled ? 0 : 1, enabled ? "enabled\n" : "disabled\n", "") : new(0, "", "");
        });
        var unit = Path.Combine(Home, ".config", "systemd", "user", "astra.service");

        var on = await service.SetAsync(true);
        Assert.True(on.Enabled);
        Assert.Equal("systemd", on.Mechanism);
        Assert.Equal(unit, on.Location);
        Assert.Equal("/opt/my apps/astra-server", on.Program);
        var text = await File.ReadAllTextAsync(unit);
        Assert.Contains("ExecStart=\"/opt/my apps/astra-server\" serve --started-by autostart\n", text);
        Assert.Contains("WantedBy=default.target", text);
        Assert.Contains("systemctl --user daemon-reload", _commands);
        Assert.Contains("systemctl --user enable astra.service", _commands);
        Assert.DoesNotContain(_commands, c => c.Contains("--now"));

        var off = await service.SetAsync(false);
        Assert.False(off.Enabled);
        Assert.False(File.Exists(unit));
        Assert.Contains("systemctl --user disable astra.service", _commands);
        Assert.DoesNotContain(_commands, c => c.Contains("--now"));
    }

    [Fact]
    public async Task Linux_reports_a_failed_enable_and_leaves_no_unit_behind()
    {
        var service = Service("linux", respond: line => line.EndsWith(" enable astra.service")
            ? new AutostartCommandResult(1, "", "Failed to connect to bus: No medium found")
            : new AutostartCommandResult(0, "", ""));

        var ex = await Assert.ThrowsAsync<AutostartException>(() => service.SetAsync(true));
        Assert.Contains("Failed to connect to bus", ex.Message);
        Assert.False(File.Exists(Path.Combine(Home, ".config", "systemd", "user", "astra.service")));
    }

    [Theory]
    [InlineData("linux", false, false, "no-systemd")]
    [InlineData("linux", true, true, "container")]
    [InlineData("freebsd", true, false, "platform")]
    public async Task Machines_without_a_login_entry_are_unsupported(string platform, bool hasSystemd, bool inContainer, string reason)
    {
        var service = Service(platform, hasSystemd: hasSystemd, inContainer: inContainer);

        var state = await service.StatusAsync();
        Assert.False(state.Supported);
        Assert.Equal("none", state.Mechanism);
        Assert.Equal(reason, state.Unsupported);
        await Assert.ThrowsAsync<AutostartException>(() => service.SetAsync(true));
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task Windows_uses_the_run_key_and_replaces_the_old_logon_task()
    {
        var taskExists = true;
        var service = Service("windows", @"C:\Program Files\Astra\astra-server.exe", respond: line =>
        {
            if (line.StartsWith("schtasks /Delete")) taskExists = false;
            return line.StartsWith("schtasks /Query") ? new AutostartCommandResult(taskExists ? 0 : 1, "", "") : new(0, "", "");
        });

        // The CLI's scheduled task from before counts as on.
        var legacy = await service.StatusAsync();
        Assert.True(legacy.Enabled);
        Assert.Equal(@"Task Scheduler\Astra Server", legacy.Location);

        var on = await service.SetAsync(true);
        Assert.Equal("\"C:\\Program Files\\Astra\\astra-server.exe\" serve --started-by autostart", _runKey.Values["Astra Server"]);
        Assert.False(taskExists);
        Assert.True(on.Enabled);
        Assert.Equal("registry", on.Mechanism);
        Assert.Equal(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Astra Server", on.Location);
        Assert.Equal(@"C:\Program Files\Astra\astra-server.exe", on.Program);

        var off = await service.SetAsync(false);
        Assert.False(off.Enabled);
        Assert.Empty(_runKey.Values);
    }

    [Fact]
    public async Task Windows_says_so_when_the_old_task_cannot_be_removed()
    {
        // Created from an elevated prompt: a normal user cannot delete it.
        var service = Service("windows", respond: line =>
            line.StartsWith("schtasks /Query") ? new AutostartCommandResult(0, "", "") : new(1, "", "Access is denied."));
        _runKey.Values["Astra Server"] = "\"C:\\astra-server.exe\" serve --started-by autostart";

        var ex = await Assert.ThrowsAsync<AutostartException>(() => service.SetAsync(false));
        Assert.Contains("schtasks /Delete /F /TN \"Astra Server\"", ex.Message);
        Assert.Empty(_runKey.Values);
    }

    [Fact]
    public async Task An_update_moves_the_entry_to_the_running_managed_binary()
    {
        await Service("darwin", Managed("0.9.2")).SetAsync(true);

        var current = Service("darwin", Managed("0.9.3"));
        Assert.True(await current.RefreshAsync(CancellationToken.None));
        Assert.Equal(Managed("0.9.3"), (await current.StatusAsync()).Program);
        Assert.False(await current.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Entries_for_other_installs_are_left_alone()
    {
        // A dev build or a package install that still exists keeps its entry, whatever starts.
        var dev = Path.Combine(_root, "repo", "astra-server");
        Directory.CreateDirectory(Path.GetDirectoryName(dev)!);
        await File.WriteAllTextAsync(dev, "");
        await Service("darwin", dev).SetAsync(true);
        Assert.False(await Service("darwin", Managed("0.9.3")).RefreshAsync(CancellationToken.None));
        Assert.Equal(dev, (await Service("darwin").StatusAsync()).Program);

        // And a server that is not a managed binary never rewrites an entry.
        await Service("darwin", Managed("0.9.2")).SetAsync(true);
        Assert.False(await Service("darwin", "/opt/astra/astra-server").RefreshAsync(CancellationToken.None));
        Assert.Equal(Managed("0.9.2"), (await Service("darwin").StatusAsync()).Program);
    }

    [Fact]
    public void Entry_formats_match_the_cli()
    {
        // cli/test/autostart.test.ts pins the same text for cli/src/lib/autostart.ts.
        string[] argv = ["/opt/astra/bin/astra-server", "serve", "--started-by", "autostart"];
        Assert.Equal("""
            [Unit]
            Description=Astra local AI gateway server
            After=network.target

            [Service]
            Type=simple
            ExecStart=/opt/astra/bin/astra-server serve --started-by autostart
            Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target

            """, AutostartService.RenderSystemdUnit(argv));
        Assert.Contains("<string>/apps &amp; tools/astra-server</string>", AutostartService.RenderLaunchdPlist(["/apps & tools/astra-server"], "/log"));
        Assert.Equal("/apps & tools/astra-server", AutostartService.PlistProgram(AutostartService.RenderLaunchdPlist(["/apps & tools/astra-server", "serve"], "/log")));
        Assert.Equal("/opt/a \"b\"/astra-server", AutostartService.UnitProgram(AutostartService.RenderSystemdUnit(["/opt/a \"b\"/astra-server", "serve"])));
        Assert.Equal(@"C:\astra-server.exe", AutostartService.FirstWindowsArg(@"C:\astra-server.exe serve"));
    }

    [Fact]
    public async Task The_api_turns_the_entry_on_and_off()
    {
        await using var host = await TestHost.StartAsync();
        var plist = Path.Combine(host.ClientHome, "Library", "LaunchAgents", "io.astra.server.plist");

        var off = await host.GetJsonAsync("/api/server/autostart");
        Assert.True(off["supported"]!.GetValue<bool>());
        Assert.False(off["enabled"]!.GetValue<bool>());
        Assert.Equal("launchd", off["mechanism"]!.GetValue<string>());
        Assert.True(off["canChange"]!.GetValue<bool>());

        var (status, on) = await host.SendAsync(HttpMethod.Put, "/api/server/autostart", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(on!["enabled"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(host.Root, "bin", "astra-server"), on["program"]!.GetValue<string>());
        Assert.True(File.Exists(plist));

        var (_, again) = await host.SendAsync(HttpMethod.Put, "/api/server/autostart", new { enabled = false });
        Assert.False(again!["enabled"]!.GetValue<bool>());
        Assert.False(File.Exists(plist));
    }

    [Fact]
    public async Task The_api_refuses_where_there_is_nothing_to_manage()
    {
        await using var host = await TestHost.StartAsync(b => b.Services.AddSingleton(new AutostartHost
        {
            Platform = "linux",
            UserHome = Home,
            Launch = ["/opt/astra/astra-server"],
            InContainer = true,
            Run = (_, _, _) => throw new InvalidOperationException("no commands in a container"),
        }));

        var state = await host.GetJsonAsync("/api/server/autostart");
        Assert.False(state["supported"]!.GetValue<bool>());
        Assert.Equal("container", state["unsupported"]!.GetValue<string>());
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/server/autostart", new { enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, status);
    }
}
