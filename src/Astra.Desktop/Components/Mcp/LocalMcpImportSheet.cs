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
/// "Import existing" (web <c>useLocalMcpCandidates</c> / <c>useImportLocalMcp</c>): the MCP entries the clients already have that Astra did
/// not write. Importing takes one over — Astra manages it from then on, and removing it from Astra puts the user's own entry back.
/// </summary>
public static class LocalMcpImportSheet
{
    /// <summary>Opens the sheet and loads the candidates.</summary>
    public static Sheet Show(Visual owner, ClientMcpApi api, IFeedback feedback)
    {
        var chosen = new HashSet<LocalMcpCandidateDto>();
        var body = new StackPanel { Spacing = 8, Children = { new LoadingState(2) } };
        var run = Ui.Button(Loc.T("mcp.local.importRun"), () => { }, ButtonVariant.Primary);
        run.IsEnabled = false;
        var close = Ui.Button(Loc.T("common.close"), () => { });
        var sheet = new Sheet
        {
            Title = Loc.T("mcp.local.importTitle"),
            Description = Loc.T("mcp.local.importHint"),
            SheetWidth = 600,
            Content = new ScrollViewer { MaxHeight = 420, Content = body },
            Footer = [close, run],
        };
        close.Click += (_, _) => sheet.Close();
        run.Click += async (_, _) =>
        {
            run.IsLoading = true;
            try
            {
                var result = await api.ImportAsync(chosen);
                feedback.Success(Loc.T("mcp.local.importDone", ("count", result.Imported.Count)));
                if (result.Skipped.Count > 0)
                    feedback.Info(Loc.T("mcp.local.importSkipped", ("count", result.Skipped.Count), ("reasons", string.Join("; ", result.Skipped.Select(s => $"{s.Name}: {s.Reason}")))));
                sheet.Close();
            }
            catch (ApiException e)
            {
                feedback.Error(Loc.T("common.error"), e.Message);
            }
            finally
            {
                run.IsLoading = false;
            }
        };
        _ = LoadAsync();
        _ = sheet.ShowAsync(owner);
        return sheet;

        async Task LoadAsync()
        {
            try
            {
                var candidates = await api.CandidatesAsync();
                body.Children.Clear();
                body.Children.Add(PdUi.Text(Loc.T("mcp.local.importSecrets"), 11.5, ArcDesignTokenKind.TextMuted, wrap: true));
                if (candidates.Count == 0) body.Children.Add(PdUi.Text(Loc.T("mcp.local.importEmpty"), 12, ArcDesignTokenKind.TextSecondary));
                foreach (var c in candidates)
                {
                    var importable = c.Importable && !c.Taken;
                    var toggle = PdUi.LabeledToggle(false, on =>
                    {
                        if (on) chosen.Add(c);
                        else chosen.Remove(c);
                        run.IsEnabled = chosen.Count > 0;
                    }, $"{LocalMcpDraft.ClientName(c.Kind)} · {c.Name}");
                    toggle.IsEnabled = importable;
                    var line = new StackPanel { Spacing = 2, Children = { toggle } };
                    var note = importable ? c.Summary : c.Taken ? Loc.T("mcp.local.importTaken") : c.Reason ?? "";
                    if (note.Length > 0) line.Children.Add(PdUi.Text(note, 11.5, ArcDesignTokenKind.TextMuted, mono: importable, wrap: true));
                    body.Children.Add(line);
                }
            }
            catch (ApiException e)
            {
                body.Children.Clear();
                body.Children.Add(ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), e.Message));
            }
        }
    }
}
