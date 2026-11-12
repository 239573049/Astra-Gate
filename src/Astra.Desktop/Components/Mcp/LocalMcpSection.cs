using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Mcp;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Mcp;

/// <summary>
/// "Your own MCP servers" (web <c>LocalMcpServers</c>, issue #34): a local command (stdio) or a remote address defined once and written into
/// the chosen clients' own MCP lists. Astra never runs or proxies them. Environment and header values stay on the server: rows show names only.
/// </summary>
public sealed class LocalMcpSection(ClientMcpApi api, IFeedback feedback, Func<Visual?> owner)
{
    /// <summary>Draws the section from the servers query.</summary>
    public Control? Render(Query<List<LocalMcpServerDto>> q)
    {
        var add = Ui.Button(Loc.T("mcp.local.add"), () => LocalMcpSheet.Show(owner()!, api, feedback, null), ButtonVariant.Primary, LucideIconKind.Plus);
        var import = Ui.Button(Loc.T("mcp.local.import"), () => LocalMcpImportSheet.Show(owner()!, api, feedback), ButtonVariant.Secondary, LucideIconKind.Download);
        var title = new StackPanel { Spacing = 2, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        title.Children.Add(PdUi.Text(Loc.T("mcp.local.title"), 12, ArcDesignTokenKind.TextSecondary, Avalonia.Media.FontWeight.Medium));
        title.Children.Add(PdUi.Text(Loc.T("mcp.local.hint"), 11.5, ArcDesignTokenKind.TextMuted, wrap: true));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(4, 0, 0, 0) };
        header.Children.Add(title);
        var buttons = ClientUi.HRow(8, import, add);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

        Control body;
        if (!q.HasData)
            body = q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(2);
        else if (q.Data!.Count == 0)
            body = new CardPanel(new EmptyStateView(Loc.T("mcp.local.emptyTitle"), Loc.T("mcp.local.emptyDetail"), icon: new LucideIcon { Kind = LucideIconKind.Terminal, Size = 24 }));
        else
            body = RowBuilder.Group(null, Loc.T("mcp.local.footer"), q.Data!.Select(ServerRow).ToArray());
        return new StackPanel { Spacing = 8, Children = { header, body } };
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

    private Control ServerRow(LocalMcpServerDto s)
    {
        var label = ClientUi.HRow(8, PdUi.Text(s.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold, mono: true));
        if (!s.Enabled) label.Children.Add(KitBadge.Create(Loc.T("mcp.local.off")));

        var summary = LocalMcpDraft.Summary(s);
        var extra = s.Transport == "http"
            ? s.HeaderNames.Count > 0 ? Loc.T("mcp.local.headerCount", ("count", s.HeaderNames.Count)) : null
            : s.EnvNames.Count > 0 ? Loc.T("mcp.local.envCount", ("count", s.EnvNames.Count)) : null;
        var detail = new StackPanel { Spacing = 4 };
        detail.Children.Add(PdUi.Text(extra is null ? summary : $"{summary} · {extra}", 11.5, ArcDesignTokenKind.TextMuted, mono: true, wrap: true));
        foreach (var p in s.Placements)
        {
            // not wanted and not there: nothing to say
            if (!p.Wanted && p.State == "missing") continue;
            var chip = ClientUi.HRow(6, PdUi.Text(LocalMcpDraft.ClientName(p.Kind), 12, ArcDesignTokenKind.TextSecondary),
                KitBadge.Create(Loc.T("mcp.local.state." + p.State), ToneOf(p.State), p.File));
            if (p.State == "drifted" && p.Wanted)
                chip.Children.Add(Ui.Button(Loc.T("mcp.clients.force"), () => _ = GuardAsync(() => RewriteAsync(s, force: true)), ButtonVariant.Secondary));
            detail.Children.Add(chip);
            if (p.Error is { Length: > 0 } err) detail.Children.Add(PdUi.Text(err, 11.5, ArcDesignTokenKind.Danger, wrap: true));
        }

        var actions = new List<Control>();
        if (LocalMcpDraft.NeedsAttention(s))
            actions.Add(Ui.Button(Loc.T("mcp.local.rewrite"), () => _ = GuardAsync(() => RewriteAsync(s, force: false)), ButtonVariant.Secondary));
        actions.Add(Ui.Menu(s.Name, LucideIconKind.Ellipsis,
        [
            new Ui.MenuItem(Loc.T("common.edit"), LucideIconKind.Pencil, () => LocalMcpSheet.Show(owner()!, api, feedback, s)),
            new Ui.MenuItem(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = GuardAsync(() => DeleteAsync(s)), Destructive: true, SeparatorBefore: true),
        ]));
        return RowBuilder.Row(label, detail, [.. actions]);
    }

    private async Task RewriteAsync(LocalMcpServerDto s, bool force)
    {
        await api.RewriteAsync(s.Id, force);
        feedback.Success(Loc.T("mcp.local.applied", ("name", s.Name)));
    }

    private async Task DeleteAsync(LocalMcpServerDto s)
    {
        if (!await feedback.ConfirmAsync(new ConfirmRequest(Loc.T("mcp.local.deleteTitle", ("name", s.Name)), Loc.T("mcp.local.deleteDetail"), Loc.T("common.delete"), Destructive: true))) return;
        await api.DeleteAsync(s.Id);
        feedback.Success(Loc.T("mcp.local.deleted", ("name", s.Name)));
    }
}
