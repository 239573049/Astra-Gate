using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Library;

/// <summary>
/// The add / edit sheet of one library item (web <c>ResourceSheet</c>): the fields of its kind and, for each client, a switch for its user-wide
/// file and one for each project directory it can also be written into.
/// </summary>
public static class ResourceSheet
{
    private static readonly string[] Kinds = ["instruction", "agent", "command", "hook", "env"];

    /// <summary>Opens the sheet; <paramref name="existing"/> null = a new item.</summary>
    public static Sheet Show(Visual owner, LibraryApi api, IFeedback feedback, string segment, ResourceLibraryDto library, ResourceItemDto? existing)
    {
        var kind = Kinds[LibraryApi.Segments.ToList().IndexOf(segment)];
        var draft = new ResourceDraft(kind, existing);
        var save = Ui.Button(Loc.T("common.save"), () => { }, ButtonVariant.Primary);
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });
        void Check() => save.IsEnabled = draft.IsValid;

        var name = PdUi.TextBox(draft.Name, v => { draft.Name = v; Check(); }, mono: true, name: Loc.T("library.field.name"));
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var form = new StackPanel { Spacing = 16 };
        form.Children.Add(new FieldBlock(Loc.T("library.field.name"), name, Loc.T("library.nameHint." + kind)));

        if (kind is "agent" or "command" or "hook")
        {
            var description = PdUi.TextBox(draft.Description, v => { draft.Description = v; Check(); }, name: Loc.T("library.field.description"));
            description.HorizontalAlignment = HorizontalAlignment.Stretch;
            form.Children.Add(new FieldBlock(Loc.T("library.field.description"), description));
        }

        if (kind == "hook")
        {
            var matcher = new StackPanel { Spacing = 16 };
            void ShowMatcher() => matcher.IsVisible = draft.Event.Contains(':') || draft.Event is not ("turn-end" or "prompt");
            var events = Pick.Segmented(ResourceDraft.HookEvents.Select(e => new SegmentedOption(e, Loc.T("library.event." + e))), ResourceDraft.HookEvents.Contains(draft.Event) ? draft.Event : "turn-end",
                v => { draft.Event = v; ShowMatcher(); }, Loc.T("library.field.event"));
            events.HorizontalAlignment = HorizontalAlignment.Left;
            var matcherBox = PdUi.TextBox(draft.Matcher, v => draft.Matcher = v, mono: true, name: Loc.T("library.field.matcher"), placeholder: "shell | edit | startup|resume");
            matcherBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            matcher.Children.Add(new FieldBlock(Loc.T("library.field.matcher"), matcherBox, Loc.T("library.field.matcherTools") + " " + Loc.T("library.field.matcherStarts")));
            var timeout = PdUi.TextBox(draft.Timeout > 0 ? draft.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture) : "", v => draft.Timeout = int.TryParse(v, out var n) && n >= 0 ? n : 0, mono: true, name: Loc.T("library.field.timeout"));
            form.Children.Add(new FieldBlock(Loc.T("library.field.event"), events, Loc.T("library.field.eventHint")));
            form.Children.Add(matcher);
            form.Children.Add(new FieldBlock(Loc.T("library.field.timeout"), timeout));
            ShowMatcher();
        }

        Control body = kind is "hook" or "env"
            ? PdUi.TextBox(draft.Body, v => { draft.Body = v; Check(); }, mono: true, name: Loc.T("library.field.body." + kind))
            : PdUi.Area(draft.Body, v => { draft.Body = v; Check(); }, null, kind == "instruction" ? 160 : 120, mono: true);
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(new FieldBlock(Loc.T("library.field.body." + kind), body, kind == "command" ? Loc.T("library.field.commandArgsHint") : null));

        if (kind == "agent")
        {
            var model = PdUi.TextBox(draft.Model, v => draft.Model = v, mono: true, name: Loc.T("library.field.model"), placeholder: "gpt-5");
            form.Children.Add(new FieldBlock(Loc.T("library.field.model"), model, Loc.T("library.field.modelHint")));
        }

        var targets = new StackPanel { Spacing = 10 };
        foreach (var client in library.Clients)
        {
            var line = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 4 };
            line.Children.Add(PdUi.LabeledToggle(draft.Targets.Contains((client.Kind, "")), on => Toggle(draft, client.Kind, "", on), Loc.T("library.userWide")));
            if (client.Project)
                foreach (var p in library.Projects)
                {
                    var toggle = PdUi.LabeledToggle(draft.Targets.Contains((client.Kind, p.Path)), on => Toggle(draft, client.Kind, p.Path, on), p.Name);
                    toggle.IsEnabled = p.Exists;
                    line.Children.Add(toggle);
                }
            var head = ClientUi.HRow(8, PdUi.Text(SkillClientNames.Of(client.Kind), 12.5, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
            if (!client.Detected) head.Children.Add(KitBadge.Create(Loc.T("library.undetected")));
            targets.Children.Add(new StackPanel { Spacing = 2, Children = { head, line } });
        }
        form.Children.Add(new FieldBlock(Loc.T("library.targets"), targets, Loc.T(library.Projects.Count > 0 ? "library.targetsHint" : "library.targetsHintNoProjects")));
        form.Children.Add(PdUi.LabeledToggle(draft.Enabled, v => draft.Enabled = v, Loc.T("library.enabled")));

        var sheet = new Sheet
        {
            Title = existing is null ? Loc.T("library.addTitle." + kind) : Loc.T("library.editTitle", ("name", existing.Name)),
            SheetWidth = 580,
            Content = new ScrollViewer { MaxHeight = 640, Content = form },
            Footer = [cancel, save],
        };
        cancel.Click += (_, _) => sheet.Close();
        save.Click += async (_, _) =>
        {
            save.IsLoading = true;
            try
            {
                var saved = await api.SaveAsync(segment, existing?.Id, draft.ToBody());
                var problems = saved.Targets.Any(t => t.State is "conflict" or "drifted") || saved.LastError is { Count: > 0 };
                if (problems) feedback.Error(Loc.T("library.toast.savedWithProblems"));
                else feedback.Success(Loc.T("library.toast.saved", ("name", saved.Name)));
                sheet.Close();
            }
            catch (ApiException e)
            {
                feedback.Error(Loc.T("common.error"), e.Message);
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

    private static void Toggle(ResourceDraft draft, string client, string project, bool on)
    {
        if (on) draft.Targets.Add((client, project));
        else draft.Targets.Remove((client, project));
    }
}
