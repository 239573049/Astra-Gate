using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;

namespace Astra.Clients.Tests;

public class MuseCodeClientAdapterTests : AdapterTestBase
{
    private const string Original = "export PATH=\"$HOME/bin:$PATH\"\nalias ll='ls -l'\n";

    private readonly MuseCodeClientAdapter _adapter;
    private readonly string _zshrc;

    public MuseCodeClientAdapterTests()
    {
        _adapter = new MuseCodeClientAdapter(Home.Env, Store);
        _zshrc = Home.File(".zshrc");
    }

    [Fact]
    public void Enable_WritesManagedBlock_DisableRestoresBytes()
    {
        Home.WriteFile(_zshrc, Original);
        var original = Home.ReadFileBytes(_zshrc);

        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-1", "muse-spark-1.3")));

        var text = Home.ReadFile(_zshrc);
        Assert.StartsWith(Original, text);
        Assert.Contains("# >>> astra muse-code >>>", text);
        Assert.Contains("muse() {", text);
        Assert.Contains("      META_API_KEY='astra-muse-code-1' MUSE_MODEL='muse-spark-1.3' command muse exec --base-url 'http://127.0.0.1:17321/v1' \"$@\"", text);
        Assert.Contains("      META_API_KEY='astra-muse-code-1' MUSE_MODEL='muse-spark-1.3' command muse --base-url 'http://127.0.0.1:17321/v1' \"$@\"", text);
        Assert.Contains("    resume|export|trace|skills|sandbox|session-message|auth|login|logout|init|config|help)", text);
        Assert.DoesNotContain("export META_API_KEY", text);
        Assert.DoesNotContain("alias muse", text);
        Assert.True(_adapter.Inspect().Enabled);

        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(original, Home.ReadFileBytes(_zshrc));
        Assert.False(_adapter.Inspect().Enabled);
    }

    [Fact]
    public void Enable_WithoutModel_DoesNotExportModel()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-2", null)));
        var text = Home.ReadFile(_zshrc);
        Assert.Contains("META_API_KEY='astra-muse-code-2' command muse exec", text);
        Assert.DoesNotContain("MUSE_MODEL", text);
    }

    [Fact]
    public void Enable_RefreshesKeyAndModel_InPlace()
    {
        Home.WriteFile(_zshrc, Original);
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-3", "muse-spark-1.2")));
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-4", "muse-spark-1.3")));

        var text = Home.ReadFile(_zshrc);
        Assert.Equal(1, text.Split("# >>> astra muse-code >>>").Length - 1);
        Assert.DoesNotContain("astra-muse-code-3", text);
        Assert.Contains("META_API_KEY='astra-muse-code-4' MUSE_MODEL='muse-spark-1.3' command muse exec", text);
        Assert.DoesNotContain("muse-spark-1.2", text);
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Equal(Original, Home.ReadFile(_zshrc));
    }

    [Fact]
    public void FileOriginallyAbsent_CreatedThenDeleted()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-5", null)));
        Assert.True(Home.FileExists(_zshrc));
        Assert.False(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.False(Home.FileExists(_zshrc));
    }

    [Fact]
    public void BashrcIsUsed_OnlyWhenZshrcIsMissing()
    {
        var bashrc = Home.File(".bashrc");
        Home.WriteFile(bashrc, "# bash\n");
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-6", null)));
        Assert.Contains("# >>> astra muse-code >>>", Home.ReadFile(bashrc));
        Assert.False(Home.FileExists(_zshrc));
        Assert.Equal([bashrc], _adapter.ConfigPaths());
    }

    [Fact]
    public void UserEditInsideBlock_IsDriftAndNotRemoved()
    {
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-7", "muse-spark-1.3")));
        Home.WriteFile(_zshrc, Home.ReadFile(_zshrc).Replace("MUSE_MODEL='muse-spark-1.3'", "MUSE_MODEL='mine'", StringComparison.Ordinal));

        var status = _adapter.Inspect();
        Assert.False(status.Enabled);
        Assert.Contains("muse-code", status.DriftedKeys);
        Assert.True(Applier.Disable(_adapter.PlanDisable()).HasDrift);
        Assert.Contains("MUSE_MODEL='mine'", Home.ReadFile(_zshrc));
    }

    [Fact]
    public void WithoutSelectedModel_PinsTheFirstListedModel()
    {
        var models = new JsonObject
        {
            ["models"] = new JsonObject
            {
                ["deepseek-v4-pro"] = new JsonObject { ["id"] = "deepseek-v4-pro" },
                ["other"] = new JsonObject { ["id"] = "other" },
            },
        };
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-8", null, models)));
        Assert.Contains("MUSE_MODEL='deepseek-v4-pro'", Home.ReadFile(_zshrc));
    }

    [Fact]
    public void SelectedModel_WinsOverTheListedModels()
    {
        var models = new JsonObject
        {
            ["models"] = new JsonObject { ["other"] = new JsonObject { ["id"] = "other" } },
        };
        Applier.Apply(_adapter.PlanEnable(TestGateway.Context("astra-muse-code-9", "deepseek-v4-pro", models)));
        Assert.Contains("MUSE_MODEL='deepseek-v4-pro'", Home.ReadFile(_zshrc));
        Assert.DoesNotContain("MUSE_MODEL='other'", Home.ReadFile(_zshrc));
    }

    [Fact]
    public void Detect_FindsConfigDirectory()
    {
        Directory.CreateDirectory(Home.File(".config", "muse"));
        Assert.True(_adapter.Detect().Detected);
    }
}
