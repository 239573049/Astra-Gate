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
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Mcp;

/// <summary>
/// The add / edit sheet of an upstream MCP server (web <c>ServerSheet</c>). A stored header value is never shown: an existing header reads
/// "keep current value" until a new one is typed, and is then sent as null — which the server reads as "keep what is stored".
/// </summary>
public static class McpServerSheet
{
    private sealed class HeaderDraft(string name, bool kept)
    {
        public string Name { get; set; } = name;
        public string Value { get; set; } = "";
        public bool Kept { get; } = kept;
    }

    /// <summary>Opens the sheet; <paramref name="existing"/> null = a new server.</summary>
    public static Sheet Show(Visual owner, McpApi api, IFeedback feedback, McpServerDto? existing)
    {
        string name = existing?.Name ?? "", slug = existing?.Slug ?? "", url = existing?.Url ?? "";
        var transport = existing?.Transport ?? "http";
        var authType = existing?.AuthType ?? "none";
        var enabled = existing?.Enabled ?? true;
        var timeout = existing?.TimeoutSeconds ?? 60;
        var headers = (existing?.HeaderNames ?? []).Select(n => new HeaderDraft(n, kept: true)).ToList();

        var save = Ui.Button(Loc.T("common.save"), () => { }, ButtonVariant.Primary);
        var cancel = Ui.Button(Loc.T("common.cancel"), () => { });

        bool Valid() => name.Trim().Length > 0 && url.Trim().Length > 0
            && (authType != "header" || headers.Any(h => h.Name.Trim().Length > 0 && (h.Value.Length > 0 || h.Kept)));
        void Check() => save.IsEnabled = Valid();

        var nameBox = PdUi.TextBox(name, v => { name = v; Check(); }, name: Loc.T("mcp.server.name"));
        nameBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var urlBox = PdUi.TextBox(url, v => { url = v; Check(); }, mono: true, name: Loc.T("mcp.server.url"), placeholder: "https://mcp.example.com/mcp");
        urlBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        var slugBox = PdUi.TextBox(slug, v => slug = v, mono: true, name: Loc.T("mcp.server.slug"), placeholder: existing is null ? "github" : null);
        slugBox.HorizontalAlignment = HorizontalAlignment.Stretch;

        var headerHost = new StackPanel { Spacing = 8 };
        var oauthNote = ClientUi.Alert(AlertTone.Warning, Loc.T("mcp.server.oauthNote"));
        var headersField = new FieldBlock(Loc.T("mcp.server.headers"), headerHost, Loc.T("mcp.server.headersHint"));

        void RebuildHeaders()
        {
            headerHost.Children.Clear();
            foreach (var h in headers.ToList())
            {
                var headerName = PdUi.TextBox(h.Name, v => { h.Name = v; Check(); }, 160, mono: true, name: Loc.T("mcp.server.headerName"), placeholder: "Authorization");
                headerName.IsReadOnly = h.Kept && existing is not null;
                var value = PdUi.Secret(h.Value, v => { h.Value = v; Check(); }, 260, Loc.T("mcp.server.headerValue"), h.Kept ? Loc.T("mcp.server.headerKept") : "Bearer …");
                var remove = Ui.IconButton(LucideIconKind.Trash2, Loc.T("mcp.server.removeHeader"), () =>
                {
                    headers.Remove(h);
                    RebuildHeaders();
                    Check();
                });
                headerHost.Children.Add(ClientUi.HRow(8, headerName, value, remove));
            }
            headerHost.Children.Add(Ui.Button(Loc.T("mcp.server.addHeader"), () =>
            {
                headers.Add(new HeaderDraft("", kept: false));
                RebuildHeaders();
                Check();
            }, ButtonVariant.Secondary, LucideIconKind.Plus));
        }

        void ShowAuth()
        {
            headersField.IsVisible = authType == "header";
            oauthNote.IsVisible = authType == "oauth";
            Check();
        }

        var transportPick = Pick.Segmented(
            [new SegmentedOption("http", Loc.T("mcp.server.transport.http")), new SegmentedOption("sse", Loc.T("mcp.server.transport.sse"))],
            transport, v => transport = v, Loc.T("mcp.server.transport"));
        transportPick.HorizontalAlignment = HorizontalAlignment.Left;
        var authPick = Pick.Segmented(
            [new SegmentedOption("none", Loc.T("mcp.server.auth.none")), new SegmentedOption("header", Loc.T("mcp.server.auth.header")), new SegmentedOption("oauth", Loc.T("mcp.server.auth.oauth"))],
            authType, v => { authType = v; ShowAuth(); }, Loc.T("mcp.server.auth"));
        authPick.HorizontalAlignment = HorizontalAlignment.Left;

        var timeoutBox = PdUi.Number(timeout, v => timeout = v is { } d ? (int)d : 60, 120, Loc.T("mcp.server.timeout"), integer: true, minimum: 1);
        var enabledToggle = PdUi.LabeledToggle(enabled, v => enabled = v, Loc.T("mcp.server.enabled"));

        RebuildHeaders();
        ShowAuth();
        var form = new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new FieldBlock(Loc.T("mcp.server.name"), nameBox),
                new FieldBlock(Loc.T("mcp.server.url"), urlBox, Loc.T("mcp.server.urlHint")),
                new FieldBlock(Loc.T("mcp.server.transport"), transportPick, Loc.T("mcp.server.transportHint")),
                new FieldBlock(Loc.T("mcp.server.auth"), authPick),
                oauthNote,
                headersField,
                new FieldBlock(Loc.T("mcp.server.slug"), slugBox, Loc.T("mcp.server.slugHint")),
                new FieldBlock(Loc.T("mcp.server.timeout"), timeoutBox, Loc.T("mcp.server.timeoutHint")),
                enabledToggle,
            },
        };

        var sheet = new Sheet
        {
            Title = existing is null ? Loc.T("mcp.server.addTitle") : Loc.T("mcp.server.editTitle", ("name", existing.Name)),
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
                var body = McpApi.ServerBody(name, slug, url, transport, authType, enabled, timeout,
                    headers.Select(h => new KeyValuePair<string, string?>(h.Name, h.Kept && h.Value.Length == 0 ? null : h.Value)));
                var saved = existing is null ? await api.CreateAsync(body) : await api.UpdateAsync(existing.Id, body);
                feedback.Success(Loc.T(existing is null ? "mcp.server.created" : "mcp.server.saved", ("name", saved.Name)));
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
