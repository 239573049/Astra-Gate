using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.Connect;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Clients;

/// <summary>What the connect sheet needs: the connect and client APIs and the toasts.</summary>
public sealed record ConnectSheetContext(ConnectApi Connect, ClientsApi Clients, IFeedback Feedback);

/// <summary>
/// The "connect to a unified gateway" sheet (the employee's side of the join links): confirm a link (or enter the gateway and a key), let the
/// local server verify it and keep the gateway as a provider, pick the clients to send through it, and report each client. Nothing is
/// exchanged before the person confirms; the key goes from the gateway to the local server and never shows here.
/// </summary>
public static class ConnectSheets
{
    /// <summary>Opens the sheet; <paramref name="link"/> is the join link the person opened, null = the manual entry (gateway + key).</summary>
    public static Sheet Show(Visual owner, ConnectSheetContext ctx, JoinLink? link)
    {
        var body = new ContentControl();
        var sheet = new Sheet { Title = Loc.T("connect.title"), SheetWidth = 520, Content = body };
        _ = sheet.ShowAsync(owner);
        if (link is not null) ShowLink(link); else ShowManual(null);
        return sheet;

        void Footer(params Control[] buttons) => sheet.Footer = buttons;
        TKCode.Arc.Button Cancel(string? text = null) => ClientUi.Button(text ?? Loc.T("common.cancel"), () => sheet.Close());

        // ---- step 1a: a link the person opened
        void ShowLink(JoinLink l)
        {
            sheet.Description = Loc.T("connect.link.detail");
            var problem = new ContentControl();
            var go = ClientUi.Button(Loc.T("connect.link.continue"), null, ButtonVariant.Primary);
            var manual = ClientUi.Button(Loc.T("connect.useKey"), () => ShowManual(l.GatewayUrl));
            body.Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    ClientUi.CodeBox(l.Display, 13),
                    ClientUi.Alert(AlertTone.Warning, Loc.T("connect.link.trust")),
                    problem,
                },
            };
            go.Click += async (_, _) =>
            {
                go.IsLoading = true;
                problem.Content = null;
                try
                {
                    ShowPick(await ctx.Connect.JoinAsync(l));
                }
                catch (ApiException e)
                {
                    problem.Content = ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), Loc.T(ConnectApi.JoinProblemKey(e)));
                }
                finally
                {
                    go.IsLoading = false;
                }
            };
            Footer(Cancel(), manual, go);
        }

        // ---- step 1b: the gateway and the employee's own key
        void ShowManual(string? gateway)
        {
            sheet.Description = Loc.T("connect.manual.detail");
            string url = gateway ?? "", key = "";
            var problem = new ContentControl();
            var go = ClientUi.Button(Loc.T("connect.verify"), null, ButtonVariant.Primary);
            var urlBox = PdUi.TextBox(url, v => url = v, mono: true, name: Loc.T("connect.gateway"), placeholder: "https://astra.example.com");
            urlBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            var keyBox = PdUi.Secret("", v => key = v, 440, Loc.T("connect.key"));
            keyBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            body.Content = PdUi.Stack(14,
                PdUi.Field(Loc.T("connect.gateway"), urlBox),
                PdUi.Field(Loc.T("connect.key"), keyBox, Loc.T("connect.keyHint")),
                problem);
            go.Click += async (_, _) =>
            {
                if (url.Trim().Length == 0 || key.Trim().Length == 0) return;
                go.IsLoading = true;
                problem.Content = null;
                try
                {
                    ShowPick(await ctx.Connect.JoinWithKeyAsync(url.Trim(), key.Trim()));
                }
                catch (ApiException e)
                {
                    problem.Content = ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), e.Status == 400 ? e.Message : Loc.T(ConnectApi.JoinProblemKey(e)));
                }
                finally
                {
                    go.IsLoading = false;
                }
            };
            Footer(Cancel(), go);
        }

        // ---- step 2: the clients to send through the gateway
        void ShowPick(ConnectJoinResultDto joined)
        {
            sheet.Title = Loc.T("connect.pick.title");
            sheet.Description = Loc.T("connect.pick.detail");
            var clients = (ctx.Clients.Clients().Data ?? []).Where(c => c.Availability == "available").ToList();
            var chosen = clients.Where(c => c.Detection.Installed || c.Enabled).Select(c => c.Kind).ToHashSet(StringComparer.Ordinal);
            var apply = ClientUi.Button(null, null, ButtonVariant.Primary);
            void Relabel()
            {
                apply.Content = Loc.T("connect.pick.apply", ("n", chosen.Count));
                apply.IsEnabled = chosen.Count > 0;
            }
            var list = new StackPanel { Spacing = 6 };
            foreach (var c in clients)
            {
                var box = new Checkbox { IsChecked = chosen.Contains(c.Kind), Content = c.Name, VerticalAlignment = VerticalAlignment.Center };
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true) chosen.Add(c.Kind); else chosen.Remove(c.Kind);
                    Relabel();
                };
                list.Children.Add(box);
            }
            var stack = new StackPanel { Spacing = 12 };
            stack.Children.Add(ClientUi.Alert(AlertTone.Success, Loc.T("connect.joined", ("name", joined.TokenName), ("count", joined.Models.Count))));
            if (joined.Insecure) stack.Children.Add(ClientUi.Alert(AlertTone.Warning, Loc.T("connect.insecure")));
            stack.Children.Add(clients.Count == 0 ? ClientUi.Text(Loc.T("connect.pick.none"), 13, ArcDesignTokenKind.TextSecondary) : list);
            body.Content = stack;
            Relabel();
            apply.Click += async (_, _) =>
            {
                apply.IsLoading = true;
                try
                {
                    ShowDone(await ctx.Connect.ApplyAsync(chosen), clients);
                }
                catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
                {
                    ctx.Feedback.Error(e.Message);
                }
                finally
                {
                    apply.IsLoading = false;
                }
            };
            Footer(Cancel(Loc.T("connect.pick.skip")), apply);
        }

        // ---- step 3: what happened to each client
        void ShowDone(ConnectApplyResultDto result, List<ClientInfoDto> clients)
        {
            sheet.Title = Loc.T("connect.done.title");
            sheet.Description = null;
            string NameOf(string kind) => clients.FirstOrDefault(c => c.Kind == kind)?.Name ?? kind;
            var stack = new StackPanel { Spacing = 8 };
            foreach (var r in result.Results)
                stack.Children.Add(r.Ok
                    ? ClientUi.Alert(AlertTone.Success, Loc.T("connect.done.ok", ("name", NameOf(r.Kind))))
                    : ClientUi.Alert(AlertTone.Danger, Loc.T("connect.done.failed", ("name", NameOf(r.Kind)), ("error", r.Error ?? ""))));
            if (result.Results.Any(r => r.Ok)) stack.Children.Add(ClientUi.Text(Loc.T("connect.done.restart"), 12, ArcDesignTokenKind.TextSecondary));
            body.Content = stack;
            Footer(ClientUi.Button(Loc.T("common.done"), () => sheet.Close(), ButtonVariant.Primary));
        }
    }
}
