using Astra.Api.Contracts;
using Astra.Desktop.Components.Clients;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Mcp;
using Astra.Desktop.Components.ProviderDetail;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Mcp;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;

namespace Astra.Desktop.Pages.Mcp;

/// <summary>
/// The MCP gateway's body (web <c>McpGateway</c>): the gateway group (switch, address, allowed origins), a row per upstream server with
/// its status and actions, and the recent calls. Each block redraws from its own query.
/// </summary>
public sealed class McpView : UserControl
{
    private readonly McpPage _page;
    private readonly QueryHost<McpSettingsDto> _gateway;
    private readonly QueryHost<List<McpServerDto>> _servers;
    private readonly QueryHost<List<McpCallDto>> _calls;
    private readonly QueryHost<List<ClientMcpDto>> _clients;
    private readonly QueryHost<List<LocalMcpServerDto>> _local;
    private string? _originsDraft;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public McpView(McpPage page)
    {
        _page = page;
        _gateway = new QueryHost<McpSettingsDto>(page.Api.Settings, RenderGateway);
        _servers = new QueryHost<List<McpServerDto>>(page.Api.Servers, RenderServers);
        _calls = new QueryHost<List<McpCallDto>>(page.Api.Calls, RenderCalls);
        var panel = new ClientMcpPanel(page.ClientApi, page.Feedback, () => page.Tokens.Data);
        _clients = new QueryHost<List<ClientMcpDto>>(page.ClientApi.Entries, panel.Render);
        var section = new LocalMcpSection(page.ClientApi, page.Feedback, () => this);
        _local = new QueryHost<List<LocalMcpServerDto>>(page.ClientApi.Servers, section.Render);
        var column = new StackPanel { Spacing = 20, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(_gateway);
        column.Children.Add(_servers);
        column.Children.Add(_clients);
        column.Children.Add(_local);
        column.Children.Add(_calls);
        Content = column;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.EditRequested += ShowSheet;
        _page.Tokens.PropertyChanged += OnTokens;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.EditRequested -= ShowSheet;
        _page.Tokens.PropertyChanged -= OnTokens;
        Loc.LanguageChanged -= OnLanguage;
    }

    // the token picker of "apply to clients" reads the tokens as they stand: draw it again when they arrive or change
    private void OnTokens(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(_clients.Rebuild);

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _gateway.Rebuild();
        _servers.Rebuild();
        _calls.Rebuild();
        _clients.Rebuild();
        _local.Rebuild();
    });

    private void ShowSheet(McpServerDto? server) => McpServerSheet.Show(this, _page.Api, _page.Feedback, server);

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

