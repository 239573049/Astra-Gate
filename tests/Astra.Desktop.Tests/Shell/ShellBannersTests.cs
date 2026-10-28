using System.Text.Json;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Shell;

public class ShellBannersTests
{
    private static ClientInfoDto Client(string kind, string name, bool outdated) => JsonSerializer.Deserialize(
        $$"""
        {"kind":"{{kind}}","name":"{{name}}","protocol":"anthropic","availability":"available","availabilityReason":null,"mode":"switch",
         "detection":{"installed":true,"configExists":true,"version":"1","configPaths":[]},"status":"enabled","enabled":true,
         "providerId":null,"accountId":null,"selectedModel":null,"extras":{},"appliedAt":null,"warnings":[],"requiresRestart":false,
         "configOutdated":{{(outdated ? "true" : "false")}}}
        """, JsonContexts.Info<ClientInfoDto>(Json.Api))!;

    [Fact]
    public void Outdated_clients_are_listed_only_while_the_server_is_up()
    {
        List<ClientInfoDto> clients = [Client("codex", "Codex", true), Client("claude-code", "Claude Code", false), Client("zed", "Zed", true)];
        Assert.Equal(["codex", "zed"], ShellBanners.OutdatedKinds(clients, down: false));
        Assert.Empty(ShellBanners.OutdatedKinds(clients, down: true));
        Assert.Empty(ShellBanners.OutdatedKinds(null, down: false));
    }

    [Fact]
    public void Client_names_come_from_the_list_else_the_kind()
    {
        List<ClientInfoDto> clients = [Client("codex", "Codex", true), Client("plugin-x", "X Agent", true)];
        Assert.Equal("Codex, X Agent, gone", ShellBanners.ClientNames(clients, ["codex", "plugin-x", "gone"]));
        Assert.Equal("a", ShellBanners.ClientNames(null, ["a"]));
    }

    [Fact]
    public void Tooltips_follow_the_web()
    {
        Assert.Equal("收起侧边栏 (⌘⌥S)", ShellBanners.ToggleTooltip("收起侧边栏", mac: true));
        Assert.Equal("Collapse sidebar (Ctrl+Alt+S)", ShellBanners.ToggleTooltip("Collapse sidebar", mac: false));
        Assert.Equal("服务运行中 · 远程访问已开启", ShellBanners.RailTooltip("服务运行中", true, "远程访问已开启"));
        Assert.Equal("服务运行中", ShellBanners.RailTooltip("服务运行中", false, "远程访问已开启"));
        Assert.False(ShellBanners.RemoteOn(false, null));
    }

    [Fact]
    public void Client_meta_matches_the_web_table()
    {
        Assert.Equal(28, ClientMeta.Names.Count);
        Assert.Equal(ClientMeta.Names.Keys.Order(StringComparer.Ordinal), ClientMeta.Order.Order(StringComparer.Ordinal));
        Assert.Equal("Claude Code", ClientMeta.Name("claude-code"));
        Assert.Equal("omp (oh-my-pi)", ClientMeta.Name("omp", "ignored"));
        Assert.Equal("My Agent", ClientMeta.Name("plugin-mine", "My Agent"));
        Assert.Equal("mine", ClientMeta.Name("plugin-mine"));
        Assert.Equal("mine", ClientMeta.Name("plugin-mine", ""));
        Assert.Equal("weird", ClientMeta.Name("weird"));
        Assert.Equal(0, ClientMeta.OrderOf("codex"));
        Assert.Equal(ClientMeta.Order.Count, ClientMeta.OrderOf("plugin-mine"));
    }
}

public class AccentMathTests
{
    [Theory]
    [InlineData("#007aff", "#ffffff")] // the default blue stays white (Arc's contrast rule would make it black)
    [InlineData("#8c8c8c", "#ffffff")]
    [InlineData("#ffc600", "#1d1d1f")] // the yellow preset
    [InlineData("#62ba46", "#ffffff")]
    [InlineData("nonsense", "#ffffff")]
    public void On_accent_follows_the_web(string accent, string expected) => Assert.Equal(expected, AccentMath.OnAccent(accent));
}
