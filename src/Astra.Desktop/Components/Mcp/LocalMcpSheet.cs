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
using Avalonia.Layout;
using TKCode.Arc;

namespace Astra.Desktop.Components.Mcp;

/// <summary>
/// The add / edit sheet of one of the user's own MCP servers (web <c>LocalSheet</c>). Stored environment and header values are never
/// shown: an existing server's boxes start blank with the stored names in the hint, and blank keeps what is stored.
/// </summary>
public static class LocalMcpSheet
{
    /// <summary>Opens the sheet; <paramref name="existing"/> null = a new definition.</summary>
    public static Sheet Show(Visual owner, ClientMcpApi api, IFeedback feedback, LocalMcpServerDto? existing)
    {
        var draft = existing is null ? new LocalMcpDraft() : new LocalMcpDraft(existing);
        var save = Ui.Button(Loc.T("common.save"), () => { }, ButtonVariant.Primary);
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });

        var stdio = new StackPanel { Spacing = 16 };
        var http = new StackPanel { Spacing = 16 };
        var envInvalid = ClientUi.Alert(AlertTone.Warning, Loc.T("mcp.local.envInvalid"));
        var headersInvalid = ClientUi.Alert(AlertTone.Warning, Loc.T("mcp.local.headersInvalid"));

        void Check()
        {
            save.IsEnabled = draft.IsValid;
            envInvalid.IsVisible = draft.Transport == "stdio" && LocalMcpDraft.ParseEnv(draft.Env) is null;
            headersInvalid.IsVisible = draft.Transport == "http" && LocalMcpDraft.ParseHeaders(draft.Headers) is null;
        }

        void ShowTransport()
        {
            stdio.IsVisible = draft.Transport == "stdio";
            http.IsVisible = draft.Transport == "http";
            Check();
        }

        var name = PdUi.TextBox(draft.Name, v => { draft.Name = v; Check(); }, mono: true, name: Loc.T("mcp.local.name"), placeholder: "filesystem");
        name.HorizontalAlignment = HorizontalAlignment.Stretch;
        var command = PdUi.TextBox(draft.Command, v => { draft.Command = v; Check(); }, mono: true, name: Loc.T("mcp.local.command"), placeholder: "npx");
        command.HorizontalAlignment = HorizontalAlignment.Stretch;
        var args = PdUi.Area(draft.Args, v => draft.Args = v, null, 96, mono: true, placeholder: "-y\n@modelcontextprotocol/server-filesystem\n/Users/me/docs");
        var env = PdUi.Area(draft.Env, v => { draft.Env = v; Check(); }, null, 72, mono: true, placeholder: "API_KEY=...");
        var url = PdUi.TextBox(draft.Url, v => { draft.Url = v; Check(); }, mono: true, name: Loc.T("mcp.local.url"), placeholder: "https://mcp.example.com/mcp");
        url.HorizontalAlignment = HorizontalAlignment.Stretch;
        var headers = PdUi.Area(draft.Headers, v => { draft.Headers = v; Check(); }, null, 72, mono: true, placeholder: "Authorization: Bearer ...");

        var envHint = existing is { EnvNames.Count: > 0 }
            ? Loc.T("mcp.local.envKept", ("names", string.Join(", ", existing.EnvNames)))
            : Loc.T("mcp.local.envHint");
        var headersHint = existing is { HeaderNames.Count: > 0 }
            ? Loc.T("mcp.local.headersKept", ("names", string.Join(", ", existing.HeaderNames)))
            : Loc.T("mcp.local.headersHint");

        stdio.Children.Add(new FieldBlock(Loc.T("mcp.local.command"), command));
        stdio.Children.Add(new FieldBlock(Loc.T("mcp.local.args"), args, Loc.T("mcp.local.argsHint")));
        stdio.Children.Add(new FieldBlock(Loc.T("mcp.local.env"), env, envHint));
        stdio.Children.Add(envInvalid);
        http.Children.Add(new FieldBlock(Loc.T("mcp.local.url"), url, Loc.T("mcp.local.urlHint")));
        http.Children.Add(new FieldBlock(Loc.T("mcp.local.headers"), headers, headersHint));
        http.Children.Add(headersInvalid);

        var transport = Pick.Segmented(
            [new SegmentedOption("stdio", Loc.T("mcp.local.transport.stdio")), new SegmentedOption("http", Loc.T("mcp.local.transport.http"))],
            draft.Transport, v => { draft.Transport = v; ShowTransport(); }, Loc.T("mcp.local.transport"));
        transport.HorizontalAlignment = HorizontalAlignment.Left;

        var clients = new StackPanel { Spacing = 8 };
        foreach (var (kind, clientName) in LocalMcpDraft.Clients)
        {
            var k = kind;
            clients.Children.Add(PdUi.LabeledToggle(draft.ChosenClients.Contains(k), on =>
            {
                if (on) draft.ChosenClients.Add(k);
                else draft.ChosenClients.Remove(k);
            }, clientName));
        }

        ShowTransport();
        var form = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new FieldBlock(Loc.T("mcp.local.transport"), transport),
                new FieldBlock(Loc.T("mcp.local.name"), name, Loc.T("mcp.local.nameHint")),
                stdio,
                http,
                new FieldBlock(Loc.T("mcp.local.clients"), clients, Loc.T("mcp.local.clientsHint")),
                PdUi.LabeledToggle(draft.Enabled, v => draft.Enabled = v, Loc.T("mcp.local.enabled")),
            },
        };

        var sheet = new Sheet
        {
            Title = existing is null ? Loc.T("mcp.local.addTitle") : Loc.T("mcp.local.editTitle", ("name", existing.Name)),
            SheetWidth = 560,
            Content = form,
            Footer = [cancel, save],
        };
        cancel.Click += (_, _) => sheet.Close();
        save.Click += async (_, _) =>
        {
            save.IsLoading = true;
            try
            {
                var body = draft.ToBody();
                var saved = existing is null ? await api.CreateAsync(body) : await api.UpdateAsync(existing.Id, body);
                feedback.Success(Loc.T(existing is null ? "mcp.local.created" : "mcp.local.saved", ("name", saved.Name)));
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
}
