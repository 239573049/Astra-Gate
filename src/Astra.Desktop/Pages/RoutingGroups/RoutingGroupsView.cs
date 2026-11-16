using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.RoutingGroups;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.RoutingGroups;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.RoutingGroups;

/// <summary>
/// The Routing groups page's body (web <c>RoutingGroups</c>): a card per group — name, model id, mode, switches — over a line per member
/// with its live state (health, quota left, last use), redrawn from the groups query and again whenever the polled status changes. The
/// editor is a sheet; reordering members there uses the arrow buttons where the web drags.
/// </summary>
public sealed class RoutingGroupsView : UserControl
{
    private readonly RoutingGroupsPage _page;
    private readonly QueryHost<List<RoutingGroupDto>> _groups;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public RoutingGroupsView(RoutingGroupsPage page)
    {
        _page = page;
        _groups = new QueryHost<List<RoutingGroupDto>>(page.Api.Groups, Render);
        var column = new StackPanel { Spacing = 12, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(PdUi.Text(Loc.T("routingGroups.intro"), 12, ArcDesignTokenKind.TextSecondary, wrap: true));
        column.Children.Add(_groups);
        Content = column;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.EditRequested += ShowEditor;
        _page.Api.Status.PropertyChanged += OnStatus;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.EditRequested -= ShowEditor;
        _page.Api.Status.PropertyChanged -= OnStatus;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnStatus(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(_groups.Rebuild);

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(_groups.Rebuild);

    private void ShowEditor(RoutingGroupDto? group) => RoutingGroupSheet.Show(this, _page.Api, _page.Feedback, group);

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            _page.Feedback.Error(Loc.T("common.error"), string.Join('\n', RoutingGroupDraft.Explain(e)));
        }
    }

    // ---------- the list ----------

    private Control? Render(Query<List<RoutingGroupDto>> q)
    {
        if (!q.HasData)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(3);
        if (q.Data!.Count == 0)
            return new CardPanel(new EmptyStateView(Loc.T("routingGroups.empty"), Loc.T("routingGroups.emptyDetail"), icon: new LucideIcon { Kind = LucideIconKind.Layers, Size = 24 }));
        var stack = new StackPanel { Spacing = 12 };
        foreach (var g in q.Data!) stack.Children.Add(Card(g));
        return stack;
    }

    private static Tone HealthTone(string health) => health switch
    {
        "healthy" => Tone.Green,
        "cooling" or "open" or "half_open" => Tone.Orange,
        "group" => Tone.Neutral,
        _ => Tone.Red,
    };

    private Control Card(RoutingGroupDto g)
    {
        var live = _page.Api.Status.Data?.FirstOrDefault(s => s.Id == g.Id);
        var byKey = live?.Members.ToDictionary(m => m.Key) ?? [];

        var title = ClientUi.HRow(8, PdUi.Text(g.Name, 14, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.Medium));
        title.Children.Add(KitBadge.Create(Loc.T("routingGroups.mode." + g.Mode), Tone.Accent));
        if (!g.Enabled) title.Children.Add(KitBadge.Create(Loc.T("routingGroups.disabled")));
        if (!g.Listed) title.Children.Add(KitBadge.Create(Loc.T("routingGroups.unlisted")));
        var heading = new StackPanel { Spacing = 2, Children = { title, PdUi.Text(g.ModelId, 12, ArcDesignTokenKind.TextSecondary, mono: true) } };

        var actions = ClientUi.HRow(8,
            PdUi.Toggle(g.Enabled, on => _ = GuardAsync(() => _page.Api.UpdateAsync(g.Id, new RoutingGroupWrite(Enabled: on))), Loc.T("routingGroups.enabled")),
            Ui.Menu(g.Name, LucideIconKind.Ellipsis,
            [
                new Ui.MenuItem(Loc.T("common.edit"), LucideIconKind.Pencil, () => _page.BeginEdit(g)),
                new Ui.MenuItem(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = GuardAsync(() => DeleteAsync(g)), Destructive: true, SeparatorBefore: true),
            ]));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        header.Children.Add(heading);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var members = new StackPanel();
        for (var i = 0; i < g.Members.Count; i++)
        {
            var m = g.Members[i];
            byKey.TryGetValue(RoutingGroupDraft.KeyOf(m), out var state);
            members.Children.Add(MemberLine(g, m, i, state));
        }

        var body = new StackPanel { Spacing = 10, Children = { header, PdUi.Card(PdUi.Divided(members.Children.ToList()), new Thickness(0)) } };
        if (live is { LastReason: { Length: > 0 } reason, LastDecisionAt: { } at })
            body.Children.Add(PdUi.Text(Loc.T("routingGroups.lastDecision", ("reason", RoutingGroupDraft.ReasonText(reason)), ("time", SettingsFormat.DateTime(at, Loc.Language))), 11, ArcDesignTokenKind.TextMuted));
        return new CardPanel(body) { Opacity = g.Enabled ? 1 : 0.7 };
    }

    private Control MemberLine(RoutingGroupDto g, RoutingGroupMemberDto m, int index, RoutingGroupMemberStatusDto? state)
    {
        var name = !string.IsNullOrEmpty(m.Group) ? "group/" + m.Group : $"{state?.ProviderName ?? m.Provider} · {m.Model}";
        var line = new WrapPanel { ItemSpacing = 10, LineSpacing = 4, Margin = new Thickness(12, 8), VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(PdUi.Text((index + 1).ToString(), 12, ArcDesignTokenKind.TextMuted));
        line.Children.Add(PdUi.Text(name, 13, ArcDesignTokenKind.Foreground));
        if (g.Mode == "weight") line.Children.Add(KitBadge.Create(Loc.T("routingGroups.weightN", ("n", m.Weight))));
        if (state is not null)
        {
            line.Children.Add(KitBadge.Create(RoutingGroupDraft.HealthText(state, Loc.Language), HealthTone(state.Health)));
            if (state.Problem is { Length: > 0 } problem) line.Children.Add(KitBadge.Create(Loc.T("routingGroups.memberProblem." + problem), Tone.Red));
            if (state.UsedPercent is { } used)
            {
                var quota = Loc.T("routingGroups.quotaUsed", ("percent", (int)Math.Round(used)));
                if (state.ResetsAt is { } reset) quota += " · " + Loc.T("routingGroups.resets", ("time", SettingsFormat.DateTime(reset, Loc.Language)));
                line.Children.Add(PdUi.Text(quota, 12, ArcDesignTokenKind.TextSecondary));
            }
            else if (state.Health != "group")
                line.Children.Add(PdUi.Text(Loc.T("routingGroups.quotaUnknown"), 12, ArcDesignTokenKind.TextMuted));
            if (state.LastUsed is { } last)
                line.Children.Add(PdUi.Text(Loc.T("routingGroups.lastUsed", ("time", SettingsFormat.DateTime(last, Loc.Language))), 12, ArcDesignTokenKind.TextMuted));
        }
        return line;
    }

    private async Task DeleteAsync(RoutingGroupDto g)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("routingGroups.deleteTitle", ("name", g.Name)), Loc.T("routingGroups.deleteDetail", ("model", g.ModelId)), Loc.T("common.delete"), Destructive: true))) return;
        await _page.Api.DeleteAsync(g.Id);
    }
}
