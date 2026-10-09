using System.Net;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Client versions, update checks and installs. Installs run a fake <c>npm</c> script from a temp bin directory
/// (the only PATH entry of the test's client environment), so nothing is installed on the machine.
/// </summary>
public class ClientInstallApiTests
{
    private static async Task<TestServer> RegistryAsync(string version)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGet("/{**package}", (string package) => package.EndsWith("/latest", StringComparison.Ordinal)
            ? Results.Json(new { name = package[..^"/latest".Length], version })
            : Results.NotFound());
        await app.StartAsync();
        return app.GetTestServer();
    }

    private static void UseRegistry(WebApplicationBuilder b, TestServer registry) =>
        b.Services.Configure<HttpClientFactoryOptions>(ClientInstallService.HttpClientName,
            o => o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = registry.CreateHandler()));

    /// <summary>
    /// A client environment whose PATH is a single temp directory holding a fake npm: <c>root -g</c> prints the
    /// fake global root, <c>install</c> lays out @openai/codex printing <paramref name="installedVersion"/> (or
    /// fails with EACCES when <paramref name="failInstall"/>).
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static (ClientEnvironment Env, string Bin) FakeNpm(string root, string installedVersion, bool failInstall = false)
    {
        var bin = Path.Combine(root, "tools-bin");
        var prefix = Path.Combine(root, "npm-prefix");
        Directory.CreateDirectory(bin);
        var package = Path.Combine(prefix, "lib", "node_modules", "@openai", "codex");
        // The test PATH is only the fake bin directory, so coreutils are called by absolute path.
        var install = failInstall
            ? "echo 'npm ERR! code EACCES' >&2; exit 243"
            : $"""
              /bin/mkdir -p "{package}/bin" "{prefix}/bin"
              printf '#!/bin/sh\necho "codex-cli {installedVersion}"\n' > "{package}/bin/codex.js"
              /bin/chmod +x "{package}/bin/codex.js"
              /bin/ln -sf "{package}/bin/codex.js" "{prefix}/bin/codex"
              echo "added 1 package"
              """;
        var npm = Path.Combine(bin, "npm");
        File.WriteAllText(npm, $"""
            #!/bin/sh
            set -e
            if [ "$1" = "root" ]; then echo "{prefix}/lib/node_modules"; exit 0; fi
            if [ "$1" = "install" ]; then
            echo "npm $*"
            {install}
            exit 0
            fi
            exit 1
            """);
        File.SetUnixFileMode(npm, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var env = new ClientEnvironment(Path.Combine(root, "client-home"), OperatingSystem.IsMacOS() ? "osx" : "linux",
            name => name == "PATH" ? bin : null);
        return (env, bin);
    }

    private static async Task<JsonNode> WaitForJobAsync(TestHost host, string kind)
    {
        for (var i = 0; i < 100; i++)
        {
            var job = await host.GetJsonAsync($"/api/clients/{kind}/install");
            if (job["state"]!.GetValue<string>() != "running") return job;
            await Task.Delay(100);
        }
        throw new TimeoutException("install job did not finish");
    }

    private static JsonNode Client(JsonNode list, string kind) => list.AsArray().Single(c => c!["kind"]!.GetValue<string>() == kind)!;

    [Fact]
    public async Task Listing_Reports_Install_Methods_Without_Running_Anything()
    {
        await using var host = await TestHost.StartAsync();
        var list = await host.GetJsonAsync("/api/clients");
        var codex = Client(list, "codex")["install"]!;
        Assert.Equal("npm", codex["method"]!.GetValue<string>());
        Assert.Equal("@openai/codex", codex["package"]!.GetValue<string>());
        Assert.False(codex["installed"]!.GetValue<bool>());
        Assert.Equal("npm-missing", codex["blocker"]!.GetValue<string>()); // the test PATH is empty
        Assert.Null(codex["latestVersion"]);
        Assert.Equal("manual", Client(list, "claude-desktop")["install"]!["method"]!.GetValue<string>());
        Assert.Equal("vscode-extension", Client(list, "vscode-copilot")["install"]!["method"]!.GetValue<string>());
        var hermes = Client(list, "hermes-agent")["install"]!;
        Assert.Equal("script", hermes["method"]!.GetValue<string>());
        Assert.Equal("curl -fsSL https://hermes-agent.nousresearch.com/install.sh | bash -s -- --non-interactive",
            hermes["installCommand"]!.GetValue<string>());
    }

    [Fact]
    public async Task Check_Updates_Reads_Latest_Versions_From_The_Registry()
    {
        using var registry = await RegistryAsync("9.9.9");
        await using var host = await TestHost.StartAsync(b => UseRegistry(b, registry));
        var (status, list) = await host.SendAsync(HttpMethod.Post, "/api/clients/check-updates", new { force = true });
        Assert.Equal(HttpStatusCode.OK, status);
        var codex = Client(list!, "codex")["install"]!;
        Assert.Equal("9.9.9", codex["latestVersion"]!.GetValue<string>());
        Assert.NotNull(codex["latestCheckedAt"]);
        Assert.False(codex["updateAvailable"]!.GetValue<bool>()); // not installed
        Assert.Null(Client(list!, "claude-desktop")["install"]!["latestVersion"]); // manual: never checked
    }

    [Fact]
    public async Task Install_Then_Update_Runs_Npm_And_Reports_Versions()
    {
        if (OperatingSystem.IsWindows()) return; // the fake npm is a POSIX shell script
        using var registry = await RegistryAsync("2.0.0");
        var root = Path.Combine(Path.GetTempPath(), "astra-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (env, _) = FakeNpm(root, "1.0.0");
            await using var host = await TestHost.StartAsync(b =>
            {
                b.Services.AddSingleton(env);
                UseRegistry(b, registry);
            });

            var codex = Client(await host.GetJsonAsync("/api/clients"), "codex")["install"]!;
            Assert.False(codex["installed"]!.GetValue<bool>());
            Assert.Equal("npm install -g @openai/codex@latest", codex["installCommand"]!.GetValue<string>());

            var (status, started) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/install", new { action = "install" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("install", started!["action"]!.GetValue<string>());
            var job = await WaitForJobAsync(host, "codex");
            Assert.Equal("succeeded", job["state"]!.GetValue<string>());
            Assert.Equal(0, job["exitCode"]!.GetValue<int>());
            Assert.Contains(job["log"]!.AsArray(), l => l!.GetValue<string>() == "added 1 package");

            var (_, checkedList) = await host.SendAsync(HttpMethod.Post, "/api/clients/check-updates", new { force = true });
            var info = Client(checkedList!, "codex");
            codex = info["install"]!;
            Assert.True(codex["installed"]!.GetValue<bool>(), codex.ToJsonString());
            Assert.Equal("1.0.0", codex["version"]!.GetValue<string>());
            Assert.Equal("1.0.0", info["detection"]!["version"]!.GetValue<string>());
            Assert.True(codex["updateAvailable"]!.GetValue<bool>());
            Assert.Equal("npm", codex["updateVia"]!.GetValue<string>());

            (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/install", new { action = "update" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("succeeded", (await WaitForJobAsync(host, "codex"))["state"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Failed_Install_Reports_Permission_Hint()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "astra-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (env, _) = FakeNpm(root, "1.0.0", failInstall: true);
            await using var host = await TestHost.StartAsync(b => b.Services.AddSingleton(env));
            var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/codex/install", new { action = "install" });
            Assert.Equal(HttpStatusCode.OK, status);
            var job = await WaitForJobAsync(host, "codex");
            Assert.Equal("failed", job["state"]!.GetValue<string>());
            Assert.Equal(243, job["exitCode"]!.GetValue<int>());
            Assert.Equal("permission-denied", job["hint"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_Placeholder_Command_Without_A_Version_Is_Not_An_Installation()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "astra-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (env, bin) = FakeNpm(root, "1.0.0");
            // Like VS Code's "copilot" shim: says the CLI is missing, offers to install it, exits 0.
            var shim = Path.Combine(bin, "copilot");
            File.WriteAllText(shim, "#!/bin/sh\necho 'Cannot find GitHub Copilot CLI (https://docs.github.com/x)'\necho \"Install GitHub Copilot CLI? ['y/N']\"\nexit 0\n");
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using var host = await TestHost.StartAsync(b => b.Services.AddSingleton(env));
            var copilot = Client(await host.GetJsonAsync("/api/clients"), "copilot-cli")["install"]!;
            Assert.False(copilot["installed"]!.GetValue<bool>());
            Assert.Equal("npm install -g @github/copilot@latest", copilot["installCommand"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Read_Only_Npm_Directories_Report_That_Admin_Rights_Are_Needed()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "astra-install-" + Guid.NewGuid().ToString("N"));
        var nodeModules = Path.Combine(root, "npm-prefix", "lib", "node_modules");
        try
        {
            var (env, _) = FakeNpm(root, "1.0.0");
            await using var host = await TestHost.StartAsync(b => b.Services.AddSingleton(env));
            var codex = Client(await host.GetJsonAsync("/api/clients"), "codex")["install"]!;
            Assert.False(codex["needsAdmin"]?.GetValue<bool>() ?? false); // nothing exists yet: npm creates it

            // Like a root-owned /usr/local/lib/node_modules.
            Directory.CreateDirectory(nodeModules);
            File.SetUnixFileMode(nodeModules, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            if (CanWrite(nodeModules)) return; // running as root: nothing is read-only
            await using var fresh = await TestHost.StartAsync(b => b.Services.AddSingleton(env)); // no cached probe
            codex = Client(await fresh.GetJsonAsync("/api/clients"), "codex")["install"]!;
            Assert.True(codex["needsAdmin"]!.GetValue<bool>());
            Assert.Equal($"sudo chown -R \"$(whoami)\" '{nodeModules}'", codex["adminFixCommand"]!.GetValue<string>());
        }
        finally
        {
            if (Directory.Exists(nodeModules)) File.SetUnixFileMode(nodeModules, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }

        static bool CanWrite(string dir)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, "probe"), "");
                File.Delete(Path.Combine(dir, "probe"));
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task An_Update_That_Lands_Behind_An_Older_Copy_Is_Reported_As_Shadowed()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "astra-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (env, bin) = FakeNpm(root, "1.0.0");
            var prefixBin = Path.Combine(root, "npm-prefix", "bin");
            // Like ~/.local/bin/claude: first on PATH and reports 1.0.0, while "claude update" puts 2.0.0 into npm's
            // prefix (which the terminal does not search first).
            var claude = Path.Combine(bin, "claude");
            File.WriteAllText(claude, $"""
                #!/bin/sh
                if [ "$1" = "--version" ]; then echo "1.0.0 (Claude Code)"; exit 0; fi
                if [ "$1" = "update" ]; then
                /bin/mkdir -p "{prefixBin}"
                printf '#!/bin/sh\necho "2.0.0 (Claude Code)"\n' > "{prefixBin}/claude"
                /bin/chmod +x "{prefixBin}/claude"
                echo "Successfully updated to 2.0.0"
                exit 0
                fi
                exit 1
                """);
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var registry = await RegistryAsync("2.0.0");
            await using var host = await TestHost.StartAsync(b =>
            {
                b.Services.AddSingleton(env);
                UseRegistry(b, registry);
            });
            var before = Client(await host.GetJsonAsync("/api/clients"), "claude-code")["install"]!;
            Assert.Equal("claude update", before["updateCommand"]!.GetValue<string>());
            Assert.Empty(before["otherCopies"]!.AsArray());

            var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/install", new { action = "update" });
            Assert.Equal(HttpStatusCode.OK, status);
            var job = await WaitForJobAsync(host, "claude-code");
            Assert.Equal("succeeded", job["state"]!.GetValue<string>());
            Assert.Equal("shadowed", job["hint"]!.GetValue<string>());
            Assert.Contains(job["log"]!.AsArray(), l => l!.GetValue<string>().Contains(Path.Combine(prefixBin, "claude")));

            var after = Client(await host.GetJsonAsync("/api/clients"), "claude-code")["install"]!;
            Assert.Equal("1.0.0", after["version"]!.GetValue<string>()); // what the terminal runs
            var other = Assert.Single(after["otherCopies"]!.AsArray())!;
            Assert.Equal(Path.Combine(prefixBin, "claude"), other["path"]!.GetValue<string>());
            Assert.Equal("2.0.0", other["version"]!.GetValue<string>());
            Assert.True(other["newer"]!.GetValue<bool>());

            // The latest version is that shadowed copy: another "claude update" would change nothing, so none is offered.
            var (_, list) = await host.SendAsync(HttpMethod.Post, "/api/clients/check-updates", new { force = true });
            var latest = Client(list!, "claude-code")["install"]!;
            Assert.True(latest["updateAvailable"]!.GetValue<bool>());
            Assert.Null(latest["updateCommand"]);
            Assert.Equal("shadowed", latest["blocker"]!.GetValue<string>());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task Invalid_Requests_Are_Rejected()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/api/clients/codex/install")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/clients/codex/install", new { action = "rm" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, "/api/clients/nope/install", new { action = "install" })).Status);
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/clients/claude-desktop/install", new { action = "install" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("claude.ai/download", body!["error"]!.GetValue<string>());
        // No npm on the test PATH.
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/clients/codex/install", new { action = "install" })).Status);
    }
}
