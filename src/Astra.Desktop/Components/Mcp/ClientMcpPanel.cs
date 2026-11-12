using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Mcp;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Mcp;

/// <summary>
/// "Apply to clients" (web <c>ClientMcpEntries</c>, issue #34): picks a token that has MCP servers bound and writes
/// <c>&lt;gateway&gt;/mcp</c> plus that token into Claude Code's or Codex's own MCP settings. The server writes the key; it never comes
/// back. What outlives a redraw (the token each row picked) lives here, not in the controls a redraw replaces.
/// </summary>
public sealed class ClientMcpPanel(ClientMcpApi api, IFeedback feedback, Func<IReadOnlyList<TokenDto>?> tokens)
{
    private static readonly (string Kind, string Name)[] Clients = [("claude-code", "Claude Code"), ("codex", "Codex")];

    private readonly Dictionary<string, string> _picked = [];

    /// <summary>Draws the group from the entries query (and the tokens as they stand).</summary>
    public Control? Render(Query<List<ClientMcpDto>> q)
    {
        if (!q.HasData)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(2);
        var list = tokens();
        // tokens that can reach MCP at all; a token with nothing bound would give the client an empty server list
        var eligible = (list ?? []).Where(t => t.Enabled && (t.McpServers?.Count ?? 0) > 0).Select(t => (t.Id, t.Name)).ToList();
        var rows = new List<Control> { Ui.GroupNote(Loc.T("mcp.clients.hint")) };
        if (list is not null && eligible.Count == 0) rows.Add(Ui.GroupNote(Loc.T("mcp.clients.noToken")));
        foreach (var (kind, name) in Clients)
            if (q.Data!.FirstOrDefault(e => e.Kind == kind) is { } entry) rows.Add(ClientRow(name, entry, eligible));
        return RowBuilder.Group(Loc.T("mcp.clients.title"), Loc.T("mcp.clients.stdio"), [.. rows]);
    }

    private static Tone ToneOf(string state) => state switch
    {
        "applied" => Tone.Green,
        "drifted" => Tone.Orange,
        "conflict" => Tone.Red,
        _ => Tone.Neutral,
    };

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            feedback.Error(Loc.T("common.error"), e.Message);
        }
    }

    private Control ClientRow(string name, ClientMcpDto entry, List<(string Id, string Name)> eligible)
    {
        var tokenId = _picked.TryGetValue(entry.Kind, out var p) && eligible.Any(t => t.Id == p) ? p
            : eligible.Any(t => t.Id == entry.TokenId) ? entry.TokenId : eligible.FirstOrDefault().Id;
        var written = entry.State is "applied" or "drifted";

        var label = ClientUi.HRow(8,
            PdUi.Text(name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold),
            KitBadge.Create(Loc.T("mcp.clients.state." + entry.State), ToneOf(entry.State)));
        var detail = new StackPanel { Spacing = 2 };
        detail.Children.Add(PdUi.Text(entry.File, 11.5, ArcDesignTokenKind.TextMuted, mono: true, wrap: true));
        if (entry.State == "applied" && entry.TokenName is { Length: > 0 } tn)
            detail.Children.Add(PdUi.Text(Loc.T("mcp.clients.usesToken", ("name", tn)), 11.5, ArcDesignTokenKind.TextMuted, wrap: true));
        if (entry.Error is { Length: > 0 } err)
            detail.Children.Add(PdUi.Text(Loc.T("mcp.clients.unreadable", ("error", err)), 11.5, ArcDesignTokenKind.Danger, wrap: true));

        var trailing = new List<Control>();
        if (eligible.Count > 0)
            trailing.Add(PdUi.Choice(eligible.Select(t => (t.Id, t.Name)), tokenId, v => _picked[entry.Kind] = v, 176, $"{name} {Loc.T("mcp.clients.token")}", Loc.T("mcp.clients.pickToken")));
        var write = Ui.Button(Loc.T(written ? "mcp.clients.replace" : "mcp.clients.apply"), () => { }, ButtonVariant.Primary);
        write.IsEnabled = tokenId is not null && entry.State != "conflict";
        write.Click += (_, _) => _ = Write(name, entry, _picked.TryGetValue(entry.Kind, out var cur) ? cur : tokenId, force: false);
        trailing.Add(write);
        if (written)
            trailing.Add(Ui.Button(Loc.T(entry.State == "drifted" ? "mcp.clients.forceRemove" : "mcp.clients.remove"), () => _ = Take(name, entry, entry.State == "drifted"), ButtonVariant.Secondary));

        var block = new StackPanel { Spacing = 0 };
        block.Children.Add(RowBuilder.Row(label, detail, [.. trailing]));
        if (!entry.GatewayEnabled)
            block.Children.Add(Ui.GroupAlert(AlertTone.Warning, Loc.T("mcp.clients.gatewayOff")));
        if (entry.State == "conflict" && entry.Error is null)
            block.Children.Add(Ui.GroupAlert(AlertTone.Warning, Loc.T("mcp.clients.conflict")));
        if (entry.State == "drifted")
        {
            var force = Ui.Button(Loc.T("mcp.clients.force"), () => { }, ButtonVariant.Secondary);
            force.IsEnabled = tokenId is not null;
            force.Click += (_, _) => _ = Write(name, entry, _picked.TryGetValue(entry.Kind, out var cur) ? cur : tokenId, force: true);
            block.Children.Add(Ui.GroupLine(ClientUi.HRow(12, PdUi.Text(Loc.T("mcp.clients.drifted"), 12, ArcDesignTokenKind.TextMuted, wrap: true), force), 4, 10));
        }
        return block;
    }

    private Task Write(string name, ClientMcpDto entry, string? tokenId, bool force) => tokenId is null ? Task.CompletedTask : GuardAsync(async () =>
    {
        await api.ApplyAsync(entry.Kind, tokenId, force);
        feedback.Success(Loc.T("mcp.clients.applied", ("client", name)));
    });

    private Task Take(string name, ClientMcpDto entry, bool force) => GuardAsync(async () =>
    {
        if (force && !await feedback.ConfirmAsync(new ConfirmRequest(Loc.T("mcp.clients.forceRemove"), entry.File, Loc.T("mcp.clients.forceRemove"), Destructive: true))) return;
        await api.RemoveAsync(entry.Kind, force);
        feedback.Success(Loc.T("mcp.clients.removed", ("client", name)));
    });
}
