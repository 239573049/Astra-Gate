using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Library;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Skills;

/// <summary>
/// Previews what moving Skills into Astra will do (web <c>SkillMigrateSheet</c>) — nothing changes until the button — and asks which copy to
/// keep when copies of one name differ. A Skill the library already has is migrated to the library's version (the others are backed up).
/// </summary>
public static class SkillMigrateSheet
{
    /// <summary>Opens the sheet for <paramref name="names"/>.</summary>
    public static Sheet Show(Visual owner, SkillsApi api, IFeedback feedback, IReadOnlyList<string> names, string libraryDir)
    {
        var items = new List<SkillMigratePreviewItemDto>();
        var keep = new Dictionary<string, string>(StringComparer.Ordinal);
        var host = new StackPanel { Spacing = 16 };
        host.Children.Add(new LoadingState(3));
        var error = new ContentControl();

        var migrate = Ui.Button(Loc.T("skills.migrate.submit", ("n", 0)), () => { }, ButtonVariant.Primary);
        migrate.IsEnabled = false;
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });

        List<SkillMigratePreviewItemDto> Ready() => items.Where(i => !i.Conflict || keep.ContainsKey(i.Name)).ToList();
        void Refresh()
        {
            var ready = Ready();
            migrate.Content = Loc.T("skills.migrate.submit", ("n", ready.Count));
            migrate.IsEnabled = ready.Count > 0 && items.All(i => !i.Conflict || keep.ContainsKey(i.Name));
        }

        void Draw()
        {
            host.Children.Clear();
            foreach (var item in items)
            {
                var body = new StackPanel { Spacing = 0 };
                if (item.Conflict)
                {
                    body.Children.Add(Pad(PdUi.Text(Loc.T("skills.migrate.keep"), 12, ArcDesignTokenKind.TextSecondary, wrap: true)));
                    var options = item.Locations.Select(l => (l.Path, $"{l.Path}  ·  {Loc.T("skills.migrate.modified", ("time", SettingsFormat.DateTime(l.Modified, Loc.Language)))}"));
                    var pick = Pick.Select(options, keep.GetValueOrDefault(item.Name), v => { keep[item.Name] = v; Refresh(); }, item.Name, Loc.T("skills.migrate.needsChoice"));
                    pick.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                    body.Children.Add(Pad(pick));
                    body.Children.Add(Pad(PdUi.Text(Loc.T("skills.migrate.keepHint"), 11, ArcDesignTokenKind.TextMuted, wrap: true)));
                }
                else
                {
                    foreach (var l in item.Locations)
                        body.Children.Add(Pad(ClientUi.HRow(8, PdUi.Text(l.Path, 12, ArcDesignTokenKind.Foreground, mono: true),
                            KitBadge.Create(Loc.T(item.InLibrary ? "skills.found.inLibrary" : "skills.migrate.noConflict"), Tone.Green))));
                    if (item.InLibrary) body.Children.Add(Pad(PdUi.Text(Loc.T("skills.migrate.inLibrary"), 11, ArcDesignTokenKind.TextMuted, wrap: true)));
                }
                host.Children.Add(new FieldBlock(item.Name, PdUi.Card(body)));
            }
            if (items.Count > 0)
                host.Children.Add(PdUi.Text(Loc.T("skills.migrate.backup", ("dir", BackupsDir(libraryDir))), 11, ArcDesignTokenKind.TextMuted, wrap: true));
            Refresh();
        }

        var sheet = new Sheet
        {
            Title = Loc.T("skills.migrate.title"),
            Description = Loc.T("skills.migrate.lead"),
            SheetWidth = 580,
            Content = new ScrollViewer { MaxHeight = 560, Content = new StackPanel { Spacing = 12, Children = { error, host } } },
            Footer = [cancel, migrate],
        };
        cancel.Click += (_, _) => sheet.Close();
        migrate.Click += async (_, _) =>
        {
            migrate.IsLoading = true;
            try
            {
                var outcomes = await api.MigrateAsync(Ready().Select(i => new SkillMigrateItemDto(i.Name, i.InLibrary ? "library" : keep.GetValueOrDefault(i.Name))));
                var failed = outcomes.Where(o => !o.Ok).ToList();
                if (failed.Count == 0) feedback.Success(Loc.T("skills.toast.migrated", ("n", outcomes.Count)));
                else feedback.Error(Loc.T("skills.toast.partial", ("ok", outcomes.Count - failed.Count), ("failed", failed.Count), ("reason", failed[0].Reason ?? "")));
                sheet.Close();
            }
            catch (ApiException e)
            {
                error.Content = Ui.ErrorText(e.Message);
            }
            finally
            {
                migrate.IsLoading = false;
            }
        };

        _ = LoadAsync();
        _ = sheet.ShowAsync(owner);
        return sheet;

        async Task LoadAsync()
        {
            try
            {
                items = await api.MigratePreviewAsync(names);
                Draw();
            }
            catch (ApiException e)
            {
                host.Children.Clear();
                error.Content = Ui.ErrorText(e.Message);
            }
        }
    }

    private static Control Pad(Control c) => PdUi.Pad(c, 12, 8);

    /// <summary>…/library/skills → …/backups/library, where the originals are kept.</summary>
    private static string BackupsDir(string libraryDir)
    {
        var skills = libraryDir.TrimEnd('/', '\\');
        var root = Path.GetDirectoryName(Path.GetDirectoryName(skills)) ?? skills;
        return Path.Combine(root, "backups", "library");
    }
}
