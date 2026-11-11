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

namespace Astra.Desktop.Components.Skills;

/// <summary>
/// Installs Skills from a Git repository or a folder (web <c>SkillInstallSheet</c>): look into the source first, pick, then install and
/// optionally switch clients on. Only offered on the machine Astra runs on (the server refuses it elsewhere).
/// </summary>
public static class SkillInstallSheet
{
    /// <summary>Opens the sheet.</summary>
    public static Sheet Show(Visual owner, SkillsApi api, IFeedback feedback, SkillLibraryDto library)
    {
        var kind = "git";
        string source = "", gitRef = "";
        SkillProbeResultDto? found = null;
        var picked = new HashSet<string>(StringComparer.Ordinal);
        var clients = new HashSet<string>(StringComparer.Ordinal);
        var error = new ContentControl();

        var install = Ui.Button(Loc.T("skills.install.submit", ("n", 0)), () => { }, ButtonVariant.Primary);
        install.IsEnabled = false;
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });
        var look = Ui.Button(Loc.T("skills.install.probe"), () => { }, ButtonVariant.Secondary);
        look.IsEnabled = false;

        void Refresh()
        {
            install.Content = Loc.T("skills.install.submit", ("n", picked.Count));
            install.IsEnabled = found is not null && picked.Count > 0;
            look.IsEnabled = source.Trim().Length > 0;
        }

        var sourceBox = PdUi.TextBox(source, v => { source = v; Refresh(); }, mono: true, name: Loc.T("skills.install.repo"), placeholder: "https://github.com/owner/skills");
        sourceBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var refBox = PdUi.TextBox(gitRef, v => gitRef = v, name: Loc.T("skills.install.ref"), placeholder: Loc.T("skills.install.refPlaceholder"));
        refBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var dirBox = PdUi.TextBox("", v => { source = v; Refresh(); }, mono: true, name: Loc.T("skills.install.dir"), placeholder: "/Users/me/skills");
        dirBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var sourceField = new FieldBlock(Loc.T("skills.install.repo"), sourceBox, Loc.T("skills.install.repoHint"));
        var dirField = new FieldBlock(Loc.T("skills.install.dir"), dirBox) { IsVisible = false };
        var refField = new FieldBlock(Loc.T("skills.install.ref"), refBox);

        var results = new StackPanel { Spacing = 16 };

        var kinds = Pick.Segmented(
            [new SegmentedOption("git", Loc.T("skills.install.kind.git")), new SegmentedOption("local", Loc.T("skills.install.kind.local"))],
            kind, v =>
            {
                kind = v;
                found = null;
                results.Children.Clear();
                refField.IsVisible = kind == "git";
                sourceField.IsVisible = kind == "git";
                dirField.IsVisible = kind == "local";
                source = kind == "git" ? sourceBox.Text ?? "" : dirBox.Text ?? "";
                Refresh();
            }, Loc.T("skills.install.title"));
        kinds.HorizontalAlignment = HorizontalAlignment.Left;

        void ShowFound(SkillProbeResultDto result)
        {
            results.Children.Clear();
            var rows = new StackPanel { Spacing = 0 };
            foreach (var c in result.Skills)
            {
                var usable = c.Status is "new" or "exists";
                var check = new Checkbox { IsChecked = picked.Contains(c.Name), IsEnabled = usable, VerticalAlignment = VerticalAlignment.Center };
                var name = c.Name;
                check.IsCheckedChanged += (_, _) =>
                {
                    if (check.IsChecked == true) picked.Add(name);
                    else picked.Remove(name);
                    Refresh();
                };
                var title = ClientUi.HRow(8, check, PdUi.Text(c.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
                if (c.Status != "new")
                    title.Children.Add(KitBadge.Create(Loc.T("skills.status." + c.Status) + (c.Reason is { Length: > 0 } r ? " · " + Loc.T("skills.reason." + (r == "size" ? "size" : "name")) : ""),
                        c.Status == "exists" ? Tone.Green : Tone.Orange));
                var row = new StackPanel { Spacing = 2, Margin = new Thickness(12, 8), Opacity = usable ? 1 : 0.6 };
                row.Children.Add(title);
                if (c.Description.Length > 0) row.Children.Add(PdUi.Text(c.Description, 11.5, ArcDesignTokenKind.TextSecondary, wrap: true));
                rows.Children.Add(row);
            }
            results.Children.Add(new FieldBlock(Loc.T("skills.install.found", ("n", result.Skills.Count)), PdUi.Card(rows)));

            var targets = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 8 };
            foreach (var t in library.Targets.Where(t => t.Supported && t.Detected))
            {
                var check = new Checkbox { Content = SkillClientNames.Of(t.Kind) + (t.ReadsShared ? " · " + Loc.T("skills.badge.shared") : "") };
                var clientKind = t.Kind;
                check.IsCheckedChanged += (_, _) =>
                {
                    if (check.IsChecked == true) clients.Add(clientKind);
                    else clients.Remove(clientKind);
                };
                targets.Children.Add(check);
            }
            results.Children.Add(new FieldBlock(Loc.T("skills.install.apply"), targets));
        }

        var form = new StackPanel { Spacing = 16, Children = { kinds, sourceField, dirField, refField, look, error, results } };
        var sheet = new Sheet
        {
            Title = Loc.T("skills.install.title"),
            Description = Loc.T("skills.install.localOnly"),
            SheetWidth = 540,
            Content = new ScrollViewer { MaxHeight = 560, Content = form },
            Footer = [cancel, install],
        };
        cancel.Click += (_, _) => sheet.Close();

        look.Click += async (_, _) =>
        {
            look.IsLoading = true;
            error.Content = null;
            found = null;
            results.Children.Clear();
            try
            {
                var result = await api.ProbeAsync(kind, source, kind == "git" ? gitRef : null);
                found = result;
                picked.Clear();
                foreach (var c in result.Skills.Where(c => c.Status == "new")) picked.Add(c.Name);
                ShowFound(result);
                Refresh();
            }
            catch (ApiException e)
            {
                error.Content = Ui.ErrorText(e.Message);
            }
            finally
            {
                look.IsLoading = false;
            }
        };

        install.Click += async (_, _) =>
        {
            if (found is null) return;
            install.IsLoading = true;
            try
            {
                var outcomes = await api.InstallAsync(found.ProbeId, picked, clients);
                var failed = outcomes.Where(o => !o.Ok).ToList();
                if (failed.Count == 0) feedback.Success(Loc.T("skills.toast.installed", ("n", outcomes.Count)));
                else feedback.Error(Loc.T("skills.toast.partial", ("ok", outcomes.Count - failed.Count), ("failed", failed.Count), ("reason", failed[0].Reason ?? "")));
                sheet.Close();
            }
            catch (ApiException e)
            {
                error.Content = Ui.ErrorText(e.Message);
            }
            finally
            {
                install.IsLoading = false;
            }
        };
        _ = sheet.ShowAsync(owner);
        return sheet;
    }
}
