using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Accounts;

/// <summary>
/// The login wizard of a subscription provider (web SubscriptionAccounts.tsx, the "login" sheet): what to do next for the login under way —
/// paste the code the authorization page shows (Claude), wait for the browser callback (PKCE, with "I authorized" to look now), wait for
/// the server-mediated CLI flow, or type the device code on the verification page (with "check now"). The last failure stays on top so a
/// finished authorization never looks like it is still waiting. The model polls; this only draws it.
/// </summary>
internal sealed class LoginSheet : Sheet
{
    private readonly SubscriptionAccountsModel _model;
    private readonly Input _paste = new() { IsLabelVisible = false };
    private string _signature = "";

    /// <summary>Creates the (closed) wizard.</summary>
    public LoginSheet(SubscriptionAccountsModel model)
    {
        _model = model;
        _paste.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _paste.TextChanged += (_, _) => _model.Pasted = _paste.Text ?? "";
    }

    /// <summary>Brings the sheet in line with the model (open state, mode, error, busy flags).</summary>
    public void Refresh()
    {
        if (!_model.LoginOpen)
        {
            _signature = "";
            if (IsOpen) IsOpen = false;
            return;
        }
        // Redraw only for a change of what is shown: typing the code must not move the focused box.
        var signature = string.Join('|', _model.Login, _model.LoginError, _model.Checking, Loc.Language);
        if (signature == _signature && IsOpen) return;
        _signature = signature;
        Title = Loc.T("providers.subscription.login");
        Description = Loc.T("providers.subscription.loginDesc");
        var stack = new StackPanel { Spacing = 12 };
        if (_model.LoginError is { } error)
            stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("providers.subscription.loginFailed"), Content = Body(error) });
        if (_model.Login is { } login) stack.Children.Add(Mode(login));
        Content = stack;
        if (!IsOpen) IsOpen = true;
    }

    private static TextBlock Body(string text) => AccountsUi.Text(text, 13, wrap: true);

    private Control Mode(LoginStart login)
    {
        var stack = new StackPanel { Spacing = 12 };
        ArcButton Open(string key, string? url) => AccountsUi.Button(Loc.T(key), () => { if (url is not null) _ = _model.OpenUrl(url); }, size: ControlSize.Medium);
        switch (login.Mode)
        {
            case "paste":
                stack.Children.Add(new Alert { Tone = AlertTone.Info, Title = Loc.T("providers.subscription.pasteTitle"), Content = Body(Loc.T("providers.subscription.pasteDetail")) });
                var openPaste = Open("providers.subscription.openAuthorize", login.AuthorizeUrl);
                openPaste.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(openPaste);
                _paste.Placeholder = Loc.T("providers.subscription.pastePlaceholder");
                _paste.Label = Loc.T("providers.subscription.pasteCode");
                if ((_paste.Text ?? "") != _model.Pasted) _paste.Text = _model.Pasted;
                (_paste.Parent as Panel)?.Children.Remove(_paste);
                stack.Children.Add(_paste);
                // The page usually shows "code#state": the whole text is accepted.
                var submit = AccountsUi.Button(Loc.T("providers.subscription.pasteSubmit"), () => _ = _model.SubmitPastedAsync(), ButtonVariant.Primary,
                    loading: _model.Checking, size: ControlSize.Medium);
                submit.HorizontalAlignment = HorizontalAlignment.Stretch;
                submit.HorizontalContentAlignment = HorizontalAlignment.Center;
                stack.Children.Add(submit);
                break;
            case "pkce":
                stack.Children.Add(new Alert { Tone = AlertTone.Info, Title = Loc.T("providers.subscription.waiting"), Content = Body(Loc.T("providers.subscription.waitingDetail")) });
                stack.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        Open("providers.subscription.openAuthorize", login.AuthorizeUrl),
                        AccountsUi.Button(Loc.T("providers.subscription.doneAuthorize"), () => _ = _model.RefreshForLoginAsync(), ButtonVariant.Primary, size: ControlSize.Medium),
                    },
                });
                break;
            case "cli":
                stack.Children.Add(new Alert { Tone = AlertTone.Info, Title = Loc.T("providers.subscription.waiting"), Content = Body(Loc.T("providers.subscription.cliDetail")) });
                var openCli = Open("providers.subscription.openAuthorize", login.AuthorizeUrl);
                openCli.HorizontalAlignment = HorizontalAlignment.Stretch;
                openCli.HorizontalContentAlignment = HorizontalAlignment.Center;
                stack.Children.Add(openCli);
                break;
            case "device":
                var code = new StackPanel
                {
                    Children =
                    {
                        AccountsUi.Text(Loc.T("providers.subscription.deviceCode"), 11, ArcDesignTokenKind.TextSecondary),
                        AccountsUi.Selectable(login.UserCode ?? "", 18, mono: true),
                    },
                };
                ((SelectableTextBlock)code.Children[1]).FontWeight = FontWeight.Medium;
                ((SelectableTextBlock)code.Children[1]).LetterSpacing = 0.45;
                stack.Children.Add(code);
                var row = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
                if (!string.IsNullOrEmpty(login.VerificationUrl)) row.Children.Add(Open("providers.subscription.visitPage", login.VerificationUrl));
                // No need to wait for the next poll once authorized: ask the server now.
                row.Children.Add(AccountsUi.Button(Loc.T("providers.subscription.checkNow"), () => _ = _model.CheckNowAsync(), ButtonVariant.Primary,
                    loading: _model.Checking, size: ControlSize.Medium));
                stack.Children.Add(row);
                stack.Children.Add(new Alert { Tone = AlertTone.Info, Title = Loc.T("providers.subscription.waiting"), Content = Body(Loc.T("providers.subscription.checkNowHint")) });
                break;
        }
        return stack;
    }
}

/// <summary>"Log in through a proxy" (web: the proxy-login sheet): the proxy becomes the new account's own one.</summary>
internal sealed class ProxyLoginSheet : Sheet
{
    private readonly SubscriptionAccountsModel _model;
    private readonly Input _proxy = new() { Placeholder = "socks5://127.0.0.1:1080" };
    private readonly ArcButton _start;

    /// <summary>Creates the (closed) sheet.</summary>
    public ProxyLoginSheet(SubscriptionAccountsModel model)
    {
        _model = model;
        _proxy.Token(Avalonia.Controls.Primitives.TemplatedControl.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _proxy.TextChanged += (_, _) =>
        {
            _model.LoginProxy = _proxy.Text ?? "";
            Buttons();
        };
        _start = AccountsUi.Button("", () => _ = _model.StartLoginAsync(null, _model.LoginProxy), ButtonVariant.Primary, size: ControlSize.Medium);
        Content = _proxy;
        Footer = [_start];
    }

    /// <summary>Brings the sheet in line with the model.</summary>
    public void Refresh()
    {
        Title = Loc.T("providers.accounts.loginProxyTitle");
        Description = Loc.T("providers.accounts.loginProxyDesc");
        _proxy.Label = Loc.T("providers.accounts.detail.proxy");
        _start.Content = Loc.T("providers.accounts.loginProxyStart");
        if ((_proxy.Text ?? "") != _model.LoginProxy) _proxy.Text = _model.LoginProxy;
        Buttons();
        if (_model.ProxyLoginOpen && !IsOpen) IsOpen = true;
        else if (!_model.ProxyLoginOpen && IsOpen) IsOpen = false;
    }

    private void Buttons()
    {
        _start.IsEnabled = _model.LoginProxy.Trim().Length > 0 && !_model.Starting;
        _start.IsLoading = _model.Starting;
    }
}
