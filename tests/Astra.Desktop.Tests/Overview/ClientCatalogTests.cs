using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

public sealed class ClientCatalogTests
{
    private static ClientInfoDto Client(string kind, bool detected = false, bool enabled = false, bool installProbe = false) => new(
        kind, kind, Astra.Core.ApiProtocol.OpenAIChat, "ready", null, "mode",
        new ClientDetectionDto(detected, false, null, []), "status", enabled, null, null, null, new JsonObject(), null, [], false,
        Install: installProbe ? new ClientInstallDto("npm", null, "", true, null, null, null, null, null, false, null, null, null, null, false) : null);

    [Fact]
    public void Installed_means_detected_probed_or_managed()
    {
        Assert.False(ClientCatalog.IsInstalled(Client("codex")));
        Assert.True(ClientCatalog.IsInstalled(Client("codex", detected: true)));
        Assert.True(ClientCatalog.IsInstalled(Client("codex", enabled: true)));
        Assert.True(ClientCatalog.IsInstalled(Client("codex", installProbe: true)));
    }

    [Fact]
    public void The_filter_offers_installed_clients_in_catalog_order_and_keeps_the_selected_one()
    {
        var clients = new[] { Client("zed", detected: true), Client("claude-code", enabled: true), Client("pi"), Client("plugin-x", detected: true) };
        Assert.Equal(["claude-code", "zed"], ClientCatalog.Filterable(clients, null));
        Assert.Equal(["claude-code", "pi", "zed"], ClientCatalog.Filterable(clients, "pi"));
        Assert.Empty(ClientCatalog.Filterable(null, null));
        Assert.Equal(["codex"], ClientCatalog.Filterable(null, "codex"));
    }

    [Fact]
    public void Names_come_from_the_catalog_then_the_reported_name_then_the_plugin_id()
    {
        Assert.Equal("Claude Code", ClientCatalog.Name("claude-code", "ignored"));
        Assert.Equal("My Agent", ClientCatalog.Name("plugin-my", "My Agent"));
        Assert.Equal("my", ClientCatalog.Name("plugin-my"));
        Assert.Equal("weird", ClientCatalog.Name("weird"));
        Assert.True(ClientCatalog.Known("zed"));
        Assert.False(ClientCatalog.Known("plugin-my"));
        Assert.Equal(28, ClientCatalog.Order.Count);
    }
}
