using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Library;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Library;

/// <summary>
/// The Library page's body (web <c>LibraryPage</c> / <c>ResourcePanel</c> / <c>ProjectsPanel</c>): a tab bar that lives outside the redrawn part and the list of
/// the tab's items below it, each with a chip for the state of every target (written, changed, removed, conflict, project gone …).
/// What the user changed is never overwritten without asking; deleting an item takes out only what Astra wrote and what is still as it wrote it.
/// </summary>
public sealed class LibraryView : UserControl
{
    private readonly LibraryPage _page;
    private readonly QueryHost<ResourceLibraryDto> _items;
    private readonly QueryHost<List<ResourceProjectDto>> _projects;
    private readonly ContentControl _tabs = new();
    private readonly StackPanel _projectForm = new() { Spacing = 8 };
    private bool _wired;
    private string _newProject = "";

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public LibraryView(LibraryPage page)
    {
        _page = page;
        _items = new QueryHost<ResourceLibraryDto>(page.Api.For(Segment), RenderItems);
        _projects = new QueryHost<List<ResourceProjectDto>>(page.Api.Projects, RenderProjects);
        var column = new StackPanel { Spacing = 12, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(_tabs);
        column.Children.Add(_items);
        column.Children.Add(_projectForm);
        column.Children.Add(_projects);
        Content = column;
        BuildTabs();
        Show();
    }

    private string Segment => _page.Tab == "projects" ? "instructions" : _page.Tab;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        BuildTabs();
        _items.Rebuild();
        _projects.Rebuild();
        BuildProjectForm();
    });

    private void Show()
    {
        var projects = _page.Tab == "projects";
        _items.IsVisible = !projects;
        _projects.IsVisible = projects;
        _projectForm.IsVisible = projects;
        if (!projects) _items.Query = _page.Api.For(Segment);
        BuildProjectForm();
    }

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

    private void BuildTabs()
    {
        var options = LibraryApi.Segments.Select((s, i) => new SegmentedOption(s, Loc.T("library.tab." + new[] { "instruction", "agent", "command", "hook", "env" }[i])))
            .Append(new SegmentedOption("projects", Loc.T("library.tab.projects")));
        var tabs = Pick.Segmented(options, _page.Tab, v =>
        {
            _page.Tab = v;
            Show();
        }, Loc.T("nav.library"));
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        _tabs.Content = tabs;
    }

    // ---------- the items of a kind ----------

    private static string KindOf(string segment) => segment switch { "agents" => "agent", "commands" => "command", "hooks" => "hook", "env" => "env", _ => "instruction" };

    private static Tone ToneOf(string state) => LibraryApi.ToneOf(state) switch { "green" => Tone.Green, "orange" => Tone.Orange, "red" => Tone.Red, _ => Tone.Neutral };

    private Control? RenderItems(Query<ResourceLibraryDto> q)
    {
        if (q.Data is not { } data)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("library.error.load"), q.Error?.Message ?? "") : new LoadingState(3);
        var kind = KindOf(Segment);
        var stack = new StackPanel { Spacing = 10 };
        if (!data.CanWrite) stack.Children.Add(ClientUi.Alert(AlertTone.Info, Loc.T("library.localOnly")));
        var add = Ui.Button(Loc.T("library.add"), () => ResourceSheet.Show(this, _page.Api, _page.Feedback, Segment, data, null), ButtonVariant.Primary, LucideIconKind.Plus);
        add.IsEnabled = data.CanWrite;
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(PdUi.Text(Loc.T("library.hint." + kind), 12, ArcDesignTokenKind.TextSecondary, wrap: true));
        stack.Children.Add(header);
        if (data.Items.Count == 0)
            stack.Children.Add(new CardPanel(new EmptyStateView(Loc.T("library.empty.title"), Loc.T("library.empty." + kind))));
        else
            stack.Children.Add(RowBuilder.Group(null, null, data.Items.Select(i => ItemRow(data, i)).ToArray()));
        return stack;
    }

    private Control ItemRow(ResourceLibraryDto data, ResourceItemDto item)
    {
        var label = ClientUi.HRow(8, PdUi.Text(item.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
        if (!item.Enabled) label.Children.Add(KitBadge.Create(Loc.T("library.state.off")));
        var preview = (item.Kind == "hook" ? $"{item.Event}{(string.IsNullOrEmpty(item.Matcher) ? "" : " · " + item.Matcher)} — " : "")
            + (item.Description.Length > 0 ? item.Description : item.Body).Split('\n')[0];

        var detail = new StackPanel { Spacing = 4 };
        detail.Children.Add(PdUi.Text(preview, 11.5, ArcDesignTokenKind.TextSecondary, wrap: false, mono: true));
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 10, LineSpacing = 4 };
        foreach (var t in item.Targets)
        {
            var key = t.Project.Length > 0 ? $"{t.Client}@{t.Project}" : t.Client;
            var text = SkillClientNames.Of(t.Client) + (t.Project.Length > 0 ? " · " + System.IO.Path.GetFileName(t.Project.TrimEnd('/', '\\')) : "");
            var why = item.LastError is { } errors && errors.TryGetValue(key, out var e) ? e : t.Message;
            chips.Children.Add(ClientUi.HRow(4, PdUi.Text(text, 12, ArcDesignTokenKind.Foreground), KitBadge.Create(Loc.T("library.state." + t.State), ToneOf(t.State), why)));
        }
        if (item.Targets.Count == 0) chips.Children.Add(PdUi.Text(Loc.T("library.noTargets"), 11.5, ArcDesignTokenKind.TextMuted));
        detail.Children.Add(chips);

        var menu = new List<Ui.MenuItem>();
        var attention = item.Targets.Any(t => t.State is "drifted" or "conflict" or "missing");
        var drifted = item.Targets.Any(t => t.State is "drifted" or "conflict");
        if (attention) menu.Add(new(Loc.T("library.rewrite"), LucideIconKind.RefreshCw, () => _ = GuardAsync(() => RewriteAsync(item, false))));
        if (drifted) menu.Add(new(Loc.T("library.rewriteForce"), LucideIconKind.Undo2, () => _ = GuardAsync(() => RewriteAsync(item, true))));
        menu.Add(new(Loc.T("common.edit"), LucideIconKind.Pencil, () => ResourceSheet.Show(this, _page.Api, _page.Feedback, Segment, data, item), SeparatorBefore: menu.Count > 0));
        menu.Add(new(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = GuardAsync(() => DeleteAsync(item)), Destructive: true, SeparatorBefore: true));
        var actions = Ui.Menu(item.Name, LucideIconKind.Ellipsis, menu);
        actions.IsEnabled = data.CanWrite;
        return RowBuilder.Row(label, detail, actions);
    }

    private async Task RewriteAsync(ResourceItemDto item, bool force)
    {
        if (force && !await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("library.confirm.force.title", ("name", item.Name)), Loc.T("library.confirm.force.detail"), Loc.T("library.rewriteForce"), Destructive: true))) return;
        await _page.Api.ApplyAsync(Segment, item.Id, force);
        _page.Feedback.Success(Loc.T("library.toast.applied"));
    }

    private async Task DeleteAsync(ResourceItemDto item)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                Loc.T("library.confirm.delete.title", ("name", item.Name)), Loc.T("library.confirm.delete.detail"), Loc.T("common.delete"), Destructive: true))) return;
        try
        {
            await _page.Api.DeleteAsync(Segment, item.Id, force: false);
        }
        catch (ApiException e)
        {
            // what the user changed is theirs and stays; removing it too is the user's call
            if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                    Loc.T("library.confirm.deleteForce.title", ("name", item.Name)), e.Message + "\n\n" + Loc.T("library.confirm.deleteForce.detail"), Loc.T("common.delete"), Destructive: true))) return;
            await _page.Api.DeleteAsync(Segment, item.Id, force: true);
        }
        _page.Feedback.Success(Loc.T("library.toast.deleted"));
    }

    // ---------- projects ----------

    private void BuildProjectForm()
    {
        _projectForm.Children.Clear();
        _projectForm.Children.Add(PdUi.Text(Loc.T("library.projects.hint"), 12, ArcDesignTokenKind.TextSecondary, wrap: true));
        var path = PdUi.TextBox(_newProject, v => _newProject = v, mono: true, name: Loc.T("library.projects.path"), placeholder: "/Users/me/code/app");
        path.HorizontalAlignment = HorizontalAlignment.Stretch;
        var add = Ui.Button(Loc.T("library.projects.add"), () => _ = GuardAsync(async () =>
        {
            var p = await _page.Api.AddProjectAsync(_newProject);
            _newProject = "";
            path.Text = "";
            _page.Feedback.Success(Loc.T("library.projects.added", ("name", p.Name)));
        }), ButtonVariant.Primary, LucideIconKind.Plus);
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        row.Children.Add(path);
        _projectForm.Children.Add(row);
    }

    private Control? RenderProjects(Query<List<ResourceProjectDto>> q)
    {
        if (!q.HasData)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(2);
        if (q.Data!.Count == 0) return new CardPanel(new EmptyStateView(Loc.T("library.projects.empty"), null));
        return RowBuilder.Group(null, null, q.Data!.Select(ProjectRow).ToArray());
    }

    private Control ProjectRow(ResourceProjectDto p)
    {
        var label = ClientUi.HRow(8, PdUi.Text(p.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
        if (!p.Exists) label.Children.Add(KitBadge.Create(Loc.T("library.state.project-missing"), Tone.Orange));
        var forget = Ui.Button(Loc.T("library.projects.forget"), () => _ = GuardAsync(async () =>
        {
            if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(
                    Loc.T("library.projects.forget.title", ("name", p.Name)), Loc.T("library.projects.forget.detail"), Loc.T("library.projects.forget")))) return;
            await _page.Api.RemoveProjectAsync(p.Path);
        }), ButtonVariant.Secondary, LucideIconKind.Trash2);
        return RowBuilder.Row(label, p.Path, forget);
    }
}
