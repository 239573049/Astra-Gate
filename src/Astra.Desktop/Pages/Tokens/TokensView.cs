using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>
/// The token list's body (web <c>TokensPage</c> markup): the address hints, then a card of rows — loading, error, empty or one row per
/// token — and the create / settings sheets the page asks for. A row is a summary line (name, badges, masked key and its copy buttons,
/// the clients, today / total / last used, the actions menu) that opens into the details.
/// </summary>
public sealed class TokensView : UserControl
{
    private readonly TokensPage _page;
    private readonly QueryHost<SettingsDto> _hints;
    private readonly QueryHost<List<TokenDto>> _list;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public TokensView(TokensPage page)
    {
        _page = page;
        _hints = new QueryHost<SettingsDto>(page.Settings, RenderHints);
        _list = new QueryHost<List<TokenDto>>(page.Tokens, RenderList);
        // web mx-auto max-w-[1100px]: stretched to the content width up to 1100, centred beyond (Center would shrink it to its content).
        var column = new StackPanel { Spacing = 16, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        column.Children.Add(_hints);
        column.Children.Add(_list);
        Content = column;
    }

    private Translate T => (key, args) => Loc.T(key, args);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.CreateRequested += ShowCreate;
        _page.SettingsRequested += ShowSettings;
        _page.Tunnel.PropertyChanged += OnRelated;
        _page.Providers.PropertyChanged += OnRelated;
        _page.Settings.PropertyChanged += OnRelated;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.CreateRequested -= ShowCreate;
        _page.SettingsRequested -= ShowSettings;
        _page.Tunnel.PropertyChanged -= OnRelated;
        _page.Providers.PropertyChanged -= OnRelated;
        _page.Settings.PropertyChanged -= OnRelated;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnRelated(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Both read only the related queries' data; a fetch's other notifications (fetching, status…) would rebuild the list for nothing.
        if (e.PropertyName != "Data") return;
        _hints.Rebuild();
        _list.Rebuild();
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _hints.Rebuild();
        _list.Rebuild();
    });

    // ---------- hints ----------

