using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.RoutingGroups;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.RoutingGroups;

/// <summary>
/// The add / edit sheet of a routing group (web <c>GroupSheet</c>): name, model id suffix (fixed once saved), routing and "stays", the members
/// in order (arrow buttons where the web drags) with a weight under the weight mode, a picker for each further model or group, and the switches.
/// A refused save lists every reason in the sheet.
/// </summary>
public static class RoutingGroupSheet
{
    /// <summary>Opens the sheet; <paramref name="existing"/> null = a new group.</summary>
    public static Sheet Show(Visual owner, RoutingGroupsApi api, IFeedback feedback, RoutingGroupDto? existing)
    {
        var draft = existing is null ? new RoutingGroupDraft() : new RoutingGroupDraft(existing);
        var save = Ui.Button(Loc.T("common.save"), () => { }, ButtonVariant.Primary);
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });
        var problems = new StackPanel { Spacing = 6, IsVisible = false };
        var membersHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var pickers = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var modeHint = PdUi.Hint("");
        var staysHint = PdUi.Hint("");
        TextBox? slugBox = null;

        void Check() => save.IsEnabled = draft.IsValid;
        void Hints()
        {
            modeHint.Text = Loc.T("routingGroups.mode." + draft.Mode + ".hint");
            staysHint.Text = Loc.T("routingGroups.stays." + draft.Stays + ".hint");
        }

        var models = api.Models.Data ?? [];
        var all = api.Groups.Data ?? [];
        string NameOf(RoutingGroupMemberDto m) => !string.IsNullOrEmpty(m.Group)
            ? "group/" + m.Group
            : $"{models.FirstOrDefault(x => x.ProviderId == m.Provider && x.Id == m.Model)?.ProviderName ?? m.Provider} · {m.Model}";

        void Members()
        {
            var rows = new StackPanel { Spacing = 0 };
            if (draft.Members.Count == 0) rows.Children.Add(PdUi.Text(Loc.T("routingGroups.noMembers"), 12, ArcDesignTokenKind.TextMuted));
            for (var i = 0; i < draft.Members.Count; i++)
            {
                var at = i;
                var m = draft.Members[i];
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(10, 6) };
                var name = PdUi.Text(NameOf(m), 13, ArcDesignTokenKind.Foreground);
                name.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(name);
                var tools = ClientUi.HRow(6);
                if (draft.Mode == "weight")
                    tools.Children.Add(PdUi.Number(m.Weight, v => draft.Members[at] = m with { Weight = (int)Math.Clamp(v ?? 1, 1, 1000) }, 86, Loc.T("routingGroups.weight"), integer: true, minimum: 1));
                tools.Children.Add(PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.ArrowUp, small: true, name: Loc.T("routingGroups.moveUp"), click: () => Move(at, -1)).With(b => b.IsEnabled = at > 0));
                tools.Children.Add(PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.ArrowDown, small: true, name: Loc.T("routingGroups.moveDown"), click: () => Move(at, 1)).With(b => b.IsEnabled = at < draft.Members.Count - 1));
                tools.Children.Add(PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.Trash2, small: true, name: Loc.T("routingGroups.remove", ("name", NameOf(m))), click: () =>
                {
                    draft.Members.RemoveAt(at);
                    Members();
                    Pickers();
                    Check();
                }));
                Grid.SetColumn(tools, 1);
                line.Children.Add(tools);
                rows.Children.Add(line);
            }
            membersHost.Content = PdUi.Card(PdUi.Divided(rows.Children.ToList()), new Thickness(0));
        }

        void Move(int at, int by)
        {
            var to = at + by;
            if (to < 0 || to >= draft.Members.Count) return;
            (draft.Members[at], draft.Members[to]) = (draft.Members[to], draft.Members[at]);
            Members();
        }

        void Pickers()
        {
            var taken = draft.Members.Select(RoutingGroupDraft.KeyOf).ToHashSet();
            var modelOptions = models.Where(m => !taken.Contains($"{m.ProviderId}/{m.Id}")).Select(m => ($"{m.ProviderId}/{m.Id}", $"{m.ProviderName} · {m.Id}")).ToList();
            var groupOptions = all.Where(g => g.Id != existing?.Id && !taken.Contains("group/" + g.Slug)).Select(g => (g.Slug, $"group/{g.Slug} · {g.Name}")).ToList();
            var model = PdUi.Choice(modelOptions, null, key =>
            {
                var picked = models.FirstOrDefault(m => $"{m.ProviderId}/{m.Id}" == key);
                if (picked is null) return;
                draft.Members.Add(new RoutingGroupMemberDto(picked.ProviderId, picked.Id, null, 1));
                Members();
                Pickers();
                Check();
            }, name: Loc.T("routingGroups.addModel"), placeholder: Loc.T("routingGroups.addModel"));
            model.IsEnabled = modelOptions.Count > 0;
            model.HorizontalAlignment = HorizontalAlignment.Stretch;
            var group = PdUi.Choice(groupOptions, null, slug =>
            {
                draft.Members.Add(new RoutingGroupMemberDto(null, null, slug, 1));
                Members();
                Pickers();
                Check();
            }, name: Loc.T("routingGroups.addGroup"), placeholder: Loc.T("routingGroups.addGroup"));
            group.IsEnabled = groupOptions.Count > 0;
            group.HorizontalAlignment = HorizontalAlignment.Stretch;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
            grid.Children.Add(model);
            Grid.SetColumn(group, 1);
            grid.Children.Add(group);
            pickers.Content = grid;
        }

        var name = PdUi.TextBox(draft.Name, v =>
        {
            draft.SetName(v);
            if (slugBox is not null && slugBox.Text != draft.Slug) slugBox.Text = draft.Slug;
            Check();
        }, name: Loc.T("routingGroups.name"), placeholder: "Opus anywhere");
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var slug = PdUi.TextBox(draft.Slug, v =>
        {
            draft.SlugTouched = true;
            draft.Slug = v;
            Check();
        }, mono: true, name: Loc.T("routingGroups.slug"), placeholder: "opus-anywhere");
        slug.HorizontalAlignment = HorizontalAlignment.Stretch;
        slug.IsEnabled = existing is null;
        slugBox = slug;

        var mode = PdUi.Choice(RoutingGroupDraft.Modes.Select(m => (m, Loc.T("routingGroups.mode." + m))), draft.Mode, v => { draft.Mode = v; Hints(); Members(); }, name: Loc.T("routingGroups.modeLabel"));
        mode.HorizontalAlignment = HorizontalAlignment.Stretch;
        var stays = PdUi.Choice(RoutingGroupDraft.StaysOptions.Select(s => (s, Loc.T("routingGroups.stays." + s))), draft.Stays, v => { draft.Stays = v; Hints(); }, name: Loc.T("routingGroups.staysLabel"));
        stays.HorizontalAlignment = HorizontalAlignment.Stretch;

        Hints();
        Members();
        Pickers();

        var membersBlock = new StackPanel
        {
            Spacing = 8,
            Children = { PdUi.Text(Loc.T("routingGroups.membersTitle"), 12, ArcDesignTokenKind.TextSecondary, Avalonia.Media.FontWeight.Medium), PdUi.Hint(Loc.T("routingGroups.membersHint")), membersHost, pickers },
        };
        var form = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new FieldBlock(Loc.T("routingGroups.name"), name),
                new FieldBlock(Loc.T("routingGroups.slug"), slug, existing is null ? Loc.T("routingGroups.slugHint", ("model", "group/" + (draft.Slug.Length > 0 ? draft.Slug : "…"))) : Loc.T("routingGroups.slugLocked")),
                new FieldBlock(Loc.T("routingGroups.modeLabel"), new StackPanel { Spacing = 6, Children = { mode, modeHint } }),
                new FieldBlock(Loc.T("routingGroups.staysLabel"), new StackPanel { Spacing = 6, Children = { stays, staysHint } }),
                membersBlock,
                PdUi.LabeledToggle(draft.Listed, v => draft.Listed = v, Loc.T("routingGroups.listed")),
                PdUi.Hint(Loc.T("routingGroups.listedHint")),
                PdUi.LabeledToggle(draft.Enabled, v => draft.Enabled = v, Loc.T("routingGroups.enabled")),
                problems,
            },
        };

        var sheet = new Sheet
        {
            Title = existing is null ? Loc.T("routingGroups.addTitle") : Loc.T("routingGroups.editTitle", ("name", existing.Name)),
            SheetWidth = 600,
            Content = form,
            Footer = [cancel, save],
        };
        cancel.Click += (_, _) => sheet.Close();
        save.Click += async (_, _) =>
        {
            save.IsLoading = true;
            problems.IsVisible = false;
            try
            {
                var body = draft.ToBody();
                if (existing is null) await api.CreateAsync(body);
                else await api.UpdateAsync(existing.Id, body);
                sheet.Close();
            }
            catch (ApiException e)
            {
                problems.Children.Clear();
                problems.Children.Add(PdUi.Text(Loc.T("routingGroups.refused"), 12, ArcDesignTokenKind.Danger, Avalonia.Media.FontWeight.Medium));
                foreach (var line in RoutingGroupDraft.Explain(e)) problems.Children.Add(PdUi.Text("• " + line, 12, ArcDesignTokenKind.Danger, wrap: true));
                problems.IsVisible = true;
            }
            finally
            {
                save.IsLoading = false;
            }
        };
        Check();
        _ = sheet.ShowAsync(owner);
        return sheet;
    }
}
