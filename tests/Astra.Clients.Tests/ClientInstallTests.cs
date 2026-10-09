using Astra.Clients.Install;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

public class ClientVersionsTests
{
    [Theory]
    [InlineData("codex-cli 0.160.1", "0.160.1")]
    [InlineData("2.1.281 (Claude Code)", "2.1.281")]
    [InlineData("v1.18.35\n", "1.18.35")]
    [InlineData("0.63.0-preview.2", "0.63.0-preview.2")]
    [InlineData("warning: something\nopencode 1.2", "1.2")]
    [InlineData("Cannot find GitHub Copilot CLI (https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli)", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parse_Takes_The_First_Version_Token(string? output, string? expected) =>
        Assert.Equal(expected, ClientVersions.Parse(output));

    [Theory]
    [InlineData("0.161.0", "0.160.1", true)]
    [InlineData("0.160.1", "0.160.1", false)]
    [InlineData("0.99.0", "0.160.1", false)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("1.2.0", "1.2.0-beta.1", true)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.1", true)]
    [InlineData("1.2.0-beta.1", "1.2.0", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("v2.0.0", "1.9.0", true)]
    [InlineData("garbage", "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    [InlineData(null, "1.0.0", false)]
    public void IsNewer_Compares_Semver(string? candidate, string? current, bool expected) =>
        Assert.Equal(expected, ClientVersions.IsNewer(candidate, current));

    [Fact]
    public void VsCode_Extension_Version_Picks_The_Newest_Matching_Entry()
    {
        const string json = """
            [
              { "identifier": { "id": "github.copilot-chat" }, "version": "0.31.0" },
              { "identifier": { "id": "GitHub.copilot-chat" }, "version": "0.32.1" },
              { "identifier": { "id": "ms-python.python" }, "version": "9.9.9" }
            ]
            """;
        Assert.Equal("0.32.1", ClientVersions.VsCodeExtensionVersion(json, "GitHub.copilot-chat"));
        Assert.Null(ClientVersions.VsCodeExtensionVersion(json, "github.copilot"));
        Assert.Null(ClientVersions.VsCodeExtensionVersion("{ not json", "github.copilot-chat"));
        Assert.Null(ClientVersions.VsCodeExtensionVersion(null, "github.copilot-chat"));
    }

    [Fact]
    public void Plist_Short_Version_Is_Read_From_Xml()
    {
        const string plist = """
            <plist version="1.0"><dict>
              <key>CFBundleName</key><string>Claude</string>
              <key>CFBundleShortVersionString</key>
              <string>0.14.10</string>
            </dict></plist>
            """;
        Assert.Equal("0.14.10", ClientVersions.PlistShortVersion(plist));
        Assert.Null(ClientVersions.PlistShortVersion("<plist><dict></dict></plist>"));
    }

    [Fact]
    public void Built_In_Extension_Version_Requires_The_Matching_Id()
    {
        const string package = """{ "publisher": "GitHub", "name": "copilot-chat", "version": "0.69.0" }""";
        Assert.Equal("0.69.0", ClientVersions.PackageJsonVersion(package, "GitHub.copilot-chat"));
        Assert.Equal("0.69.0", ClientVersions.PackageJsonVersion(package, "github.copilot-chat"));
        Assert.Null(ClientVersions.PackageJsonVersion(package, "GitHub.copilot"));
        Assert.Null(ClientVersions.PackageJsonVersion("not json", "GitHub.copilot-chat"));
    }
}

public class ClientInstallPlannerTests
{
    private static readonly ClientInstallSpec Codex = ClientInstallCatalog.Get(ClientKinds.Codex)!;
    private static readonly ClientInstallSpec ClaudeCode = ClientInstallCatalog.Get(ClientKinds.ClaudeCode)!;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "prefix", "lib", "node_modules");
    private static readonly string Npm = Path.Combine(Path.GetTempPath(), "prefix", "bin", "npm");

    [Fact]
    public void Catalog_Lists_Known_Clients_In_Kind_Order()
    {
        var kinds = ClientInstallCatalog.All.Select(s => s.Kind).ToList();
        Assert.Equal(ClientKinds.All.Where(kinds.Contains), kinds);
        Assert.Contains(ClientKinds.Codex, kinds);
        Assert.Contains(ClientKinds.VsCodeCopilot, kinds);
        Assert.Null(ClientInstallCatalog.Get("not-a-client"));
        Assert.All(ClientInstallCatalog.All.Where(s => s.Method == ClientInstallMethod.Npm), s =>
        {
            Assert.False(string.IsNullOrEmpty(s.Package));
            Assert.False(string.IsNullOrEmpty(s.Executable));
        });
    }

    [Fact]
    public void Missing_Npm_Client_Installs_With_Npm()
    {
        var plan = ClientInstallPlanner.Plan(Codex, new ClientToolState(false, NpmPath: Npm, NpmGlobalRoot: Root), "osx");
        Assert.Equal("npm install -g @openai/codex@latest", plan.Install!.Display);
        Assert.Null(plan.Update);
        Assert.Null(plan.Blocker);
    }

    [Fact]
    public void Missing_Client_Without_Npm_Is_Blocked()
    {
        var plan = ClientInstallPlanner.Plan(Codex, new ClientToolState(false), "osx");
        Assert.Null(plan.Install);
        Assert.Equal("npm-missing", plan.Blocker);
    }

    [Fact]
    public void Npm_Managed_Client_Updates_With_Npm()
    {
        var resolved = Path.Combine(Root, "@openai", "codex", "bin", "codex.js");
        var state = new ClientToolState(true, "/usr/local/bin/codex", resolved, Npm, Root, NpmPackagePresent: true);
        Assert.True(ClientInstallPlanner.IsNpmManaged(Codex, state, "osx"));
        var plan = ClientInstallPlanner.Plan(Codex, state, "osx");
        Assert.Null(plan.Install);
        Assert.Equal("npm", plan.UpdateVia);
        Assert.Equal(["install", "-g", "@openai/codex@latest"], plan.Update!.Arguments);
    }

    [Fact]
    public void Package_Name_Prefix_Does_Not_Count_As_Npm_Managed()
    {
        // "@openai/codex-other" must not match the "@openai/codex" package directory.
        var resolved = Path.Combine(Root, "@openai", "codex-other", "bin", "codex.js");
        var state = new ClientToolState(true, "/usr/local/bin/codex", resolved, Npm, Root, NpmPackagePresent: true);
        Assert.False(ClientInstallPlanner.IsNpmManaged(Codex, state, "osx"));
        Assert.Equal("external-install", ClientInstallPlanner.Plan(Codex, state, "osx").Blocker);
    }

    [Fact]
    public void Copy_Under_An_Older_Npm_Prefix_Is_Updated_In_Place()
    {
        // npm's prefix is now ~/.npm-global, but codex was installed under /usr/local before that.
        var oldPrefix = Path.Combine(Path.GetTempPath(), "usr-local");
        var resolved = Path.Combine(oldPrefix, "lib", "node_modules", "@openai", "codex", "bin", "codex.js");
        var state = new ClientToolState(true, Path.Combine(oldPrefix, "bin", "codex"), resolved, Npm, Root, NpmPackagePresent: false);
        Assert.False(ClientInstallPlanner.IsNpmManaged(Codex, state, "osx"));
        Assert.Equal(oldPrefix, ClientInstallPlanner.InstalledNpmPrefix(Codex, state, "osx"));
        var plan = ClientInstallPlanner.Plan(Codex, state, "osx");
        Assert.Equal("npm", plan.UpdateVia);
        Assert.Equal(["install", "-g", "--prefix", oldPrefix, "@openai/codex@latest"], plan.Update!.Arguments);
        Assert.Same(plan.Update, ClientInstallPlanner.NpmCommand(Codex, plan, "update"));
    }

    [Fact]
    public void Other_Package_Manager_Layouts_Are_Not_Treated_As_Npm()
    {
        // bun: ~/.bun/install/global/node_modules/<package>/… (no lib/node_modules)
        var resolved = Path.Combine(Path.GetTempPath(), ".bun", "install", "global", "node_modules", "@openai", "codex", "bin", "codex.js");
        var state = new ClientToolState(true, "/x/bin/codex", resolved, Npm, Root, NpmPackagePresent: false);
        Assert.Null(ClientInstallPlanner.InstalledNpmPrefix(Codex, state, "osx"));
        Assert.Equal("external-install", ClientInstallPlanner.Plan(Codex, state, "osx").Blocker);
    }

    [Fact]
    public void Natively_Installed_Client_Uses_Its_Own_Updater()
    {
        var exe = Path.Combine(Path.GetTempPath(), "home", ".local", "bin", "claude");
        var state = new ClientToolState(true, exe, exe, Npm, Root, NpmPackagePresent: false);
        var plan = ClientInstallPlanner.Plan(ClaudeCode, state, "osx");
        Assert.Equal("self", plan.UpdateVia);
        Assert.Equal(exe, plan.Update!.FileName);
        Assert.Equal(["update"], plan.Update.Arguments);
    }

    [Fact]
    public void VsCode_Extension_Installs_With_Code_And_Never_Updates()
    {
        var spec = ClientInstallCatalog.Get(ClientKinds.VsCodeCopilot)!;
        var missing = ClientInstallPlanner.Plan(spec, new ClientToolState(false, "/usr/local/bin/code"), "osx");
        Assert.Equal("code --install-extension GitHub.copilot-chat", missing.Install!.Display);
        Assert.Equal("vscode-missing", ClientInstallPlanner.Plan(spec, new ClientToolState(false), "osx").Blocker);
        Assert.Equal(ClientInstallPlan.None, ClientInstallPlanner.Plan(spec, new ClientToolState(true, "/usr/local/bin/code"), "osx"));
    }

    [Theory]
    [InlineData(ClientKinds.Crush, "@charmland/crush")]
    [InlineData(ClientKinds.KimiCode, "@moonshot-ai/kimi-code")]
    [InlineData(ClientKinds.QwenCode, "@qwen-code/qwen-code")]
    [InlineData(ClientKinds.Droid, "droid")]
    [InlineData(ClientKinds.MiMoCode, "@mimo-ai/cli")]
    public void New_Npm_Clients_Install_And_Update_With_Npm_Whether_Scoped_Or_Not(string kind, string package)
    {
        var spec = ClientInstallCatalog.Get(kind)!;
        Assert.Equal(package, spec.Package);
        Assert.Equal($"npm install -g {package}@latest",
            ClientInstallPlanner.Plan(spec, new ClientToolState(false, NpmPath: Npm, NpmGlobalRoot: Root), "osx").Install!.Display);
        Assert.Equal("npm-missing", ClientInstallPlanner.Plan(spec, new ClientToolState(false), "osx").Blocker);

        var resolved = Path.Combine(Root, package.Replace('/', Path.DirectorySeparatorChar), "bin", "cli.js");
        var managed = new ClientToolState(true, "/usr/local/bin/x", resolved, Npm, Root, NpmPackagePresent: true);
        Assert.True(ClientInstallPlanner.IsNpmManaged(spec, managed, "osx"));
        Assert.Equal("npm", ClientInstallPlanner.Plan(spec, managed, "osx").UpdateVia);

        var elsewhere = new ClientToolState(true, "/opt/x/bin/x", "/opt/x/bin/x", Npm, Root, NpmPackagePresent: false);
        Assert.False(ClientInstallPlanner.IsNpmManaged(spec, elsewhere, "osx"));
        var plan = ClientInstallPlanner.Plan(spec, elsewhere, "osx");
        if (spec.SelfUpdateArgs is null) Assert.Equal("external-install", plan.Blocker);
        else Assert.Equal("self", plan.UpdateVia);
    }

    [Theory]
    [InlineData(ClientKinds.Zed, "zed")]
    [InlineData(ClientKinds.VsCodeInsiders, "code-insiders")]
    [InlineData(ClientKinds.VsCodium, "codium")]
    [InlineData(ClientKinds.Omp, "omp")]
    [InlineData(ClientKinds.DeepSeekHarness, "dsh")]
    [InlineData(ClientKinds.WorkBuddy, null)]
    public void New_Manual_Clients_Only_Show_Version_And_Homepage(string kind, string? executable)
    {
        var spec = ClientInstallCatalog.Get(kind)!;
        Assert.Equal(ClientInstallMethod.Manual, spec.Method);
        Assert.Equal(executable, spec.Executable);
        Assert.StartsWith("https://", spec.HomepageUrl);
        Assert.Equal(ClientInstallPlan.None, ClientInstallPlanner.Plan(spec, new ClientToolState(false, NpmPath: Npm, NpmGlobalRoot: Root), "osx"));
        Assert.Equal(ClientInstallPlan.None, ClientInstallPlanner.Plan(spec, new ClientToolState(true, "/usr/local/bin/x", NpmPath: Npm), "osx"));
    }

    [Fact]
    public void Manual_Clients_Have_No_Commands()
    {
        var spec = ClientInstallCatalog.Get(ClientKinds.ClaudeDesktop)!;
        Assert.Equal(ClientInstallPlan.None, ClientInstallPlanner.Plan(spec, new ClientToolState(false), "osx"));
    }
}

public class ClientEnvironmentToolPathTests : IDisposable
{
    private readonly TestHome _home = new();

    [Fact]
    public void FindTool_Searches_Path_Then_Extra_Directories()
    {
        var onPath = _home.File("path-bin");
        var extra = _home.File("extra-bin");
        _home.WriteFile(Path.Combine(extra, "codex"), "#!/bin/sh\n");
        _home.Variables["PATH"] = onPath;
        Assert.Null(_home.Env.FindOnPath("codex"));
        Assert.Equal(Path.Combine(extra, "codex"), _home.Env.FindTool("codex", extra));

        _home.WriteFile(Path.Combine(onPath, "codex"), "#!/bin/sh\n");
        Assert.Equal(Path.Combine(onPath, "codex"), _home.Env.FindTool("codex", extra));
        Assert.Equal([onPath, extra], _home.Env.ToolSearchPath(extra, onPath));
    }

    [Fact]
    public void Login_Shell_Path_Is_Read_Between_Markers_Ignoring_Rc_Noise()
    {
        const string output = "Welcome!\n[oh-my-zsh] update available\n__ASTRA_PATH_BEGIN__/Users/u/.local/bin:/usr/local/bin::relative/bin:/usr/bin__ASTRA_PATH_END__";
        Assert.Equal(["/Users/u/.local/bin", "/usr/local/bin", "/usr/bin"], Astra.Clients.Config.ClientEnvironment.ParseMarkedPath(output));
        Assert.Empty(Astra.Clients.Config.ClientEnvironment.ParseMarkedPath("no markers here"));
        Assert.Empty(Astra.Clients.Config.ClientEnvironment.ParseMarkedPath("__ASTRA_PATH_BEGIN__/usr/bin"));
    }

    [Fact]
    public void Tests_Never_Read_The_Real_Login_Shell() =>
        // TestHome's environment is not the real one: no shell is spawned and the tool path is PATH only.
        Assert.Empty(_home.Env.LoginShellPath());

    [Fact]
    public void FindAllTools_Lists_Every_Copy_In_Search_Order()
    {
        var first = _home.File("first-bin");
        var second = _home.File("second-bin");
        _home.WriteFile(Path.Combine(first, "claude"), "#!/bin/sh\n");
        _home.WriteFile(Path.Combine(second, "claude"), "#!/bin/sh\n");
        _home.Variables["PATH"] = first;
        Assert.Equal([Path.Combine(first, "claude"), Path.Combine(second, "claude")], _home.Env.FindAllTools("claude", second));
    }

    public void Dispose() => _home.Dispose();
}

public class ClientElevationTests
{
    private static readonly ToolCommand Npm = new("/usr/local/bin/npm", ["install", "-g", "@openai/codex@latest"]);
    private static readonly KeyValuePair<string, string>[] Environment =
    [
        new("PATH", "/usr/local/bin:/usr/bin"),
        new("npm_config_cache", ClientElevation.RootNpmCache),
    ];

    [Fact]
    public void MacOs_Runs_Npm_Through_Osascript_With_Administrator_Privileges()
    {
        var wrapped = ClientElevation.Wrap(Npm, "osx", "/usr/bin/osascript", Environment, "Astra needs \"admin\"")!;
        Assert.Equal("/usr/bin/osascript", wrapped.FileName);
        Assert.Equal("-e", wrapped.Arguments[0]);
        Assert.Equal(
            """do shell script "export PATH='/usr/local/bin:/usr/bin' npm_config_cache='/tmp/astra-npm-root-cache'; '/usr/local/bin/npm' 'install' '-g' '@openai/codex@latest' 2>&1" with prompt "Astra needs \"admin\"" with administrator privileges without altering line endings""",
            wrapped.Arguments[1]);
        Assert.Equal("sudo npm install -g @openai/codex@latest", wrapped.Display);
    }

    [Fact]
    public void Linux_Runs_Npm_Through_Pkexec_And_Env()
    {
        var wrapped = ClientElevation.Wrap(Npm, "linux", "/usr/bin/pkexec", Environment, "prompt")!;
        Assert.Equal("/usr/bin/pkexec", wrapped.FileName);
        Assert.Equal(
            ["/usr/bin/env", "PATH=/usr/local/bin:/usr/bin", "npm_config_cache=/tmp/astra-npm-root-cache", "/usr/local/bin/npm", "install", "-g", "@openai/codex@latest"],
            wrapped.Arguments);
    }

    [Fact]
    public void No_Elevation_Without_An_Elevator_Or_On_Windows()
    {
        Assert.Null(ClientElevation.Wrap(Npm, "osx", null, Environment, "prompt"));
        Assert.Null(ClientElevation.Wrap(Npm, "windows", @"C:\elevate.exe", Environment, "prompt"));
    }

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", @"'it'\''s'")]
    [InlineData("/path with space/$HOME", "'/path with space/$HOME'")]
    public void Shell_Quoting_Keeps_Values_Literal(string value, string expected) =>
        Assert.Equal(expected, ClientElevation.ShellQuote(value));

    [Fact]
    public void AppleScript_Strings_Escape_Quotes_And_Backslashes() =>
        Assert.Equal("\"a \\\"b\\\" c\\\\d\"", ClientElevation.AppleScriptString("a \"b\" c\\d"));

    [Fact]
    public void Write_Targets_Cover_Root_Scope_Package_And_Bin()
    {
        var root = Path.Combine("/usr", "local", "lib", "node_modules");
        Assert.Equal(
            [root, Path.Combine(root, "@openai"), Path.Combine(root, "@openai", "codex"), "/usr/local/bin"],
            ClientElevation.NpmWriteTargets(ClientInstallCatalog.Get(ClientKinds.Codex)!, root, "/usr/local/bin"));
        Assert.Equal(
            [root, Path.Combine(root, "opencode-ai")],
            ClientElevation.NpmWriteTargets(ClientInstallCatalog.Get(ClientKinds.OpenCode)!, root, null));
    }

    [Fact]
    public void Only_Npm_Commands_Can_Be_Elevated()
    {
        var codex = ClientInstallCatalog.Get(ClientKinds.Codex)!;
        var claude = ClientInstallCatalog.Get(ClientKinds.ClaudeCode)!;
        var npmUpdate = new ClientInstallPlan(null, Npm, "npm", null);
        var selfUpdate = new ClientInstallPlan(null, new ToolCommand("/home/u/.local/bin/claude", ["update"]), "self", null);
        Assert.Same(Npm, ClientInstallPlanner.NpmCommand(codex, npmUpdate, "update"));
        Assert.Null(ClientInstallPlanner.NpmCommand(claude, selfUpdate, "update"));
        Assert.Same(Npm, ClientInstallPlanner.NpmCommand(codex, new ClientInstallPlan(Npm, null, null, null), "install"));
        var vscode = ClientInstallCatalog.Get(ClientKinds.VsCodeCopilot)!;
        Assert.Null(ClientInstallPlanner.NpmCommand(vscode, new ClientInstallPlan(new ToolCommand("code", ["--install-extension", "x"]), null, null, null), "install"));
    }
}
