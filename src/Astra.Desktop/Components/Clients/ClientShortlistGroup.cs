using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The models a client should see (web <c>ClientShortlistGroup</c>, magpie's "visible" list): only the ticked ones reach its model list;
/// none ticked = no limit. It is saved on its own (it rewrites an enabled client's model list), not through the pending-changes bar.
/// </summary>
public static class ClientShortlistGroup
{
    /// <summary>
    /// The group over <paramref name="all"/> (every model the client can reach) with <paramref name="picked"/> ticked; ids picked earlier that
    /// the providers no longer serve stay listed so they can be unticked. <paramref name="onChange"/> gets each new pick list;
    /// <paramref name="apply"/> saves it (shown only when <paramref name="changed"/>).
    /// </summary>
    public static SettingsGroup Create(IReadOnlyList<string> all, IReadOnlyList<string> picked, bool changed, bool busy, bool enabled,
        Action<List<string>> onChange, Action apply)
    {
        var rows = all.Concat(picked.Where(m => !all.Contains(m, StringComparer.Ordinal))).ToList();
        var group = new SettingsGroup { Title = Loc.T("clients.shortlist.title"), Hint = enabled ? Loc.T("clients.shortlist.footerEnabled") : Loc.T("clients.shortlist.footer") };
        var summary = new SettingsRow
        {
            Label = picked.Count == 0
                ? Loc.T("clients.shortlist.empty")
                : Loc.T("clients.shortlist.summary", ("count", picked.Count), ("total", rows.Count)),
        };
        if (picked.Count > 0)
            summary.Trailing.Add(ClientUi.Button(Loc.T("clients.shortlist.clear"), () => onChange([]), small: true));
        if (changed)
        {
            var save = ClientUi.Button(Loc.T("common.apply"), apply, ButtonVariant.Primary, small: true);
            save.IsLoading = busy;
            summary.Trailing.Add(save);
        }
        group.Rows.Add(summary);
        foreach (var model in rows)
        {
            var id = model;
            var row = new SettingsRow { Label = id, Detail = all.Contains(id, StringComparer.Ordinal) ? null : Loc.T("clients.shortlist.missing") };
            var toggle = new Switch { IsChecked = picked.Contains(id, StringComparer.Ordinal) };
            Avalonia.Automation.AutomationProperties.SetName(toggle, id);
            toggle.IsCheckedChanged += (_, _) =>
            {
                var next = picked.Where(m => m != id).ToList();
                if (toggle.IsChecked == true) next.Add(id);
                onChange(next);
            };
            row.Trailing.Add(toggle);
            group.Rows.Add(row);
        }
        return group;
    }
}