    /// <summary>The address hints: where direct calls go, where a key holder sees their own usage, and the same for the remote tunnel.</summary>
    private Control? RenderHints(Query<SettingsDto> q)
    {
        if (q.Data is not { } s) return null;
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(4, 0) };
        stack.Children.Add(Hint(Loc.T("tokens.directHint", ("url", TokenPresentation.V1Url(s.GatewayBaseUrl))), false, false));
        stack.Children.Add(Hint(Loc.T("tokens.selfServiceHint", ("url", s.GatewayBaseUrl)), true, false));
        if (_page.RemoteUrl is { } remote)
            stack.Children.Add(Hint(Loc.T("tokens.remoteHint", ("url", remote + "/v1"), ("key", remote + "/key")), true, true));
        return stack;
    }

    private static Control Hint(string text, bool selectable, bool accent)
    {
        var block = selectable ? new SelectableTextBlock() : new TextBlock();
        block.Text = text;
        block.TextWrapping = TextWrapping.Wrap;
        block.FontSize = KitStyle.Type12;
        block.Token(TextBlock.ForegroundProperty, accent ? ArcDesignTokenKind.Accent : ArcDesignTokenKind.TextSecondary);
        return block;
    }

    // ---------- list ----------

    private Control? RenderList(Query<List<TokenDto>> q)
    {
        if (!q.HasData)
            return q.IsError
                ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message ?? "" }
                : new LoadingState(4);
        var tokens = q.Data!;
        if (tokens.Count == 0)
            return new CardPanel(new EmptyStateView(Loc.T("tokens.empty"), icon: new LucideIcon { Kind = LucideIconKind.KeyRound, Size = 24 }));
        var rows = new StackPanel();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                var line = new Border { Height = 0.5 };
                line.Token(Border.BackgroundProperty, ArcDesignTokenKind.BorderSubtle);
                rows.Children.Add(line);
            }
            rows.Children.Add(Row(tokens[i], tokens.Count));
        }
        return new CardPanel(rows);
    }

    private Control Row(TokenDto token, int count)
    {
        var providers = _page.Providers.Data ?? [];
        var zone = TimeSpan.FromMinutes(_page.Now.OffsetMinutes);
        var open = _page.IsOpen(token.Id, count);

        // ----- summary line -----
        var chevron = new LucideIcon { Kind = LucideIconKind.ChevronRight, Size = 16 };
        chevron.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        var toggle = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = chevron, Padding = new Thickness(2), MinWidth = 0, MinHeight = 0, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        Avalonia.Automation.AutomationProperties.SetName(toggle, Loc.T("tokens.details"));

        var name = KitStyle.Text(token.Name, ArcDesignTokenKind.TypeBase, token.Enabled ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextMuted, FontWeight.Medium);
        name.FontSize = 14;
        var titleRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
        titleRow.Children.Add(name);
        if (token.IsDefault) titleRow.Children.Add(KitBadge.Create(Loc.T("tokens.default"), Tone.Accent));
        if (!token.Enabled) titleRow.Children.Add(KitBadge.Create(Loc.T("tokens.disabledBadge"), Tone.Orange));
        if (token.LimitUsage.Any(u => u.Exhausted)) titleRow.Children.Add(KitBadge.Create(Loc.T("tokens.limitExhausted"), Tone.Orange));
        if (token.Pool is { } pool)
        {
            var poolRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            poolRow.Children.Add(KitBadge.Create(TokenPresentation.PoolBadge(pool, T), Tone.Neutral));
            foreach (var m in pool.Members) poolRow.Children.Add(new StatusDot(TokenPoolEditor.ToTone(PoolDrafts.ToneOf(m.Health.State))));
            ToolTip.SetTip(poolRow, string.Join(", ", pool.Members.Select(m => m.ProviderId)));
            titleRow.Children.Add(poolRow);
        }

        var key = new SelectableTextBlock { Text = token.KeyMasked, FontSize = KitStyle.Type12, VerticalAlignment = VerticalAlignment.Center };
        key.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        var settings = _page.Settings.Data;
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2, 0, 0) };
        keyRow.Children.Add(key);
        ArcButton? copyKey = null;
        copyKey = CopyButton(LucideIconKind.Copy, Loc.T("tokens.copy"), true, async () =>
        {
            // web: loading={reveal.isPending} while the plaintext key is fetched
            if (copyKey!.IsLoading) return;
            copyKey.IsLoading = true;
            try { await _page.Actions.CopyKeyAsync(token.Id, CopyToClipboard); }
            finally { copyKey.IsLoading = false; }
        });
        keyRow.Children.Add(copyKey);
        // The two static values go through Arc's CopyButton (it confirms in place); the key itself cannot, it is fetched when asked for.
        if (settings is not null)
        {
            keyRow.Children.Add(StaticCopy(TokenPresentation.V1Url(settings.GatewayBaseUrl), Loc.T("tokens.copyUrlShort"), Loc.T("tokens.copyUrl")));
            keyRow.Children.Add(StaticCopy(TokenPresentation.JoinCommand(settings.GatewayBaseUrl), Loc.T("tokens.copyJoinShort"), Loc.T("tokens.copyJoin")));
        }

        var left = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, MinWidth = 224 };
        Grid.SetColumn(toggle, 0);
        var texts = new StackPanel { Children = { titleRow, keyRow } };
        Grid.SetColumn(texts, 1);
        left.Children.Add(toggle);
        left.Children.Add(texts);

        var clients = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Width = 64, VerticalAlignment = VerticalAlignment.Center };
        if (token.Clients.Count == 0) clients.Children.Add(KitStyle.Text(StatsFormat.Dash, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        else foreach (var kind in token.Clients) clients.Children.Add(BrandIcon.Client(kind, 16, glyph: true));
        ToolTip.SetTip(clients, TokenPresentation.ClientsTitle(token.Clients, T));

        var lastUsedFull = token.LastUsedAt is { } at ? StatsFormat.DateTime(at, zone, Loc.Language) : Loc.T("tokens.neverUsed");
        var today = Summary(Loc.T("tokens.today"), StatsFormat.Usd(token.Today.CostUsd), Loc.T("tokens.requestsContext", ("count", StatsFormat.Tokens(token.Today.Requests))), null);
        var total = Summary(Loc.T("tokens.total"), StatsFormat.Usd(token.Total.CostUsd), StatsFormat.Tokens(token.Total.TotalTokens, true) + " Token", null);
        var last = Summary(Loc.T("tokens.lastUsedTitle"), token.LastUsedAt is { } l ? StatsFormat.Time(l, _page.Clock, zone, Loc.Language) : StatsFormat.Dash,
            token.LastUsedAt is null ? Loc.T("tokens.neverUsed") : "", lastUsedFull);

        var line = new Grid { ColumnSpacing = 20, Margin = new Thickness(16, 12) };
        line.ColumnDefinitions = new ColumnDefinitions("*,Auto,96,96,96,Auto");
        var cells = new Control[] { left, clients, today, total, last, ActionsMenu(token) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            cells[i].VerticalAlignment = i == 0 ? VerticalAlignment.Top : VerticalAlignment.Center;
            line.Children.Add(cells[i]);
        }

        // ----- details -----
        var detail = Details(token, providers, zone, lastUsedFull);
        detail.IsVisible = open;
        chevron.RenderTransform = open ? new RotateTransform(90) : null;

        void SetOpen(bool value)
        {
            _page.SetOpen(token.Id, value);
            detail.IsVisible = value;
            chevron.RenderTransform = value ? new RotateTransform(90) : null;
        }

        toggle.Click += (_, _) => SetOpen(!detail.IsVisible);
        var summary = new Border { Child = line, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        // The whole line toggles; presses the buttons and the menu handled themselves do not reach this handler.
        summary.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(summary).Properties.IsLeftButtonPressed) SetOpen(!detail.IsVisible);
        };
        summary.PointerEntered += (_, _) => summary.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        summary.PointerExited += (_, _) => summary.Background = Brushes.Transparent;
        return new StackPanel { Children = { summary, detail } };
    }

    private static TKCode.Arc.CopyButton StaticCopy(string value, string label, string tip)
    {
        var button = new TKCode.Arc.CopyButton { Value = value, Label = label, Margin = new Thickness(4, 0, 0, 0) };
        ToolTip.SetTip(button, tip);
        Avalonia.Automation.AutomationProperties.SetName(button, tip);
        return button;
    }

    private static ArcButton CopyButton(LucideIconKind icon, string label, bool enabled, Action click)
    {
        var button = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = new LucideIcon { Kind = icon, Size = 14 }, IsEnabled = enabled, Padding = new Thickness(4), MinWidth = 0, MinHeight = 0, Width = 24, Height = 24 };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>One fixed-width summary column of a collapsed row, so the columns line up across tokens (web <c>Summary</c>).</summary>
    private static Control Summary(string label, string value, string context, string? tip)
    {
        var l = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        l.FontSize = 11;
        var v = KitStyle.Text(value, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        v.FontSize = 14;
        var c = KitStyle.Text(context.Length == 0 ? " " : context, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        c.FontSize = 11;
        var box = new StackPanel { Width = 96, Children = { l, v, c } };
        if (tip is not null) ToolTip.SetTip(box, tip);
        return box;
    }

    private Control ActionsMenu(TokenDto token)
    {
        var menu = new DropdownMenu { Label = Loc.T("common.more"), Icon = Lucide.Geometry(LucideIconKind.Ellipsis) };
        MenuEntry Entry(string header, LucideIconKind icon, Action run, bool destructive = false, bool separator = false)
        {
            var entry = new MenuEntry { Header = header, Icon = new LucideIcon { Kind = icon, Size = 14 }, IsDestructive = destructive, HasSeparatorBefore = separator };
            entry.Click += (_, _) => run();
            return entry;
        }
        menu.Items.Add(Entry(Loc.T("tokens.settings"), LucideIconKind.Pencil, () => _page.BeginEdit(token)));
        menu.Items.Add(Entry(token.Enabled ? Loc.T("tokens.disable") : Loc.T("tokens.enable"), LucideIconKind.Power, () => _ = _page.Actions.ToggleAsync(token)));
        menu.Items.Add(Entry(Loc.T("tokens.reset"), LucideIconKind.RotateCcw, () => _ = _page.Actions.ResetAsync(token)));
        if (!token.IsDefault) menu.Items.Add(Entry(Loc.T("tokens.delete"), LucideIconKind.Trash2, () => _ = _page.Actions.DeleteAsync(token), destructive: true, separator: true));
        return menu;
    }

    private Control Details(TokenDto token, IReadOnlyList<ProviderDto> providers, TimeSpan zone, string lastUsedFull)
    {
        var dl = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 32, LineSpacing = 8 };

        // clients
        var clientNames = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 4 };
        if (token.Clients.Count == 0) clientNames.Children.Add(Muted12(Loc.T("tokens.noClients"), true));
        else
            foreach (var kind in token.Clients)
                clientNames.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4,
                    Children = { BrandIcon.Client(kind, 14, glyph: true), Muted12(ClientCatalog.Name(kind), true) },
                });
        dl.Children.Add(Pair(Loc.T("tokens.clients"), clientNames));

        // pool / direct provider
        if (token.Pool is { } pool)
        {
            var members = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 4 };
            foreach (var m in TokenPresentation.PoolMembers(pool, providers, _page.Clock, zone, Loc.Language, T))
            {
                var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new StatusDot(TokenPoolEditor.ToTone(m.Tone)), Muted12(m.Name, true) } };
                ToolTip.SetTip(item, m.HealthText);
                members.Children.Add(item);
            }
            dl.Children.Add(Pair(Loc.T("tokens.pool.title"), members));
        }
        else dl.Children.Add(Pair(Loc.T("tokens.directProvider"), Muted12(TokenPresentation.DirectProviderText(token, providers, T), true)));

        dl.Children.Add(Pair(Loc.T("tokens.allowedClients"), Muted12(TokenPresentation.AllowedClientsText(token, T), true)));
        dl.Children.Add(Pair(Loc.T("tokens.lastUsedTitle"), Muted12(lastUsedFull, true)));

        var box = new StackPanel { Spacing = 0 };
        box.Children.Add(dl);
        box.Children.Add(Limits(token, zone));
        var stats = StatsTableView.Create(TokenPresentation.StatsTable(token, T));
        stats.Margin = new Thickness(0, 12, 0, 0);
        box.Children.Add(stats);

        var open = new ArcButton { Variant = ButtonVariant.Secondary, Size = ControlSize.Small, Content = IconText(LucideIconKind.ChartColumn, Loc.T("tokens.detail.open")) };
        open.Click += (_, _) => _page.OpenDetail(token.Id);
        var logs = new ArcButton { Variant = ButtonVariant.Secondary, Size = ControlSize.Small, Content = IconText(LucideIconKind.ScrollText, Loc.T("tokens.detail.viewLogs")) };
        logs.Click += (_, _) => _page.OpenLogs(token.Id);
        box.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 12, 0, 0), Children = { open, logs } });

        var panel = new Border { Padding = new Thickness(40, 12, 16, 12), Child = box };
        CardPanel.Themed(panel, CardPanel.SurfaceMutedBrush);
        var top = new Border { BorderThickness = new Thickness(0, 0.5, 0, 0), Child = panel };
        top.Token(Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
        return top;
    }

    private static Control IconText(LucideIconKind icon, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { new LucideIcon { Kind = icon, Size = 14 }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
    };

    private static Control Pair(string label, Control value)
    {
        var l = KitStyle.Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        l.FontSize = KitStyle.Type12;
        value.Margin = new Thickness(0, 2, 0, 0);
        return new StackPanel { MinWidth = 0, Children = { l, value } };
    }

    private static TextBlock Muted12(string text, bool secondary)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, secondary ? ArcDesignTokenKind.TextSecondary : ArcDesignTokenKind.TextMuted);
        block.FontSize = KitStyle.Type12;
        return block;
    }

    /// <summary>The token's caps: one bar per budget plus the RPM window; "unlimited" without any (web <c>LimitsSummary</c>).</summary>
    private Control Limits(TokenDto token, TimeSpan zone)
    {
        var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        box.Children.Add(Muted12(Loc.T("tokens.limits"), false));
        if (TokenPresentation.IsUnlimited(token))
        {
            var text = Muted12(Loc.T("tokens.unlimited"), true);
            text.Margin = new Thickness(0, 2, 0, 0);
            box.Children.Add(text);
            return box;
        }
        // web: grid gap-3 sm:grid-cols-2, rows as tall as their own content (a UniformGrid gave the RPM line a bar's height).
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, RowSpacing = 12, Margin = new Thickness(0, 6, 0, 0) };
        var items = new List<Control>();
        foreach (var usage in token.LimitUsage) items.Add(LimitBarView.Create(TokenPresentation.LimitBar(usage, zone, Loc.Language, T)));
        if (TokenPresentation.RpmText(token, T) is { } rpm)
        {
            var text = KitStyle.Text(rpm, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            text.FontSize = 11.5;
            text.VerticalAlignment = VerticalAlignment.Top;
            items.Add(text);
        }
        for (var i = 0; i < items.Count; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(items[i], i / 2);
            Grid.SetColumn(items[i], i % 2);
            grid.Children.Add(items[i]);
        }
        box.Children.Add(grid);
        return box;
    }

    // ---------- clipboard ----------

    private async Task<bool> CopyToClipboard(string text)
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

    // ---------- sheets ----------

    private void ShowCreate() => TokenSheets.ShowCreate(this, _page, _page.Providers.Data ?? []);

    private void ShowSettings(TokenDto token) => TokenSheets.ShowSettings(this, _page, token, _page.Providers.Data ?? []);
}
