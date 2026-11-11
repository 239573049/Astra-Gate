using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Skills;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;

namespace Astra.Desktop.Pages.Skills;

/// <summary>
/// The Skills page's body (web <c>SkillsPage</c>): a tab bar and a search box that live outside the redrawn part (so a refresh never takes
/// the focus out of the box), and the list below them, redrawn from the library query — each Skill with a chip per client, or the Skills
/// found in client directories with a tick each.
/// </summary>
public sealed class SkillsView : UserControl
{
    private readonly SkillsPage _page;
    private readonly QueryHost<SkillLibraryDto> _body;
    private readonly ContentControl _tabs = new();
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public SkillsView(SkillsPage page)
    {
        _page = page;
        _body = new QueryHost<SkillLibraryDto>(page.Api.Library, Render);
        var column = new StackPanel { Spacing = 12, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(_tabs);
        column.Children.Add(_body);
        Content = column;
        BuildTabs();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.InstallRequested += ShowInstall;
        _page.ReturnAllRequested += ReturnAll;
        _page.MigrateRequested += ShowMigrate;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.InstallRequested -= ShowInstall;
        _page.ReturnAllRequested -= ReturnAll;
        _page.MigrateRequested -= ShowMigrate;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        BuildTabs();
        _body.Rebuild();
    });