    private async Task<bool> CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return false;
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            return false;
        }
    }

    // ---------- gateway ----------

    private Control? RenderGateway(Query<McpSettingsDto> q)
    {
        if (q.Data is not { } s)
            return q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(2);
        var enable = PdUi.Toggle(s.Enabled, on => _ = GuardAsync(() => _page.Api.UpdateSettingsAsync(on, null)), Loc.T("mcp.gateway.enable"));
        var endpoint = ClientUi.HRow(6,
            PdUi.Text(s.Endpoint, 12, ArcDesignTokenKind.TextSecondary, mono: true),
            Ui.IconButton(LucideIconKind.Copy, Loc.T("mcp.gateway.copyEndpoint"), () => _ = CopyAsync(s.Endpoint).ContinueWith(async t =>
            {
                if (await t) _page.Feedback.Success(Loc.T("mcp.gateway.copied"));
            }, TaskScheduler.FromCurrentSynchronizationContext())));

        _originsDraft ??= string.Join('\n', s.AllowedOrigins);
        var origins = PdUi.Area(_originsDraft, v => _originsDraft = v, null, 72, mono: true, placeholder: "https://app.example.com");
        var saveOrigins = Ui.Button(Loc.T("mcp.gateway.originsSave"), () => _ = GuardAsync(async () =>
        {
            var saved = await _page.Api.UpdateSettingsAsync(null, McpApi.ParseOrigins(_originsDraft));
            _originsDraft = string.Join('\n', saved.AllowedOrigins);
            _page.Feedback.Success(Loc.T("common.saved"));
        }));
        return RowBuilder.Group(Loc.T("mcp.gateway.title"), null,
            RowBuilder.Hinted(Loc.T("mcp.gateway.enable"), Loc.T("mcp.gateway.enableHint"), enable),
            RowBuilder.Hinted(Loc.T("mcp.gateway.endpoint"), Loc.T("mcp.gateway.endpointHint"), endpoint),
            Ui.GroupLine(PdUi.Stack(8, PdUi.Field(Loc.T("mcp.gateway.origins"), origins, Loc.T("mcp.gateway.originsHint")), saveOrigins), 12, 12));
    }

    // ---------- servers ----------

    private Control? RenderServers(Query<List<McpServerDto>> q)
    {
        var title = PdUi.Text(Loc.T("mcp.servers.title"), 12, ArcDesignTokenKind.TextSecondary, Avalonia.Media.FontWeight.Medium);
        title.Margin = new Thickness(4, 0, 0, 0);
        Control body;
        if (!q.HasData)
            body = q.IsError ? ClientUi.Alert(AlertTone.Danger, Loc.T("common.error"), q.Error?.Message ?? "") : new LoadingState(3);
        else if (q.Data!.Count == 0)
            body = new CardPanel(new EmptyStateView(Loc.T("mcp.servers.emptyTitle"), Loc.T("mcp.servers.emptyDetail"), icon: new LucideIcon { Kind = LucideIconKind.Plug, Size = 24 }));
        else
            body = RowBuilder.Group(null, Loc.T("mcp.servers.localOnly"), q.Data!.Select(ServerRow).ToArray());
        return new StackPanel { Spacing = 8, Children = { title, body } };
    }

    private static Tone ToneOf(string status) => status switch
    {
        "ok" => Tone.Green,
        "needs_auth" => Tone.Orange,
        "auth_error" or "unreachable" or "version_unsupported" => Tone.Red,
        _ => Tone.Neutral,
    };

    private Control ServerRow(McpServerDto s)
    {
        var oauth = s.AuthType == "oauth";
        var authorized = s.OAuth?.Authorized == true;
        var label = ClientUi.HRow(8,
            PdUi.Text(s.Name, 13, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold),
            KitBadge.Create(Loc.T("mcp.status." + s.Status), ToneOf(s.Status), s.StatusMessage));
        if (!s.Enabled) label.Children.Add(KitBadge.Create(Loc.T("mcp.server.disabledBadge")));

        var meta = string.Join(" · ", new[]
        {
            s.Slug,
            s.ProtocolVersion is { Length: > 0 } v ? Loc.T("mcp.server.protocol", ("version", v)) : null,
            Loc.T("mcp.server.boundTokens", ("count", s.BoundTokens)),
            s.CheckedAt is { } at ? SettingsFormat.DateTime(at, Loc.Language) : null,
        }.Where(p => p is not null));
        var detail = new StackPanel { Spacing = 2 };
        detail.Children.Add(PdUi.Text(s.Url, 11.5, ArcDesignTokenKind.TextMuted, mono: true, wrap: true));
        detail.Children.Add(PdUi.Text(meta, 11.5, ArcDesignTokenKind.TextMuted, wrap: true));
        if (oauth && authorized && s.OAuth?.Issuer is { Length: > 0 } issuer)
            detail.Children.Add(PdUi.Text(Loc.T("mcp.server.authorizedBy", ("issuer", issuer)), 11.5, ArcDesignTokenKind.TextMuted, wrap: true));

        var actions = new List<Control>();
        if (oauth)
            actions.Add(Ui.Button(Loc.T(authorized ? "mcp.server.reauthorize" : "mcp.server.authorize"), () => _ = GuardAsync(() => AuthorizeAsync(s)),
                authorized ? ButtonVariant.Secondary : ButtonVariant.Primary, LucideIconKind.ShieldCheck));
        var test = Ui.Button(Loc.T("mcp.server.test"), () => { }, ButtonVariant.Secondary, LucideIconKind.Zap);
        test.Click += async (_, _) =>
        {
            test.IsLoading = true;
            try
            {
                await GuardAsync(() => TestAsync(s));
            }
            finally
            {
                test.IsLoading = false;
            }
        };
        actions.Add(test);
        var items = new List<Ui.MenuItem> { new(Loc.T("common.edit"), LucideIconKind.Pencil, () => _page.BeginEdit(s)) };
        if (oauth && authorized) items.Add(new(Loc.T("mcp.server.forget"), LucideIconKind.KeyRound, () => _ = GuardAsync(() => ForgetAsync(s))));
        items.Add(new(Loc.T("common.delete"), LucideIconKind.Trash2, () => _ = GuardAsync(() => DeleteAsync(s)), Destructive: true, SeparatorBefore: true));
        actions.Add(Ui.Menu(s.Name, LucideIconKind.Ellipsis, items));
        return RowBuilder.Row(label, detail, actions.ToArray());
    }

    private async Task TestAsync(McpServerDto s)
    {
        var result = await _page.Api.TestAsync(s.Id);
        if (!result.Ok)
        {
            _page.Feedback.Error(Loc.T("mcp.server.testFailed", ("message", result.Message ?? result.Status)));
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        foreach (var tool in result.Tools) list.Children.Add(PdUi.Text(tool, 12, ArcDesignTokenKind.TextSecondary, mono: true));
        var close = Ui.Button(Loc.T("common.close"), () => { });
        var sheet = new Sheet
        {
            Title = Loc.T("mcp.server.testToolsTitle", ("name", s.Name)),
            Description = Loc.T("mcp.server.testOk", ("count", result.ToolCount)),
            Content = new ScrollViewer { MaxHeight = 360, Content = list },
            Footer = [close],
        };
        close.Click += (_, _) => sheet.Close();
        _ = sheet.ShowAsync(this);
    }

    private async Task AuthorizeAsync(McpServerDto s)
    {
        var started = await _page.Api.StartOAuthAsync(s.Id);
        if (started.AuthorizationUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher) await launcher.LaunchUriAsync(uri);
            _page.Feedback.Info(Loc.T("mcp.server.authorizeOpened"));
        }
        else
            _page.Feedback.Info(started.Message ?? Loc.T("mcp.server.authorizeNotNeeded"));
    }

    private async Task ForgetAsync(McpServerDto s)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(Loc.T("mcp.server.forgetConfirm", ("name", s.Name)), Loc.T("mcp.server.forgetDetail"), Loc.T("mcp.server.forget"), Destructive: true))) return;
        await _page.Api.ForgetOAuthAsync(s.Id);
        _page.Feedback.Success(Loc.T("mcp.server.forgotten"));
    }

    private async Task DeleteAsync(McpServerDto s)
    {
        if (!await _page.Feedback.ConfirmAsync(new Astra.Desktop.Core.Shell.ConfirmRequest(Loc.T("mcp.server.deleteConfirm", ("name", s.Name)), Loc.T("mcp.server.deleteDetail"), Loc.T("common.delete"), Destructive: true))) return;
        await _page.Api.DeleteAsync(s.Id);
        _page.Feedback.Success(Loc.T("mcp.server.deleted", ("name", s.Name)));
    }

    // ---------- calls ----------

    private Control? RenderCalls(Query<List<McpCallDto>> q)
    {
        if (!q.HasData) return q.IsError ? null : new LoadingState(2);
        var calls = q.Data!;
        Control[] rows = calls.Count == 0
            ? [Ui.GroupNote(Loc.T("mcp.calls.empty"))]
            : calls.Select(CallRow).ToArray();
        return RowBuilder.Group(Loc.T("mcp.calls.title"), Loc.T("mcp.calls.hint"), rows);
    }

    private static Control CallRow(McpCallDto c)
    {
        var tool = ClientUi.HRow(6, PdUi.Text($"{c.ServerSlug}/{c.Tool}", 12.5, ArcDesignTokenKind.Foreground, mono: true));
        if (c.FrontEra is { Length: > 0 } era) tool.Children.Add(PdUi.Text(Loc.T("mcp.calls.era." + era), 10.5, ArcDesignTokenKind.TextMuted));
        var detail = $"{SettingsFormat.DateTime(c.At, Loc.Language)} · {c.TokenName ?? c.TokenId}";
        var result = c.Ok
            ? KitBadge.Create(Loc.T("mcp.calls.ok"), Tone.Green)
            : KitBadge.Create(Loc.T("mcp.calls.error." + (c.ErrorCategory ?? "protocol")), Tone.Red);
        return RowBuilder.Row(tool, detail, PdUi.Text(c.DurationMs + " ms", 11.5, ArcDesignTokenKind.TextMuted), result);
    }
}