    private SkillLibraryDto? Data => _page.Api.Library.Data;

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException e)
        {
            _page.Feedback.Error(Loc.T("common.error"), e.Message);
        }
    }

    // ---------- tab bar ----------

    private void BuildTabs()
    {
        var tabs = Pick.Segmented(
            [new SegmentedOption("managed", Loc.T("skills.tab.managed")), new SegmentedOption("found", Loc.T("skills.tab.found"))],
            _page.Tab, v =>
            {
                _page.Tab = v;
                _body.Rebuild();
            }, Loc.T("nav.skills"));
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        var search = PdUi.TextBox(_page.Search, v =>
        {
            _page.Search = v;
            if (_page.Tab == "managed") _body.Rebuild();
        }, 240, name: Loc.T("skills.search"), placeholder: Loc.T("skills.search"));
        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(search, Dock.Right);
        row.Children.Add(search);
        row.Children.Add(tabs);
        _tabs.Content = row;
    }

    // ---------- body ----------

    private Control? Render(Query<SkillLibraryDto> q)
    {
        if (q.Data is not { } data)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("skills.error.load"), q.Error?.Message ?? "") : new LoadingState(3);
        var stack = new StackPanel { Spacing = 12 };
        if (!data.CanCreate) stack.Children.Add(ClientUi.Alert(AlertTone.Info, Loc.T("skills.install.disabled")));
        var unmanaged = data.Found.Where(f => f.Kind == "unmanaged").ToList();
        if (_page.Tab == "managed")
        {
            if (unmanaged.Count > 0) stack.Children.Add(FoundBanner(unmanaged.Count));
            stack.Children.Add(RenderManaged(data));
        }
        else
            stack.Children.Add(RenderFound(data, unmanaged));
        return stack;
    }

    private Control FoundBanner(int count)
    {
        var go = Ui.Button(Loc.T("skills.foundBannerAction"), () =>
        {
            _page.Tab = "found";
            BuildTabs();
            _body.Rebuild();
        }, ButtonVariant.Secondary);
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(16, 10) };
        DockPanel.SetDock(go, Dock.Right);
        row.Children.Add(go);
        row.Children.Add(PdUi.Text(Loc.T("skills.foundBanner", ("n", count)), 12, ArcDesignTokenKind.Foreground, wrap: true));
        return PdUi.Card(row);
    }

    // ---------- managed ----------

    private Control RenderManaged(SkillLibraryDto data)
    {
        var query = _page.Search.Trim();
        var skills = data.Skills.Where(s => query.Length == 0
            || s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (data.Skills.Count == 0)
        {
            var empty = new EmptyStateView(Loc.T("skills.empty.title"), Loc.T("skills.empty.detail"), icon: new LucideIcon { Kind = LucideIconKind.Sparkles, Size = 24 });
            return new CardPanel(empty);
        }
        var legend = PdUi.Text(string.Join("   ·   ", new[] { "skills.legend.on", "skills.legend.shared", "skills.legend.copy", "skills.legend.off" }.Select(k => Loc.T(k))),
            11, ArcDesignTokenKind.TextMuted, wrap: true);
        legend.Margin = new Thickness(4, 0, 0, 0);
        return new StackPanel { Spacing = 8, Children = { RowBuilder.Group(null, null, skills.Select(s => SkillRow(s, data.Targets)).ToArray()), legend } };
    }

    private Control SkillRow(SkillDto skill, List<SkillTargetDto> targets)
    {
        var modified = skill.Placements.Any(p => p.Modified);
        var label = ClientUi.HRow(8,
            PdUi.Text(skill.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold),
            KitBadge.Create(Loc.T("skills.source." + skill.SourceKind), skill.SourceKind == "git" ? Tone.Green : Tone.Neutral));
        if (modified) label.Children.Add(KitBadge.Create(Loc.T("skills.copyModified"), Tone.Orange));

        var detail = new StackPanel { Spacing = 2 };
        detail.Children.Add(PdUi.Text(skill.Description.Length > 0 ? skill.Description : "—", 11.5, ArcDesignTokenKind.TextSecondary, wrap: true));
        var meta = new List<string>();
        if (SkillsApi.SourceText(skill.SourceJson) is { Length: > 0 } source) meta.Add(source);
        if (skill.Placements.Count == 0) meta.Add(Loc.T("skills.noPlacements"));
        if (meta.Count > 0) detail.Children.Add(PdUi.Text(string.Join(" · ", meta), 11.5, ArcDesignTokenKind.TextMuted, wrap: true, mono: false));

        var chips = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = 380, ItemSpacing = 6, LineSpacing = 6 };
        foreach (var view in skill.Clients) chips.Children.Add(Chip(skill, view, targets.FirstOrDefault(t => t.Kind == view.Kind)));

        var items = new List<Ui.MenuItem>();
        if (modified)
        {
            items.Add(new(Loc.T("skills.menu.take"), LucideIconKind.ArrowUpFromLine, () => _ = GuardAsync(() => CopyActionAsync(skill.Name, takeEdits: true))));
            items.Add(new(Loc.T("skills.menu.overwrite"), LucideIconKind.RefreshCw, () => _ = GuardAsync(() => CopyActionAsync(skill.Name, takeEdits: false))));
        }
        if (skill.Placements.Count > 0)
            items.Add(new(Loc.T("skills.menu.return"), LucideIconKind.Undo2, () => _ = GuardAsync(() => GiveBackAsync(skill.Name)), SeparatorBefore: modified));
        items.Add(new(Loc.T("skills.menu.delete"), LucideIconKind.Trash2, () => _ = GuardAsync(() => DeleteAsync(skill.Name)), Destructive: true, SeparatorBefore: true));
        return RowBuilder.Row(label, detail, chips, Ui.Menu(skill.Name, LucideIconKind.Ellipsis, items));
    }

    private Control Chip(SkillDto skill, SkillClientDto view, SkillTargetDto? target)
    {
        var look = SkillsApi.LookOf(view, target);
        var name = SkillClientNames.Of(view.Kind);
        var text = look switch
        {
            SkillChipLook.Shared => $"{name} · {Loc.T("skills.badge.shared")}",
            SkillChipLook.Copy => $"{name} · {Loc.T("skills.badge.copy")}{(view.Modified ? "·" + Loc.T("skills.badge.modified") : "")}",
            _ => name,
        };
        var tone = look switch
        {
            SkillChipLook.On or SkillChipLook.Shared or SkillChipLook.Locked => Tone.Accent,
            SkillChipLook.Copy => Tone.Orange,
            _ => Tone.Neutral,
        };
        var hint = look switch
        {
            SkillChipLook.Shared => Loc.T("skills.badge.sharedHint"),
            SkillChipLook.Locked => Loc.T("skills.badge.viaHint", ("client", SkillClientNames.Of(view.State.StartsWith("via-", StringComparison.Ordinal) ? view.State[4..] : view.State))),
            SkillChipLook.Copy => Loc.T("skills.badge.copyHint"),
            SkillChipLook.Disabled => Loc.T(target?.Supported == true ? "skills.badge.notDetected" : "skills.badge.unsupported"),
            _ => null,
        };
        var chip = KitBadge.Create(text, tone, hint);
        chip.Opacity = look is SkillChipLook.Disabled ? 0.45 : 1;
        if (look is SkillChipLook.On or SkillChipLook.Shared or SkillChipLook.Copy or SkillChipLook.Off)
            chip.Clickable(() => _ = GuardAsync(() => ToggleAsync(skill.Name, view.Kind, look == SkillChipLook.Off)));
        return chip;
    }

    /// <summary>Switches a client on or off; removing a link other clients share asks first (the server answers <c>NeedsConfirm</c> and changes nothing).</summary>
    private async Task ToggleAsync(string skill, string client, bool on)
    {
        var result = await _page.Api.SwitchAsync(skill, client, on);
        if (!result.NeedsConfirm) return;
        var others = string.Join("、", result.Affected.Select(SkillClientNames.Of));
        var ok = await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
            Loc.T("skills.confirm.sharedOff.title", ("client", SkillClientNames.Of(client))),
            Loc.T("skills.confirm.sharedOff.detail", ("client", SkillClientNames.Of(client)), ("skill", skill), ("others", others)),
            Loc.T("skills.confirm.sharedOff.confirm"), Destructive: true));
        if (ok) await _page.Api.SwitchAsync(skill, client, on, confirm: true);
    }

    private void Report(List<SkillOutcomeDto> outcomes, string successKey)
    {
        var failed = outcomes.Where(o => !o.Ok).ToList();
        if (failed.Count == 0) _page.Feedback.Success(Loc.T(successKey));
        else _page.Feedback.Error(Loc.T("skills.toast.partial", ("ok", outcomes.Count - failed.Count), ("failed", failed.Count), ("reason", failed[0].Reason ?? "")));
    }

    private async Task GiveBackAsync(string name)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("skills.confirm.return.title", ("skill", name)), Loc.T("skills.confirm.return.detail"), Loc.T("skills.menu.return")))) return;
        Report(await _page.Api.ReturnAsync(name), "skills.toast.returned");
    }

    private void ReturnAll() => _ = GuardAsync(async () =>
    {
        if (Data is { Skills.Count: 0 }) return;
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("skills.confirm.returnAll.title"), Loc.T("skills.confirm.returnAll.detail"), Loc.T("skills.returnAll")))) return;
        Report(await _page.Api.ReturnAsync(null), "skills.toast.returned");
    });

    private async Task DeleteAsync(string name)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("skills.confirm.delete.title", ("skill", name)), Loc.T("skills.confirm.delete.detail"), Loc.T("common.delete"), Destructive: true))) return;
        await _page.Api.DeleteAsync(name);
        _page.Feedback.Success(Loc.T("skills.toast.deleted"));
    }

    private async Task CopyActionAsync(string name, bool takeEdits)
    {
        var prefix = takeEdits ? "skills.confirm.take" : "skills.confirm.overwrite";
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T(prefix + ".title"), Loc.T(prefix + ".detail"), Loc.T(takeEdits ? "skills.menu.take" : "skills.menu.overwrite")))) return;
        await _page.Api.CopyActionAsync(name, takeEdits);
        _page.Feedback.Success(Loc.T("skills.toast.done"));
    }

    // ---------- found ----------

    private Control RenderFound(SkillLibraryDto data, List<FoundSkillDto> unmanaged)
    {
        var external = data.Found.Where(f => f.Kind == "external").ToList();
        if (unmanaged.Count == 0 && external.Count == 0) return new CardPanel(new EmptyStateView(Loc.T("skills.found.empty"), null));

        // A Skill the skills CLI manages is not ticked by default: migrating it would let `skills update` write into Astra's copy.
        var selected = _page.Selected ??= unmanaged.Where(f => !f.SkillsCli).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        selected.IntersectWith(unmanaged.Select(f => f.Name));
        var summary = PdUi.Text(Loc.T("skills.found.selected", ("n", selected.Count), ("total", unmanaged.Count)), 12, ArcDesignTokenKind.TextSecondary);
        var migrate = Ui.Button(Loc.T("skills.found.migrate"), () => _page.BeginMigrate(selected.ToList()), ButtonVariant.Primary);
        migrate.IsEnabled = selected.Count > 0;
        var bar = new DockPanel { LastChildFill = false, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(migrate, Dock.Right);
        bar.Children.Add(migrate);
        bar.Children.Add(summary);

        var rows = new List<Control>();
        foreach (var f in unmanaged)
        {
            var check = new Checkbox { IsChecked = selected.Contains(f.Name), VerticalAlignment = VerticalAlignment.Center };
            var name = f.Name;
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true) selected.Add(name);
                else selected.Remove(name);
                summary.Text = Loc.T("skills.found.selected", ("n", selected.Count), ("total", unmanaged.Count));
                migrate.IsEnabled = selected.Count > 0;
            };
            var label = ClientUi.HRow(8, check, PdUi.Text(f.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold),
                KitBadge.Create(Loc.T("skills.found.kind.unmanaged")));
            if (f.Conflict) label.Children.Add(KitBadge.Create(Loc.T("skills.found.differs"), Tone.Orange));
            if (f.InLibrary) label.Children.Add(KitBadge.Create(Loc.T("skills.found.inLibrary"), Tone.Green));
            if (f.SkillsCli) label.Children.Add(KitBadge.Create(Loc.T("skills.found.skillsCli"), Tone.Orange, Loc.T("skills.found.skillsCliHint")));
            rows.Add(RowBuilder.Row(label, string.Join(" · ", f.Locations.Select(l => l.Path)), PdUi.Text(Loc.T("skills.found.places", ("n", f.Locations.Count)), 11.5, ArcDesignTokenKind.TextMuted)));
        }
        foreach (var f in external)
        {
            var label = ClientUi.HRow(8, PdUi.Text(f.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold), KitBadge.Create(Loc.T("skills.found.kind.external")));
            var detail = $"{f.Locations.FirstOrDefault()?.Path} → {f.LinkTarget}\n{Loc.T("skills.found.externalHint")}";
            var row = RowBuilder.Row(label, detail);
            row.Opacity = 0.7;
            rows.Add(row);
        }
        return new StackPanel { Spacing = 8, Children = { bar, RowBuilder.Group(null, null, rows.ToArray()) } };
    }

    // ---------- sheets ----------

    private void ShowInstall()
    {
        if (Data is { CanCreate: false })
        {
            _page.Feedback.Info(Loc.T("skills.install.disabled"));
            return;
        }
        if (Data is { } data) SkillInstallSheet.Show(this, _page.Api, _page.Feedback, data);
    }

    private void ShowMigrate(IReadOnlyList<string> names)
    {
        if (Data is { } data) SkillMigrateSheet.Show(this, _page.Api, _page.Feedback, names, data.LibraryDir);
    }
}
